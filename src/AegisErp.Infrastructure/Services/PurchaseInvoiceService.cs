using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a purchase invoice from the UI.</summary>
public record PurchaseLineInput(string Description, int ExpenseAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate);

public class PurchaseInvoiceService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public PurchaseInvoiceService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<PurchaseInvoice>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PurchaseInvoices.AsNoTracking()
            .Include(i => i.Vendor).Include(i => i.Lines).Include(i => i.JournalVoucher)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>Every invoice, for the invoice list page.</summary>
    public async Task<List<PurchaseInvoice>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PurchaseInvoices.AsNoTracking()
            .Include(i => i.Vendor).Include(i => i.Lines).Include(i => i.JournalVoucher)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .ToListAsync();
    }

    /// <summary>
    /// Creates and posts a purchase invoice in one transaction: the invoice, its lines and the
    /// generated GL voucher (Dr expense/asset net per line / Dr VAT input / Cr AP gross) are
    /// persisted atomically. Invoice number and voucher number are the same document number.
    /// </summary>
    public async Task<PurchaseInvoice> CreateAndPostAsync(
        int vendorId, string? vendorRef, DateOnly date, int fiscalPeriodId, string? narration,
        string createdBy, IEnumerable<PurchaseLineInput> lines, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var vendor = await db.Vendors.FindAsync(vendorId)
            ?? throw new PostingException("Vendor not found.");

        var invoiceNo = await JournalPoster.NextDocNoAsync(db, "PINV", date.Year);

        var invoice = new PurchaseInvoice
        {
            InvoiceNo = invoiceNo,
            VendorRef = string.IsNullOrWhiteSpace(vendorRef) ? null : vendorRef.Trim(),
            VendorId = vendorId,
            Date = date,
            DueDate = date.AddDays(vendor.PaymentTermsDays),
            FiscalPeriodId = fiscalPeriodId,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Purchase invoice — {vendor.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in lines)
            invoice.Lines.Add(new PurchaseInvoiceLine
            {
                LineNo = no++,
                Description = l.Description,
                ExpenseAccountId = l.ExpenseAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
            });

        invoice.Post(nowUtc); // domain validation (positive totals, valid lines, due date)

        // Build the GL voucher: Dr expense per line, Dr VAT input for the total tax, Cr AP for the gross.
        var ap = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsPayable);
        var voucherLines = new List<VoucherLineInput>();
        voucherLines.AddRange(invoice.Lines.Where(l => l.Net > 0).Select(l =>
            new VoucherLineInput(l.ExpenseAccountId, l.CostCenterId, l.Description, l.Net, 0)));
        if (invoice.TotalVat > 0)
        {
            var vat = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatInput);
            voucherLines.Add(new VoucherLineInput(vat.Id, null, $"Input VAT — {invoiceNo}", invoice.TotalVat, 0));
        }
        voucherLines.Add(new VoucherLineInput(ap.Id, null, $"{vendor.Name} — {invoiceNo}", 0, invoice.TotalGross));

        invoice.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.PurchaseInvoice, invoiceNo, date, fiscalPeriodId,
            invoice.Narration, invoiceNo, createdBy, voucherLines, nowUtc);

        db.PurchaseInvoices.Add(invoice);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return invoice;
    }
}
