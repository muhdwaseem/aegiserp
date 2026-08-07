using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>
/// Backs the cross-invoice Expense Transactions view — the AP mirror of <see cref="TransactionService"/>.
/// Every Purchase Invoice line, company-wide, flattened into one list with fulfillment (Completed)
/// and payment ("Paid To") attribution. Direct Expenses are excluded: they're settled in full the
/// moment they're posted, so there's nothing to track.
/// </summary>
public class ExpenseTransactionService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public ExpenseTransactionService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<ExpenseTransactionRow>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();

        // Start from PurchaseInvoices (company-scoped) and flatten in memory — PurchaseInvoiceLine
        // has no CompanyId/query filter of its own, so querying db.PurchaseInvoiceLines directly
        // would leak every company's lines.
        var invoices = await db.PurchaseInvoices.AsNoTracking()
            .Include(i => i.Vendor)
            .Include(i => i.Lines)
            .Where(i => i.Status != VoucherStatus.Void)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .ToListAsync();

        var lineIds = invoices.SelectMany(i => i.Lines).Select(l => l.Id).ToList();
        var paidTo = (await db.VendorPaymentAllocations.AsNoTracking()
            .Where(a => lineIds.Contains(a.PurchaseInvoiceLineId) && a.VendorPayment.Status == VoucherStatus.Posted)
            .OrderByDescending(a => a.VendorPayment.Date).ThenByDescending(a => a.VendorPaymentId)
            .Select(a => new { a.PurchaseInvoiceLineId, AccountName = a.VendorPayment.BankAccount.Name })
            .ToListAsync())
            .GroupBy(a => a.PurchaseInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.First().AccountName); // most recently-dated payment's account

        var rows = new List<ExpenseTransactionRow>();
        foreach (var inv in invoices)
            foreach (var l in inv.Lines.OrderBy(l => l.LineNo))
                rows.Add(new ExpenseTransactionRow(
                    l.Id, $"{inv.InvoiceNo}-L{l.LineNo}", inv.Id, inv.InvoiceNo, inv.Date, inv.Status,
                    inv.Vendor.Name, inv.VendorRef, inv.LpoNo, l.Description,
                    l.Quantity, l.Gross, l.IsCompleted, l.CompletedAtUtc, l.CompletedBy,
                    paidTo.GetValueOrDefault(l.Id)));
        return rows;
    }

    /// <summary>Marks one line complete/incomplete. Scoped through PurchaseInvoices so a lineId
    /// from another company can never be reached, even though PurchaseInvoiceLine has no filter
    /// of its own.</summary>
    public async Task SetCompletionAsync(int lineId, bool isCompleted, string changedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.PurchaseInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Transaction line not found.");

        line.IsCompleted = isCompleted;
        line.CompletedAtUtc = isCompleted ? nowUtc : null;
        line.CompletedBy = isCompleted ? changedBy : null;
        await db.SaveChangesAsync();
    }
}

public record ExpenseTransactionRow(
    int LineId, string TranRef, int InvoiceId, string InvoiceNo, DateOnly InvoiceDate, VoucherStatus InvoiceStatus,
    string VendorName, string? VendorRef, string? LpoNo, string Description,
    decimal Quantity, decimal Amount, bool IsCompleted, DateTime? CompletedAtUtc, string? CompletedBy,
    string? PaidToAccountName);
