using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Account" form collects.</summary>
public record NewAccountInput(
    string Code, string Name, AccountType Type, bool IsPostable,
    string? Category, string Currency, int? ParentId, string? Description, decimal OpeningBalance,
    PnlSection? PnlSection = null);

/// <summary>One parsed row from an imported chart-of-accounts CSV, before its parent code has been
/// resolved to an id (see <see cref="ChartOfAccountsService.ImportAsync"/>).</summary>
public record ImportAccountRow(
    string Code, string Name, AccountType Type, bool IsPostable,
    string? ParentCode, string? Category, PnlSection? PnlSection, string? Currency);

/// <summary>Outcome of importing one CSV row — surfaced per-row so a bad row doesn't block the rest.</summary>
public record ImportRowResult(int RowNumber, string Code, bool Success, string Message);

/// <summary>Outcome of one account in a <see cref="ChartOfAccountsService.DeleteManyAsync"/> batch.</summary>
public record BulkDeleteResult(int Id, string Code, bool Success, string Message);

/// <summary>One row of the bulk Opening Balances screen — an account and whatever opening balance
/// it currently carries (both zero if it doesn't have one yet).</summary>
public record OpeningBalanceRow(int AccountId, string Code, string Name, AccountType Type, decimal Debit, decimal Credit);

/// <summary>One account's opening balance as entered on that screen.</summary>
public record OpeningBalanceEntry(int AccountId, decimal Debit, decimal Credit);

public class ChartOfAccountsService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public ChartOfAccountsService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    /// <summary>
    /// Every posting flow (Sales/Purchase Invoice, Receipt, Vendor Payment, Credit/Debit Note) and
    /// opening-balance entry looks up a handful of control accounts by their exact well-known code
    /// (see <see cref="WellKnownAccounts"/> and the "31010" equity account <see cref="CreateAsync"/>
    /// uses) — a company with none of these can't post anything at all. The Receipt/Payment
    /// Voucher and Direct Expense "bank account" pickers have their own undocumented convention on
    /// top of that: they only offer Asset accounts whose code starts with "110" (see those dialogs'
    /// OnInitializedAsync) — with none, there is nothing to select and nothing to deposit/pay from.
    /// Generating all of this up front for a new company avoids both dead ends; the user can still
    /// rename, recode or add more accounts later.
    /// </summary>
    public static List<Account> BuildStarterAccounts() => new()
    {
        new() { Code = "11020", Name = "Bank Account", Type = AccountType.Asset, Category = "Cash and cash equivalents" },
        new() { Code = WellKnownAccounts.AccountsReceivable, Name = "Accounts Receivable", Type = AccountType.Asset, Category = "Accounts receivable" },
        new() { Code = WellKnownAccounts.VatInput, Name = "VAT Input / Prepaid Expenses", Type = AccountType.Asset, Category = "Current asset" },
        new() { Code = WellKnownAccounts.AccountsPayable, Name = "Accounts Payable", Type = AccountType.Liability, Category = "Accounts payable" },
        new() { Code = WellKnownAccounts.VatPayable, Name = "VAT Payable", Type = AccountType.Liability, Category = "Current liability" },
        new() { Code = WellKnownAccounts.DeferredRevenue, Name = "Deferred Revenue", Type = AccountType.Liability, Category = "Current liability" },
        new() { Code = "31010", Name = "Share Capital & Retained Earnings", Type = AccountType.Equity, Category = "Equity" },
    };

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

    /// <summary>Every cost centre including inactive ones — for the management page.</summary>
    public async Task<List<CostCenter>> GetAllCostCentersAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CostCenters.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
    }

    public async Task<CostCenter> CreateCostCenterAsync(string code, string name)
    {
        code = code.Trim();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PostingException("Cost centre code is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Cost centre name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.CostCenters.AnyAsync(c => c.Code == code))
            throw new PostingException($"A cost centre with code {code} already exists.");

        var cc = new CostCenter { Code = code, Name = name, IsActive = true };
        db.CostCenters.Add(cc);
        await db.SaveChangesAsync();
        return cc;
    }

    public async Task UpdateCostCenterAsync(int id, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Cost centre name is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        var cc = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Cost centre not found.");
        cc.Name = name;
        await db.SaveChangesAsync();
    }

    public async Task SetCostCenterActiveAsync(int id, bool isActive)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var cc = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Cost centre not found.");
        cc.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    public async Task DeleteCostCenterAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var cc = await db.CostCenters.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Cost centre not found.");
        if (await db.JournalLines.AnyAsync(l => l.CostCenterId == id))
            throw new PostingException("This cost centre has posted entries and cannot be deleted — deactivate it instead.");
        db.CostCenters.Remove(cc);
        await db.SaveChangesAsync();
    }

    public async Task<bool> CodeExistsAsync(string code)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Accounts.AnyAsync(a => a.Code == code);
    }

    /// <summary>Suggests the next free numeric code under a parent (max child + 10), or parent + 10 if it has none.</summary>
    /// <summary>
    /// Suggests the next posting-account code under a header, e.g. header "510" already has
    /// postable children "51001", "51002" — the next one offered is "51003". Children are numbered
    /// by appending a running suffix to the header's own code, so the suggestion is always the
    /// highest existing suffix plus one (not a jump — the very next number a user expects).
    /// </summary>
    public async Task<string> SuggestCodeAsync(int? parentId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        if (parentId is not int pid)
            return "";
        var parent = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == pid);
        if (parent is null) return "";

        var childCodes = await db.Accounts.AsNoTracking()
            .Where(a => a.ParentId == pid).Select(a => a.Code).ToListAsync();

        const int defaultSuffixWidth = 2;
        var suffixWidth = defaultSuffixWidth;
        var maxSuffix = 0;
        foreach (var c in childCodes)
        {
            if (c.Length > parent.Code.Length && c.StartsWith(parent.Code) &&
                int.TryParse(c[parent.Code.Length..], out var suf))
            {
                suffixWidth = Math.Max(suffixWidth, c.Length - parent.Code.Length);
                if (suf > maxSuffix) maxSuffix = suf;
            }
        }
        return parent.Code + (maxSuffix + 1).ToString(new string('0', suffixWidth));
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
            // Dated at the very start of the books (earliest period), not the latest one — an
            // opening balance has to predate every reporting range it should show up in, or the
            // Trial Balance's "Opening Balance" column treats it as an in-period transaction instead.
            var period = await db.FiscalPeriods.OrderBy(p => p.StartDate).FirstOrDefaultAsync()
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
    /// Every postable account (except the opening-balance equity account itself, which only ever
    /// absorbs whatever the others need to balance) alongside whatever opening balance it currently
    /// carries — for the bulk Opening Balances screen. A small firm switching from another system
    /// can key in each account's Debit or Credit balance here in one sitting, the way they'd read it
    /// off their old trial balance, instead of re-creating every account just to set one field.
    /// </summary>
    public async Task<List<OpeningBalanceRow>> GetOpeningBalancesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.IsPostable && a.Code != "31010")
            .OrderBy(a => a.Code)
            .ToListAsync();

        // Grouped and summed client-side (not once the values reach SQL) rather than a straight
        // dictionary lookup, because the equity account (excluded above) carries one contra line
        // per account that has an opening balance — a plain ToDictionaryAsync on AccountId would
        // throw the moment a second one exists.
        var openingLines = await db.JournalLines.AsNoTracking()
            .Where(l => l.JournalVoucher.Type == VoucherType.Opening)
            .Select(l => new { l.AccountId, l.Debit, l.Credit })
            .ToListAsync();
        var openingByAccount = openingLines.GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => (Debit: g.Sum(l => l.Debit), Credit: g.Sum(l => l.Credit)));

        return accounts.Select(a =>
        {
            openingByAccount.TryGetValue(a.Id, out var line);
            return new OpeningBalanceRow(a.Id, a.Code, a.Name, a.Type, line.Debit, line.Credit);
        }).ToList();
    }

    /// <summary>
    /// Saves opening balances for any number of accounts in one go. Each account's entry is
    /// upserted independently: a zero/zero entry clears a previously-set balance, a nonzero one
    /// creates or adjusts a two-line "Opening" voucher against the equity account (31010) — so
    /// re-saving after fixing a typo just corrects the existing entry rather than piling up a second
    /// one. Every entry is dated at the very start of the books (earliest fiscal period), so it
    /// shows as a true opening balance rather than an in-period transaction on every report.
    /// </summary>
    public async Task SetOpeningBalancesAsync(IEnumerable<OpeningBalanceEntry> entries, string updatedBy)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var equity = await db.Accounts.FirstOrDefaultAsync(a => a.Code == "31010")
            ?? throw new PostingException("Opening-balance equity account 31010 is missing.");
        var period = await db.FiscalPeriods.OrderBy(p => p.StartDate).FirstOrDefaultAsync()
            ?? throw new PostingException("No fiscal period is defined for the opening entry.");
        var now = DateTime.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync();

        foreach (var entry in entries)
        {
            if (entry.Debit < 0 || entry.Credit < 0)
                throw new PostingException("Opening balances cannot be negative.");
            if (entry.Debit != 0 && entry.Credit != 0)
                throw new PostingException("An account can't have both a debit and a credit opening balance.");
            if (entry.AccountId == equity.Id)
                continue; // the equity account absorbs the plug automatically; it has no row to edit

            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == entry.AccountId)
                ?? throw new PostingException("Account not found.");

            var existing = await db.JournalVouchers.Include(v => v.Lines)
                .FirstOrDefaultAsync(v => v.Type == VoucherType.Opening && v.Reference == account.Code);

            if (entry.Debit == 0 && entry.Credit == 0)
            {
                if (existing is not null) db.JournalVouchers.Remove(existing);
                continue;
            }

            if (existing is not null)
            {
                existing.Lines.First(l => l.AccountId == account.Id).Debit = entry.Debit;
                existing.Lines.First(l => l.AccountId == account.Id).Credit = entry.Credit;
                existing.Lines.First(l => l.AccountId == equity.Id).Debit = entry.Credit;
                existing.Lines.First(l => l.AccountId == equity.Id).Credit = entry.Debit;
            }
            else
            {
                var lines = new List<VoucherLineInput>
                {
                    new(account.Id, null, "Opening balance", entry.Debit, entry.Credit),
                    new(equity.Id, null, $"Opening balance — {account.Code}", entry.Credit, entry.Debit),
                };
                await JournalPoster.PostAsync(db, VoucherType.Opening, explicitNo: null,
                    period.StartDate, period.Id, $"Opening balance — {account.Name}", account.Code, updatedBy, lines, now);
                // Voucher numbers are allocated by counting existing rows in the database, not the
                // change tracker — each one has to land before the next PostAsync call in this loop
                // computes its own number, or two accounts in the same batch collide on one number.
                await db.SaveChangesAsync();
            }
        }

        await JournalPoster.SaveAndCommitAsync(db, tx);
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

    /// <summary>Quick activate/deactivate toggle that doesn't require re-submitting the whole edit
    /// form — mirrors the same pattern used for currencies and tags.</summary>
    public async Task SetActiveAsync(int id, bool isActive)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var acc = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id)
            ?? throw new PostingException("Account not found.");
        acc.IsActive = isActive;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Imports a batch of accounts (e.g. from a CSV) one at a time, so one bad row is reported
    /// without blocking the rest. A row's parent must already exist — either from before this
    /// import or from an earlier row in the same batch — so header/group rows should be listed
    /// before the accounts that live under them.
    /// </summary>
    public async Task<List<ImportRowResult>> ImportAsync(List<ImportAccountRow> rows, string createdBy)
    {
        var codeToId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var db = await _dbf.CreateDbContextAsync())
            foreach (var a in await db.Accounts.AsNoTracking().ToListAsync())
                codeToId[a.Code] = a.Id;

        var results = new List<ImportRowResult>();
        var rowNo = 1; // row 1 is the header line in the source file
        foreach (var row in rows)
        {
            rowNo++;
            try
            {
                int? parentId = null;
                if (!string.IsNullOrWhiteSpace(row.ParentCode))
                {
                    if (!codeToId.TryGetValue(row.ParentCode, out var pid))
                        throw new PostingException(
                            $"Parent account '{row.ParentCode}' was not found — list it on an earlier row or create it first.");
                    parentId = pid;
                }

                var input = new NewAccountInput(row.Code, row.Name, row.Type, row.IsPostable,
                    row.Category, row.Currency ?? "AED", parentId, null, 0, row.PnlSection);
                var created = await CreateAsync(input, createdBy);
                codeToId[created.Code] = created.Id;
                results.Add(new ImportRowResult(rowNo, row.Code, true, "Created"));
            }
            catch (PostingException ex)
            {
                results.Add(new ImportRowResult(rowNo, row.Code, false, ex.Message));
            }
        }
        return results;
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

        var reason = await DeleteBlockReasonAsync(db, id);
        if (reason is not null) throw new PostingException(reason);

        db.Accounts.Remove(acc);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Deletes as many of the given accounts as it safely can — e.g. clearing out an old chart of
    /// accounts before importing a new one. Each account gets the same checks as
    /// <see cref="DeleteAsync"/>, so anything still referenced is skipped (never force-deleted) and
    /// reported back with its reason rather than aborting the whole batch. Runs in passes so a
    /// header whose children are *also* selected this run is deleted right after they are, without
    /// the caller having to pre-sort the selection.
    /// </summary>
    public async Task<List<BulkDeleteResult>> DeleteManyAsync(IEnumerable<int> ids)
    {
        var idSet = ids.ToHashSet();
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.Where(a => idSet.Contains(a.Id)).ToListAsync();
        var pending = accounts.Select(a => a.Id).ToHashSet();
        var results = new Dictionary<int, BulkDeleteResult>();

        bool progressed;
        do
        {
            progressed = false;
            foreach (var id in pending.ToList())
            {
                var reason = await DeleteBlockReasonAsync(db, id, pending);
                if (reason is not null) continue;

                var acc = accounts.First(a => a.Id == id);
                db.Accounts.Remove(acc);
                await db.SaveChangesAsync();
                results[id] = new BulkDeleteResult(id, acc.Code, true, "Deleted.");
                pending.Remove(id);
                progressed = true;
            }
        } while (progressed && pending.Count > 0);

        foreach (var id in pending)
        {
            var acc = accounts.First(a => a.Id == id);
            var reason = await DeleteBlockReasonAsync(db, id, pending) ?? "Could not be deleted.";
            results[id] = new BulkDeleteResult(id, acc.Code, false, reason);
        }

        return accounts.Select(a => results[a.Id]).ToList();
    }

    /// <summary>Null if <paramref name="id"/> is safe to delete right now. <paramref name="alsoBeingDeleted"/>
    /// lets a batch delete ignore sub-accounts that are themselves queued for deletion this run,
    /// instead of treating them as a permanent blocker.</summary>
    private static async Task<string?> DeleteBlockReasonAsync(AegisDbContext db, int id, HashSet<int>? alsoBeingDeleted = null)
    {
        var hasOtherChildren = alsoBeingDeleted is null
            ? await db.Accounts.AnyAsync(a => a.ParentId == id)
            : await db.Accounts.AnyAsync(a => a.ParentId == id && !alsoBeingDeleted.Contains(a.Id));
        if (hasOtherChildren) return "This account has sub-accounts. Remove or reassign them first.";
        if (await db.JournalLines.AnyAsync(l => l.AccountId == id))
            return "This account has ledger entries and cannot be deleted — deactivate it instead.";
        if (await db.SalesInvoiceLines.AnyAsync(l => l.RevenueAccountId == id))
            return "This account is used on sales invoices and cannot be deleted.";
        if (await db.CustomerReceipts.AnyAsync(r => r.BankAccountId == id))
            return "This account is used on receipts and cannot be deleted.";
        return null;
    }
}
