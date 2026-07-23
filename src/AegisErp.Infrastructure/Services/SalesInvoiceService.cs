using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a sales invoice from the UI.</summary>
public record InvoiceLineInput(string Description, int RevenueAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate);

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

        var invoiceNo = await JournalPoster.NextDocNoAsync(db, "INV", date.Year);

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
        };

        var no = 1;
        foreach (var l in lines)
            invoice.Lines.Add(new SalesInvoiceLine
            {
                LineNo = no++,
                Description = l.Description,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
            });

        invoice.Post(nowUtc); // domain validation (positive totals, valid lines, due date)

        // Build the GL voucher: Dr AR for the gross, Cr revenue per line, Cr VAT for the total tax.
        var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
        var voucherLines = new List<VoucherLineInput>
        {
            new(ar.Id, null, $"{customer.Name} — {invoiceNo}", invoice.TotalGross, 0),
        };
        // Skip zero-net lines (e.g. complimentary items) — the invoice still records them,
        // but a zero GL line would fail the double-entry rules.
        voucherLines.AddRange(invoice.Lines.Where(l => l.Net > 0).Select(l =>
            new VoucherLineInput(l.RevenueAccountId, l.CostCenterId, l.Description, 0, l.Net)));
        if (invoice.TotalVat > 0)
        {
            var vat = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatPayable);
            voucherLines.Add(new VoucherLineInput(vat.Id, null, $"Output VAT — {invoiceNo}", 0, invoice.TotalVat));
        }

        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.SalesInvoice, invoiceNo, date, fiscalPeriodId,
            invoice.Narration, invoiceNo, createdBy, voucherLines, nowUtc);

        db.SalesInvoices.Add(invoice);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }
}
