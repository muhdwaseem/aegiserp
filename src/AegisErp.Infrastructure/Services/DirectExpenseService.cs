using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>One expense line: an amount charged to a specific expense account, optionally picked from the Items catalog.</summary>
public record DirectExpenseLineInput(int ExpenseAccountId, int? CostCenterId, string? Description, decimal Amount, int? ItemId = null);

public class DirectExpenseService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public DirectExpenseService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<DirectExpense>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.DirectExpenses.AsNoTracking()
            .Include(x => x.Vendor).Include(x => x.Customer).Include(x => x.BankAccount)
            .Include(x => x.Lines).ThenInclude(l => l.ExpenseAccount)
            .OrderByDescending(x => x.Date).ThenByDescending(x => x.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>One direct expense with full detail, for the expense detail view.</summary>
    public async Task<DirectExpense?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.DirectExpenses.AsNoTracking()
            .Include(x => x.Vendor).Include(x => x.Customer).Include(x => x.BankAccount)
            .Include(x => x.Lines).ThenInclude(l => l.ExpenseAccount)
            .Include(x => x.JournalVoucher)
            .FirstOrDefaultAsync(x => x.Id == id);
    }

    /// <summary>
    /// Creates and posts a direct expense in one transaction, generating the GL voucher
    /// (Dr each line's expense account for its amount / Cr the "paid through" bank account for
    /// the total). No vendor invoice or Accounts Payable is involved — Vendor and Customer are
    /// both optional (Vendor just records who was paid; Customer is a "billable to" tag only).
    /// </summary>
    public async Task<DirectExpense> CreateAndPostAsync(
        int? vendorId, int? customerId, DateOnly date, int fiscalPeriodId, int bankAccountId,
        string? reference, string? narration, string createdBy, DateTime nowUtc,
        IEnumerable<DirectExpenseLineInput> lines)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        if (vendorId is int vId && await db.Vendors.FindAsync(vId) is null)
            throw new PostingException("Vendor not found.");
        if (customerId is int cId && await db.Customers.FindAsync(cId) is null)
            throw new PostingException("Customer not found.");

        var expenseNo = await JournalPoster.NextDocNoAsync(db, "EXP", date.Year);

        var expense = new DirectExpense
        {
            ExpenseNo = expenseNo,
            VendorId = vendorId,
            CustomerId = customerId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            BankAccountId = bankAccountId,
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            Narration = narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in lines)
            expense.Lines.Add(new DirectExpenseLine
            {
                LineNo = no++,
                ItemId = l.ItemId,
                ExpenseAccountId = l.ExpenseAccountId,
                CostCenterId = l.CostCenterId,
                Description = l.Description,
                Amount = l.Amount,
            });

        expense.Post(nowUtc); // domain validation (lines exist, accounts/amounts valid)

        var voucherLines = expense.Lines
            .Select(l => new VoucherLineInput(l.ExpenseAccountId, l.CostCenterId, l.Description, l.Amount, 0))
            .ToList();
        voucherLines.Add(new VoucherLineInput(bankAccountId, null, expense.Narration, 0, expense.TotalAmount));

        expense.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.Expense, expenseNo, date, fiscalPeriodId,
            expense.Narration, reference ?? expenseNo, createdBy, voucherLines, nowUtc);

        db.DirectExpenses.Add(expense);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return expense;
    }
}
