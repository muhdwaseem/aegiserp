using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public class VendorPaymentService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public VendorPaymentService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<VendorPayment>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.VendorPayments.AsNoTracking()
            .Include(p => p.Vendor).Include(p => p.BankAccount).Include(p => p.PurchaseInvoice)
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>
    /// Creates and posts a vendor payment in one transaction, generating the GL voucher
    /// (Dr Accounts Payable / Cr bank). When allocated to a purchase invoice, the amount may not
    /// exceed that invoice's outstanding balance.
    /// </summary>
    public async Task<VendorPayment> CreateAndPostAsync(
        int vendorId, int? purchaseInvoiceId, DateOnly date, int fiscalPeriodId,
        int bankAccountId, decimal amount, string? narration, string createdBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var vendor = await db.Vendors.FindAsync(vendorId)
            ?? throw new PostingException("Vendor not found.");

        if (purchaseInvoiceId is int invId)
        {
            var invoice = await db.PurchaseInvoices.AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == invId)
                ?? throw new PostingException("Purchase invoice not found.");
            if (invoice.VendorId != vendorId)
                throw new PostingException("Purchase invoice belongs to a different vendor.");
            if (invoice.Status != VoucherStatus.Posted)
                throw new PostingException("Only posted purchase invoices can be settled.");

            // SQLite cannot aggregate decimals server-side, so materialise then sum.
            var paid = (await db.VendorPayments
                .Where(p => p.PurchaseInvoiceId == invId && p.Status == VoucherStatus.Posted)
                .Select(p => p.Amount).ToListAsync()).Sum();
            var debited = (await db.DebitNotes.Include(d => d.Lines)
                .Where(d => d.PurchaseInvoiceId == invId && d.Status == VoucherStatus.Posted)
                .ToListAsync()).Sum(d => d.TotalGross);
            var outstanding = invoice.TotalGross - paid - debited;
            if (amount > outstanding)
                throw new PostingException(
                    $"Amount {amount:N2} exceeds the outstanding {outstanding:N2} on {invoice.InvoiceNo}.");
        }

        var paymentNo = await JournalPoster.NextDocNoAsync(db, "PV", date.Year);

        var payment = new VendorPayment
        {
            PaymentNo = paymentNo,
            VendorId = vendorId,
            PurchaseInvoiceId = purchaseInvoiceId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            BankAccountId = bankAccountId,
            Amount = amount,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Vendor payment — {vendor.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        payment.Post(nowUtc); // domain validation

        var ap = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsPayable);
        var voucherLines = new List<VoucherLineInput>
        {
            new(ap.Id, null, $"{vendor.Name} — {paymentNo}", amount, 0),
            new(bankAccountId, null, payment.Narration, 0, amount),
        };

        payment.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.Payment, paymentNo, date, fiscalPeriodId,
            payment.Narration, paymentNo, createdBy, voucherLines, nowUtc);

        db.VendorPayments.Add(payment);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return payment;
    }
}
