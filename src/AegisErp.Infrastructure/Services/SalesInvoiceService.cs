using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a sales invoice from the UI.</summary>
public record InvoiceLineInput(string Description, int RevenueAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate, int? ItemId = null,
    decimal DiscountPercent = 0, string? Uom = null);

public class SalesInvoiceService
{
    /// <summary>Hard cap on an attached document's size (5 MB) — stored inline in the database.</summary>
    public const int MaxAttachmentBytes = 5 * 1024 * 1024;

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

        // Derived from ReceiptAllocation (joined through the invoice line it targets), never from
        // CustomerReceipt.SalesInvoiceId — that FK can't represent a receipt spanning multiple
        // invoices (it's null in that case even though the receipt did settle part of this one).
        var allocatedByInvoice = (await db.ReceiptAllocations.AsNoTracking()
            .Where(a => a.CustomerReceipt.Status == VoucherStatus.Posted)
            .Select(a => new { InvoiceId = a.SalesInvoiceLine.SalesInvoiceId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.InvoiceId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var credits = await db.CreditNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => n.Status == VoucherStatus.Posted && n.SalesInvoiceId != null)
            .ToListAsync();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return invoices.Select(i =>
        {
            var allocated = allocatedByInvoice.GetValueOrDefault(i.Id)
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

    /// <summary>One invoice with full detail (Customer, Lines with Item, JournalVoucher) for the detail page.</summary>
    public async Task<SalesInvoice?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer).Include(i => i.Lines).ThenInclude(l => l.Item).Include(i => i.JournalVoucher)
            .FirstOrDefaultAsync(i => i.Id == id);
    }

    /// <summary>
    /// Per-line paid/balance breakdown for one invoice, from posted receipt allocations — so
    /// staff can see which specific service on a multi-line invoice is settled vs still owing.
    /// </summary>
    public async Task<List<SalesInvoiceLineBalance>> GetLineBalancesAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var lines = await db.SalesInvoiceLines.AsNoTracking().Include(l => l.Item)
            .Where(l => l.SalesInvoiceId == invoiceId)
            .OrderBy(l => l.LineNo)
            .ToListAsync();

        var lineIds = lines.Select(l => l.Id).ToList();
        var allocated = (await db.ReceiptAllocations.AsNoTracking()
            .Where(a => lineIds.Contains(a.SalesInvoiceLineId) && a.CustomerReceipt.Status == VoucherStatus.Posted)
            .Select(a => new { a.SalesInvoiceLineId, a.Amount })
            .ToListAsync())
            .GroupBy(a => a.SalesInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

        return lines.Select(l => new SalesInvoiceLineBalance(
            l.Id, l.Description, l.Item?.Name, l.Gross, allocated.GetValueOrDefault(l.Id))).ToList();
    }

    /// <summary>
    /// Total posted credit notes against this invoice — credit notes reduce the invoice-level
    /// balance but, unlike receipts, aren't yet attributed to a specific line (see
    /// <see cref="GetLineBalancesAsync"/>'s doc comment), so callers use this to show a
    /// disclaimer rather than silently showing per-line totals that don't reconcile to the header.
    /// </summary>
    public async Task<decimal> GetCreditedTotalAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return (await db.CreditNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => n.SalesInvoiceId == invoiceId && n.Status == VoucherStatus.Posted)
            .ToListAsync()).Sum(n => n.TotalGross);
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
        string createdBy, IEnumerable<InvoiceLineInput> lines, DateTime nowUtc,
        string? customerPoNo = null, string? deliveryNoteRef = null, string? salesOrderRef = null,
        string? notes = null)
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
            CustomerPoNo = string.IsNullOrWhiteSpace(customerPoNo) ? null : customerPoNo.Trim(),
            DeliveryNoteRef = string.IsNullOrWhiteSpace(deliveryNoteRef) ? null : deliveryNoteRef.Trim(),
            SalesOrderRef = string.IsNullOrWhiteSpace(salesOrderRef) ? null : salesOrderRef.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
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
                DiscountPercent = l.DiscountPercent,
                Uom = l.Uom,
            });
        if (invoice.Lines.Count == 0)
            throw new PostingException("Invoice needs at least one line.");

        db.SalesInvoices.Add(invoice);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return invoice;
    }

    /// <summary>
    /// Replaces a draft's header fields and lines in place — the invoice number and Draft status
    /// are untouched. Only ever valid while the invoice is still Draft; once posted, an invoice
    /// is immutable and must be reversed with a Credit Note instead of edited.
    /// </summary>
    public async Task<SalesInvoice> UpdateDraftAsync(
        int invoiceId, int customerId, DateOnly date, int fiscalPeriodId, string? narration,
        IEnumerable<InvoiceLineInput> lines,
        string? customerPoNo = null, string? deliveryNoteRef = null, string? salesOrderRef = null,
        string? notes = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        var invoice = await db.SalesInvoices.Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");
        if (invoice.Status != VoucherStatus.Draft)
            throw new PostingException("Only draft invoices can be edited — a posted invoice already hit the ledger; reverse it with a Credit Note instead.");

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        invoice.CustomerId = customerId;
        invoice.Date = date;
        invoice.DueDate = date.AddDays(customer.PaymentTermsDays);
        invoice.FiscalPeriodId = fiscalPeriodId;
        invoice.Narration = string.IsNullOrWhiteSpace(narration) ? $"Sales invoice — {customer.Name}" : narration;
        invoice.CustomerPoNo = string.IsNullOrWhiteSpace(customerPoNo) ? null : customerPoNo.Trim();
        invoice.DeliveryNoteRef = string.IsNullOrWhiteSpace(deliveryNoteRef) ? null : deliveryNoteRef.Trim();
        invoice.SalesOrderRef = string.IsNullOrWhiteSpace(salesOrderRef) ? null : salesOrderRef.Trim();
        invoice.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

        invoice.Lines.Clear();
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
                DiscountPercent = l.DiscountPercent,
                Uom = l.Uom,
            });
        if (invoice.Lines.Count == 0)
            throw new PostingException("Invoice needs at least one line.");

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

        invoice.Post(postedBy, nowUtc); // domain validation (positive totals, valid lines, due date, approval gate)

        var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
        var vat = invoice.TotalVat > 0 ? await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatPayable) : null;

        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.SalesInvoice, invoice.InvoiceNo, invoice.Date, invoice.FiscalPeriodId,
            invoice.Narration, invoice.InvoiceNo, postedBy, BuildVoucherLines(invoice, ar.Id, vat?.Id), nowUtc);

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }

    /// <summary>Voids a draft invoice. Posted invoices already hit the ledger — reverse those with a Credit Note instead.</summary>
    public async Task VoidDraftAsync(int invoiceId, string voidedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.Void(voidedBy, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>Submits a draft invoice for approval. Posting is blocked while pending.</summary>
    public async Task SubmitForApprovalAsync(int invoiceId, string submittedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.SubmitForApproval(submittedBy, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>Approves a pending invoice, clearing the block on posting.</summary>
    public async Task ApproveAsync(int invoiceId, string approvedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.Approve(approvedBy, nowUtc);
        await db.SaveChangesAsync();
    }

    /// <summary>Rejects a pending invoice. It stays a Draft, editable and re-submittable.</summary>
    public async Task RejectAsync(int invoiceId, string rejectedBy, DateTime nowUtc, string? note)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.Reject(rejectedBy, nowUtc, note);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Attaches (or replaces) the invoice's single supporting document. Allowed regardless of the
    /// invoice's posting status — attaching a document doesn't touch financial data.
    /// </summary>
    public async Task SetAttachmentAsync(int invoiceId, string fileName, string contentType, byte[] data)
    {
        if (data.Length > MaxAttachmentBytes)
            throw new PostingException($"Attachment is too large — the limit is {MaxAttachmentBytes / (1024 * 1024)} MB.");

        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.AttachmentFileName = fileName;
        invoice.AttachmentContentType = contentType;
        invoice.AttachmentData = data;
        await db.SaveChangesAsync();
    }

    public async Task RemoveAttachmentAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.FirstOrDefaultAsync(i => i.Id == invoiceId)
            ?? throw new PostingException("Invoice not found.");

        invoice.AttachmentFileName = null;
        invoice.AttachmentContentType = null;
        invoice.AttachmentData = null;
        await db.SaveChangesAsync();
    }

    public async Task<(string FileName, string ContentType, byte[] Data)?> GetAttachmentAsync(int invoiceId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoice = await db.SalesInvoices.AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId);
        if (invoice?.AttachmentData is null || invoice.AttachmentFileName is null) return null;
        return (invoice.AttachmentFileName, invoice.AttachmentContentType ?? "application/octet-stream", invoice.AttachmentData);
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
        string createdBy, IEnumerable<InvoiceLineInput> lines, DateTime nowUtc,
        string? customerPoNo = null, string? deliveryNoteRef = null, string? salesOrderRef = null,
        string? notes = null)
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
            CustomerPoNo = string.IsNullOrWhiteSpace(customerPoNo) ? null : customerPoNo.Trim(),
            DeliveryNoteRef = string.IsNullOrWhiteSpace(deliveryNoteRef) ? null : deliveryNoteRef.Trim(),
            SalesOrderRef = string.IsNullOrWhiteSpace(salesOrderRef) ? null : salesOrderRef.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
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
                DiscountPercent = l.DiscountPercent,
                Uom = l.Uom,
            });

        invoice.Post(createdBy, nowUtc); // domain validation (positive totals, valid lines, due date)

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
