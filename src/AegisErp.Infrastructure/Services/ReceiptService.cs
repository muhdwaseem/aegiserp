using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public class ReceiptService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public ReceiptService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<CustomerReceipt>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CustomerReceipts.AsNoTracking()
            .Include(r => r.Customer).Include(r => r.BankAccount).Include(r => r.SalesInvoice)
            .OrderByDescending(r => r.Date).ThenByDescending(r => r.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>
    /// Creates and posts a customer receipt in one transaction, generating the GL voucher
    /// (Dr bank / Cr Accounts Receivable). When allocated to an invoice, the amount may not
    /// exceed that invoice's outstanding balance.
    /// </summary>
    public async Task<CustomerReceipt> CreateAndPostAsync(
        int customerId, int? salesInvoiceId, DateOnly date, int fiscalPeriodId,
        int bankAccountId, decimal amount, string? narration, string createdBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        if (salesInvoiceId is int invId)
        {
            var invoice = await db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == invId)
                ?? throw new PostingException("Invoice not found.");
            if (invoice.CustomerId != customerId)
                throw new PostingException("Invoice belongs to a different customer.");
            if (invoice.Status != VoucherStatus.Posted)
                throw new PostingException("Only posted invoices can be settled.");

            // SQLite cannot aggregate decimals server-side, so materialise then sum.
            var allocated = (await db.CustomerReceipts
                .Where(r => r.SalesInvoiceId == invId && r.Status == VoucherStatus.Posted)
                .Select(r => r.Amount)
                .ToListAsync()).Sum();
            var credited = (await db.CreditNotes.Include(n => n.Lines)
                .Where(n => n.SalesInvoiceId == invId && n.Status == VoucherStatus.Posted)
                .ToListAsync()).Sum(n => n.TotalGross);
            var outstanding = invoice.TotalGross - allocated - credited;
            if (amount > outstanding)
                throw new PostingException(
                    $"Amount {amount:N2} exceeds the outstanding {outstanding:N2} on {invoice.InvoiceNo}.");
        }

        var receiptNo = await JournalPoster.NextDocNoAsync(db, "RV", date.Year);

        var receipt = new CustomerReceipt
        {
            ReceiptNo = receiptNo,
            CustomerId = customerId,
            SalesInvoiceId = salesInvoiceId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            BankAccountId = bankAccountId,
            Amount = amount,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Customer receipt — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        receipt.Post(nowUtc); // domain validation

        var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
        var voucherLines = new List<VoucherLineInput>
        {
            new(bankAccountId, null, receipt.Narration, amount, 0),
            new(ar.Id, null, $"{customer.Name} — {receiptNo}", 0, amount),
        };

        receipt.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.Receipt, receiptNo, date, fiscalPeriodId,
            receipt.Narration, receiptNo, createdBy, voucherLines, nowUtc);

        db.CustomerReceipts.Add(receipt);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return receipt;
    }
}
