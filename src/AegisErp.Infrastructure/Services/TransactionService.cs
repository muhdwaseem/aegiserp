using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>
/// Backs the cross-invoice Transactions view — every Sales Invoice line, company-wide, flattened
/// into one list with fulfillment (Completed/Supplier) and payment (Paid From) attribution.
/// </summary>
public class TransactionService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public TransactionService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<TransactionRow>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();

        // Start from SalesInvoices (company-scoped) and flatten in memory — SalesInvoiceLine has
        // no CompanyId/query filter of its own, so querying db.SalesInvoiceLines directly would
        // leak every company's lines.
        var invoices = await db.SalesInvoices.AsNoTracking()
            .Include(i => i.Customer)
            .Include(i => i.Lines).ThenInclude(l => l.Item)
            .Include(i => i.Lines).ThenInclude(l => l.Supplier)
            .Where(i => i.Status != VoucherStatus.Void)
            .OrderByDescending(i => i.Date).ThenByDescending(i => i.Id)
            .ToListAsync();

        var lineIds = invoices.SelectMany(i => i.Lines).Select(l => l.Id).ToList();
        var paidFrom = (await db.ReceiptAllocations.AsNoTracking()
            .Where(a => lineIds.Contains(a.SalesInvoiceLineId) && a.CustomerReceipt.Status == VoucherStatus.Posted)
            .OrderByDescending(a => a.CustomerReceipt.Date).ThenByDescending(a => a.CustomerReceiptId)
            .Select(a => new { a.SalesInvoiceLineId, AccountName = a.CustomerReceipt.BankAccount.Name })
            .ToListAsync())
            .GroupBy(a => a.SalesInvoiceLineId)
            .ToDictionary(g => g.Key, g => g.First().AccountName); // most recently-dated receipt's account

        var rows = new List<TransactionRow>();
        foreach (var inv in invoices)
            foreach (var l in inv.Lines.OrderBy(l => l.LineNo))
                rows.Add(new TransactionRow(
                    l.Id, $"{inv.InvoiceNo}-L{l.LineNo}", inv.Id, inv.InvoiceNo, inv.Date, inv.Status,
                    inv.Customer.Name, inv.SalesOrderRef, l.Description, l.Item?.Name, l.Item?.Kind,
                    l.Quantity, l.Gross, l.IsCompleted, l.CompletedAtUtc, l.CompletedBy,
                    l.SupplierId, l.Supplier?.Name, paidFrom.GetValueOrDefault(l.Id)));
        return rows;
    }

    /// <summary>Marks one line complete/incomplete. Scoped through SalesInvoices so a lineId from
    /// another company can never be reached, even though SalesInvoiceLine has no filter of its own.</summary>
    public async Task SetCompletionAsync(int lineId, bool isCompleted, string changedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.SalesInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Transaction line not found.");

        line.IsCompleted = isCompleted;
        line.CompletedAtUtc = isCompleted ? nowUtc : null;
        line.CompletedBy = isCompleted ? changedBy : null;
        await db.SaveChangesAsync();
    }

    /// <summary>Assigns (or clears) the vendor/subcontractor fulfilling one line.</summary>
    public async Task SetSupplierAsync(int lineId, int? supplierId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var line = await db.SalesInvoices.SelectMany(i => i.Lines).FirstOrDefaultAsync(l => l.Id == lineId)
            ?? throw new PostingException("Transaction line not found.");

        if (supplierId is int sid && !await db.Vendors.AnyAsync(v => v.Id == sid))
            throw new PostingException("Supplier not found.");

        line.SupplierId = supplierId;
        await db.SaveChangesAsync();
    }
}
