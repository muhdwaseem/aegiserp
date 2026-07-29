using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a sales invoice from the UI.</summary>
public record InvoiceLineInput(string Description, int RevenueAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate, int? ItemId = null);

public class SalesInvoiceService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public SalesInvoiceService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<SalesInvoice>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer).Include(i => i.Lines).Include(i => i.JournalVoucher)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>
    /// Every invoice for the invoice list page, each with its Zoho-style display status
    /// (Draft / Pending / Overdue / Paid / Void) and remaining balance computed from posted
    /// receipts and credit notes applied against it.
    /// </summary>
    public async Task<List<SalesInvoiceRow>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = await db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer).Include(i => i.Lines).Include(i => i.JournalVoucher)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .ToListAsync();

        var receipts = await db.CustomerReceipts.AsNoTracking()
            .Where(r => r.Status == VoucherStatus.Posted && r.SalesInvoiceId != null)
            .ToListAsync();
        var credits = await db.CreditNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => n.Status == VoucherStatus.Posted && n.SalesInvoiceId != null)
            .ToListAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return invoices.Select(i =>
        {
            var allocated = receipts.Where(r => r.SalesInvoiceId == i.Id).Sum(r => r.Amount)
                          + credits.Where(n => n.SalesInvoiceId == i.Id).Sum(n => n.TotalGross);
            var balance = i.TotalGross - allocated;
            var status = i.Status switch
            {
                VoucherStatus.Draft => ArStatus.Draft,
                VoucherStatus.Void => ArStatus.Void,
                _ => balance <= 0 ? ArStatus.Paid : i.DueDate < today ? ArStatus.Overdue : ArStatus.Pending,
            };
            return new SalesInvoiceRow(i, balance, status);
        }).ToList();
    }

    /// <summary>
    /// The revenue account used the last time this item was billed on a posted sales invoice,
    /// so the invoice line editor can pre-fill it instead of asking the user to pick it again.
    /// </summary>
    public async Task<int?> GetLastRevenueAccountForItemAsync(int itemId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalesInvoices.AsNoTracking()
            .SelectMany(i => i.Lines, (i, l) => new { i.Date, i.Id, l.ItemId, l.RevenueAccountId })
            .Where(x => x.ItemId == itemId)
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
            .Select(x => (int?)x.RevenueAccountId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Next invoice number, scoped to the SalesInvoices table itself (not the JournalVoucher
    /// sequence) so a still-unposted draft's number can never collide with — or be reused by —
    /// an invoice created directly through <see cref="CreateAndPostAsync"/>.
    /// </summary>
    private static async Task<string> NextInvoiceNoAsync(AegisDbContext db, int year)
    {
        var existing = await db.SalesInvoices.Select(i => i.InvoiceNo).ToListAsync();
        return JournalPoster.NextDocNo(existing, "INV", year);
    }

    /// <summary>
    /// Saves a sales invoice as a Draft: no GL voucher is generated and nothing touches the
    /// ledger. The invoice number is reserved up front so it stays the same when later posted.
    /// </summary>
    public async Task<SalesInvoice> CreateDraftAsync(
        int customerId, DateOnly date, int fiscalPeriodId, string? narration,
        string createdBy, IEnumerable<InvoiceLineInput> lines, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        var invoice = new SalesInvoice
        {
            InvoiceNo = await NextInvoiceNoAsync(db, date.Year),
            CustomerId = customerId,
            Date = date,
            DueDate = date.AddDays(customer.PaymentTermsDays),
            FiscalPeriodId = fiscalPeriodId,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Sales invoice — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            Status = VoucherStatus.Draft,
        };

        var no = 1;
        foreach (var l in lines)
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNo = no++,
                Description = l.Description,
                ItemId = l.ItemId,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
            });
        if (invoice.Lines.Count == 0)
            throw new PostingException("Invoice needs at least one line.");

        db.SalesInvoices.Add(invoice);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return invoice;
    }

    /// <summary>Posts a previously-saved draft, generating its GL voucher under the same invoice number.</summary>
    public async Task<SalesInvoice> PostDraftAsync(int invoiceId, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var invoice = await db.SalesInvoices.Include(i => i.Lines).Include(i => i.Customer)
            .FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        if (invoice.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft invoices can be posted.");

        invoice.Post(nowUtc); // domain validation (positive totals, valid lines, due date)

        var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
        var vat = invoice.TotalVat > 0 ? await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatPayable) : null;

        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.SalesInvoice, invoice.InvoiceNo, invoice.Date, invoice.FiscalPeriodId,
            invoice.Narration, invoice.InvoiceNo, postedBy, BuildVoucherLines(invoice, ar.Id, vat?.Id), nowUtc);

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }

    /// <summary>Voids a draft invoice. Posted invoices already hit the ledger — reverse those with a Credit Note instead.</summary>
    public async Task VoidDraftAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        if (invoice.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft invoices can be voided — a posted invoice already hit the ledger; reverse it with a Credit Note instead.");

        invoice.Status = VoucherStatus.Void;
        await db.SaveChangesAsync();
    }

    /// <summary>Dr AR for the gross, Cr revenue per line, Cr VAT for the total tax.</summary>
    private static List<VoucherLineInput> BuildVoucherLines(SalesInvoice invoice, int arAccountId, int? vatAccountId)
    {
        var lines = new List<VoucherLineInput>
        {
            new(arAccountId, null, $"{invoice.Customer.Name} — {invoice.InvoiceNo}", invoice.TotalGross, 0),
        };
        // Skip zero-net lines (e.g. complimentary items) — the invoice still records them,
        // but a zero GL line would fail the double-entry rules.
        lines.AddRange(invoice.Lines.Where(l => l.Net > 0).Select(l =>
            new VoucherLineInput(l.RevenueAccountId, l.CostCenterId, l.Description, 0, l.Net)));
        if (invoice.TotalVat > 0 && vatAccountId is int vatId)
            lines.Add(new VoucherLineInput(vatId, null, $"Output VAT — {invoice.InvoiceNo}", 0, invoice.TotalVat));
        return lines;
    }

    /// <summary>
    /// Creates and posts a sales invoice in one transaction: the invoice, its lines and the
    /// generated GL voucher (Dr AR gross / Cr revenue net per line / Cr VAT payable) are
    /// persisted atomically. Invoice number and voucher number are the same document number.
    /// </summary>
    public async Task<SalesInvoice> CreateAndPostAsync(
        int customerId, DateOnly date, int fiscalPeriodId, string? narration,
        string createdBy, IEnumerable<InvoiceLineInput> lines, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        var invoiceNo = await NextInvoiceNoAsync(db, date.Year);

        var invoice = new SalesInvoice
        {
            InvoiceNo = invoiceNo,
            CustomerId = customerId,
            Date = date,
            DueDate = date.AddDays(customer.PaymentTermsDays),
            FiscalPeriodId = fiscalPeriodId,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Sales invoice — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            Customer = customer,
        };

        var no = 1;
        foreach (var l in lines)
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNo = no++,
                Description = l.Description,
                ItemId = l.ItemId,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
            });

        invoice.Post(nowUtc); // domain validation (positive totals, valid lines, due date)

        var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
        var vat = invoice.TotalVat > 0 ? await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatPayable) : null;

        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.SalesInvoice, invoiceNo, date, fiscalPeriodId,
            invoice.Narration, invoiceNo, createdBy, BuildVoucherLines(invoice, ar.Id, vat?.Id), nowUtc);

        db.SalesInvoices.Add(invoice);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }
}
