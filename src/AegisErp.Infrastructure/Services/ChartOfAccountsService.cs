using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Account" form collects.</summary>
public record NewAccountInput(
    string Code, string Name, AccountType Type, bool IsPostable,
    string? Category, string Currency, int? ParentId, string? Description, decimal OpeningBalance,
    PnlSection? PnlSection = null);

public class ChartOfAccountsService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public ChartOfAccountsService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<Account>> GetAllAsync(bool postableOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Accounts.AsNoTracking().OrderBy(a => a.Code).AsQueryable();
        if (postableOnly) q = q.Where(a => a.IsPostable && a.IsActive);
        return await q.ToListAsync();
    }

    /// <summary>Header (non-postable) accounts, for the parent picker.</summary>
    public async Task<List<Account>> GetHeaderAccountsAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().Where(a => !a.IsPostable).OrderBy(a => a.Code).ToListAsync();
    }

    public async Task<List<CostCenter>> GetCostCentersAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CostCenters.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<bool> CodeExistsAsync(string code)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Accounts.AnyAsync(a => a.Code == code);
    }

    /// <summary>Suggests the next free numeric code under a parent (max child + 10), or parent + 10 if it has none.</summary>
    public async Task<string> SuggestCodeAsync(int? parentId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        if (parentId is not int pid)
            return "";
        var parent = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == pid);
        if (parent is null) return "";

        var childCodes = await db.Accounts.AsNoTracking()
            .Where(a => a.ParentId == pid).Select(a => a.Code).ToListAsync();

        var baseNum = int.TryParse(parent.Code, out var pn) ? pn : 0;
        var max = baseNum;
        foreach (var c in childCodes)
            if (int.TryParse(c, out var n) && n > max) max = n;
        return (max + 10).ToString();
    }

    /// <summary>
    /// Creates an account and, if an opening balance is supplied for a postable account,
    /// posts a balanced opening voucher against the equity account (31010) in one transaction.
    /// </summary>
    public async Task<Account> CreateAsync(NewAccountInput input, string createdBy)
    {
        var code = input.Code.Trim();
        var name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PostingException("Account number is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Account name is required.");
        if (input.OpeningBalance != 0 && !input.IsPostable)
            throw new PostingException("A header account cannot carry an opening balance.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.Accounts.AnyAsync(a => a.Code == code))
            throw new PostingException($"An account with number {code} already exists.");

        await using var tx = await db.Database.BeginTransactionAsync();

        var account = new Account
        {
            Code = code,
            Name = name,
            Type = input.Type,
            IsPostable = input.IsPostable,
            ParentId = input.ParentId,
            Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim(),
            Currency = string.IsNullOrWhiteSpace(input.Currency) ? "AED" : input.Currency.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            IsActive = true,
            PnlSection = input.PnlSection ?? DefaultPnlSection(input.Type),
        };
        db.Accounts.Add(account);
        await db.SaveChangesAsync(); // assigns account.Id

        if (input.OpeningBalance != 0)
        {
            var equity = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "31010")
                ?? throw new PostingException("Opening-balance equity account 31010 is missing.");
            var period = await db.FiscalPeriods.OrderByDescending(p => p.StartDate).FirstOrDefaultAsync()
                ?? throw new PostingException("No fiscal period is defined for the opening entry.");

            var now = DateTime.UtcNow;
            var amount = Math.Abs(input.OpeningBalance);
            var onDebitSide = input.Type.NormalBalance() == NormalBalance.Debit;

            // Positive opening balance sits on the account's normal side; equity is the contra.
            var lines = new List<VoucherLineInput>
            {
                onDebitSide
                    ? new VoucherLineInput(account.Id, null, "Opening balance", amount, 0)
                    : new VoucherLineInput(account.Id, null, "Opening balance", 0, amount),
                onDebitSide
                    ? new VoucherLineInput(equity.Id, null, $"Opening balance — {code}", 0, amount)
                    : new VoucherLineInput(equity.Id, null, $"Opening balance — {code}", amount, 0),
            };

            await JournalPoster.PostAsync(db, VoucherType.Opening, explicitNo: null,
                period.StartDate, period.Id, $"Opening balance — {name}", code, createdBy, lines, now);
        }

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return account;
    }

    /// <summary>
    /// Updates the editable fields of an account. Code, type and posting-type are fixed once created.
    /// <paramref name="expectedRowVersion"/> must be the value the editor read the account with —
    /// if another user has saved a change since, this throws a recoverable <see cref="PostingException"/>
    /// instead of silently overwriting their edit.
    /// </summary>
    public async Task<Account> UpdateAsync(int id, string name, string? category, string currency,
        int? parentId, string? description, bool isActive, Guid expectedRowVersion, string updatedBy,
        PnlSection? pnlSection = null)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Account name is required.");
        if (parentId == id) throw new PostingException("An account cannot be its own parent.");

        await using var db = await _dbf.CreateDbContextAsync();
        var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new PostingException("Account not found.");

        acc.Name = name;
        acc.Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        acc.Currency = string.IsNullOrWhiteSpace(currency) ? "AED" : currency.Trim();
        acc.ParentId = parentId;
        acc.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        acc.IsActive = isActive;
        acc.PnlSection = acc.Type is AccountType.Income or AccountType.Expense
            ? pnlSection ?? acc.PnlSection ?? DefaultPnlSection(acc.Type)
            : null;
        acc.UpdatedBy = updatedBy;
        acc.UpdatedAtUtc = DateTime.UtcNow;
        acc.RowVersion = Guid.NewGuid();
        // Check against the version the editor actually saw, not whatever's now freshly loaded above.
        db.Entry(acc).Property(a => a.RowVersion).OriginalValue = expectedRowVersion;
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return acc;
    }

    /// <summary>The sensible default P&amp;L section for a newly created account of this type — Cost
    /// of Goods Sold is never auto-assigned; the user picks it explicitly when it applies.</summary>
    private static PnlSection? DefaultPnlSection(AccountType type) => type switch
    {
        AccountType.Income => PnlSection.OperatingIncome,
        AccountType.Expense => PnlSection.OperatingExpense,
        _ => null,
    };

    /// <summary>
    /// Deletes an account, but only if nothing references it — sub-accounts, ledger entries,
    /// invoices or receipts. Otherwise it should be deactivated, not deleted.
    /// </summary>
    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new PostingException("Account not found.");

        if (await db.Accounts.AnyAsync(a => a.ParentId == id))
            throw new PostingException("This account has sub-accounts. Remove or reassign them first.");
        if (await db.JournalLines.AnyAsync(l => l.AccountId == id))
            throw new PostingException("This account has ledger entries and cannot be deleted — deactivate it instead.");
        if (await db.SalesInvoiceLines.AnyAsync(l => l.RevenueAccountId == id))
            throw new PostingException("This account is used on sales invoices and cannot be deleted.");
        if (await db.CustomerReceipts.AnyAsync(r => r.BankAccountId == id))
            throw new PostingException("This account is used on receipts and cannot be deleted.");

        db.Accounts.Remove(acc);
        await db.SaveChangesAsync();
    }
}
