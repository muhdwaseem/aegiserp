using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class ChartOfAccountsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ChartOfAccountsService _coa;

    public ChartOfAccountsServiceTests() => _coa = new ChartOfAccountsService(_db);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task SuggestCodeAsync_appends_a_running_two_digit_suffix_to_the_parent_code()
    {
        var header = await _coa.CreateAsync(
            new("510", "Sales Revenue", AccountType.Income, IsPostable: false, Category: null, Currency: "AED", ParentId: null, Description: null, OpeningBalance: 0),
            "tester");

        // No children yet — the very first one offered is "01".
        Assert.Equal("51001", await _coa.SuggestCodeAsync(header.Id));

        await _coa.CreateAsync(
            new("51001", "Consulting Revenue", AccountType.Income, IsPostable: true, Category: null, Currency: "AED", ParentId: header.Id, Description: null, OpeningBalance: 0),
            "tester");

        // The next suggestion picks up right after the last one created — not a jump of ten.
        Assert.Equal("51002", await _coa.SuggestCodeAsync(header.Id));
    }

    [Fact]
    public async Task DeleteManyAsync_deletes_a_header_and_its_children_regardless_of_selection_order()
    {
        var header = await _coa.CreateAsync(
            new("610", "Old Group", AccountType.Expense, IsPostable: false, Category: null, Currency: "AED", ParentId: null, Description: null, OpeningBalance: 0),
            "tester");
        var child = await _coa.CreateAsync(
            new("61001", "Old Child", AccountType.Expense, IsPostable: true, Category: null, Currency: "AED", ParentId: header.Id, Description: null, OpeningBalance: 0),
            "tester");

        // Header listed before its own child — a naive single pass would skip it.
        var results = await _coa.DeleteManyAsync(new[] { header.Id, child.Id });

        Assert.All(results, r => Assert.True(r.Success, $"{r.Code}: {r.Message}"));
        var remaining = await _coa.GetAllAsync();
        Assert.DoesNotContain(remaining, a => a.Id == header.Id || a.Id == child.Id);
    }

    [Fact]
    public async Task DeleteManyAsync_skips_a_header_whose_child_was_not_also_selected()
    {
        var header = await _coa.CreateAsync(
            new("620", "Kept Group", AccountType.Expense, IsPostable: false, Category: null, Currency: "AED", ParentId: null, Description: null, OpeningBalance: 0),
            "tester");
        await _coa.CreateAsync(
            new("62001", "Kept Child", AccountType.Expense, IsPostable: true, Category: null, Currency: "AED", ParentId: header.Id, Description: null, OpeningBalance: 0),
            "tester");

        var results = await _coa.DeleteManyAsync(new[] { header.Id });

        Assert.False(results.Single().Success);
        Assert.Contains("sub-accounts", results.Single().Message);
        Assert.Contains(await _coa.GetAllAsync(), a => a.Id == header.Id);
    }

    [Fact]
    public async Task SetActiveAsync_toggles_the_account()
    {
        await _coa.SetActiveAsync(_db.Expense.Id, false);
        Assert.False((await _coa.GetAllAsync()).Single(a => a.Id == _db.Expense.Id).IsActive);

        await _coa.SetActiveAsync(_db.Expense.Id, true);
        Assert.True((await _coa.GetAllAsync()).Single(a => a.Id == _db.Expense.Id).IsActive);
    }

    [Fact]
    public async Task ImportAsync_creates_accounts_and_resolves_a_parent_from_an_earlier_row()
    {
        var rows = new List<ImportAccountRow>
        {
            new("60000", "Marketing", AccountType.Expense, IsPostable: false, ParentCode: null, Category: null, PnlSection: null, Currency: null),
            new("60010", "Advertising", AccountType.Expense, IsPostable: true, ParentCode: "60000", Category: null, PnlSection: null, Currency: null),
        };

        var results = await _coa.ImportAsync(rows, "tester");

        Assert.All(results, r => Assert.True(r.Success, r.Message));
        var all = await _coa.GetAllAsync();
        var header = all.Single(a => a.Code == "60000");
        var child = all.Single(a => a.Code == "60010");
        Assert.Null(header.ParentId);
        Assert.Equal(header.Id, child.ParentId);
    }

    [Fact]
    public async Task ImportAsync_resolves_a_parent_that_already_existed_before_the_import()
    {
        var rows = new List<ImportAccountRow>
        {
            new("60020", "Utilities Sub", AccountType.Expense, IsPostable: true, ParentCode: _db.Expense.Code, Category: null, PnlSection: null, Currency: null),
        };

        var results = await _coa.ImportAsync(rows, "tester");

        Assert.True(results.Single().Success);
        var created = (await _coa.GetAllAsync()).Single(a => a.Code == "60020");
        Assert.Equal(_db.Expense.Id, created.ParentId);
    }

    [Fact]
    public async Task ImportAsync_reports_a_missing_parent_without_blocking_other_rows()
    {
        var rows = new List<ImportAccountRow>
        {
            new("60030", "Orphan", AccountType.Expense, IsPostable: true, ParentCode: "NOPE", Category: null, PnlSection: null, Currency: null),
            new("60040", "Fine", AccountType.Expense, IsPostable: true, ParentCode: null, Category: null, PnlSection: null, Currency: null),
        };

        var results = await _coa.ImportAsync(rows, "tester");

        Assert.False(results[0].Success);
        Assert.Contains("Parent account", results[0].Message);
        Assert.True(results[1].Success);
        Assert.Contains(await _coa.GetAllAsync(), a => a.Code == "60040");
    }

    [Fact]
    public async Task ImportAsync_reports_a_duplicate_code_without_blocking_other_rows()
    {
        var rows = new List<ImportAccountRow>
        {
            new(_db.Expense.Code, "Duplicate", AccountType.Expense, IsPostable: true, ParentCode: null, Category: null, PnlSection: null, Currency: null),
            new("60050", "Unique", AccountType.Expense, IsPostable: true, ParentCode: null, Category: null, PnlSection: null, Currency: null),
        };

        var results = await _coa.ImportAsync(rows, "tester");

        Assert.False(results[0].Success);
        Assert.True(results[1].Success);
    }

    [Fact]
    public async Task ImportAsync_does_not_leak_accounts_across_companies()
    {
        _db.SeedOtherCompany();

        var rows = new List<ImportAccountRow>
        {
            new("60060", "Mine", AccountType.Expense, IsPostable: true, ParentCode: null, Category: null, PnlSection: null, Currency: null),
        };
        await _coa.ImportAsync(rows, "tester");

        _db.SwitchTo(_db.OtherCompany.Id);
        Assert.DoesNotContain(await new ChartOfAccountsService(_db).GetAllAsync(), a => a.Code == "60060");
    }

    [Fact]
    public async Task CreateCostCenterAsync_creates_it_and_rejects_a_duplicate_code()
    {
        var created = await _coa.CreateCostCenterAsync("ADM", "Admin");
        Assert.Equal("ADM", created.Code);
        Assert.True(created.IsActive);

        await Assert.ThrowsAsync<PostingException>(() => _coa.CreateCostCenterAsync("ADM", "Admin Again"));
    }

    [Fact]
    public async Task UpdateCostCenterAsync_renames_it()
    {
        await _coa.UpdateCostCenterAsync(_db.CostCenter.Id, "Operations & Support");
        Assert.Equal("Operations & Support", (await _coa.GetAllCostCentersAsync()).Single(c => c.Id == _db.CostCenter.Id).Name);
    }

    [Fact]
    public async Task SetCostCenterActiveAsync_toggles_it_and_GetCostCentersAsync_excludes_inactive()
    {
        await _coa.SetCostCenterActiveAsync(_db.CostCenter.Id, false);
        Assert.DoesNotContain(await _coa.GetCostCentersAsync(), c => c.Id == _db.CostCenter.Id);
        Assert.Contains(await _coa.GetAllCostCentersAsync(), c => c.Id == _db.CostCenter.Id);

        await _coa.SetCostCenterActiveAsync(_db.CostCenter.Id, true);
        Assert.Contains(await _coa.GetCostCentersAsync(), c => c.Id == _db.CostCenter.Id);
    }

    [Fact]
    public async Task DeleteCostCenterAsync_refuses_one_with_posted_entries_but_allows_an_unused_one()
    {
        await using (var db = _db.CreateDbContext())
        {
            var v = new AegisErp.Domain.Entities.JournalVoucher
            {
                VoucherNo = "JV-TEST-0001",
                Type = VoucherType.Journal,
                Date = new(2026, 5, 2),
                FiscalPeriodId = _db.May.Id,
                CreatedAtUtc = DateTime.UtcNow,
            };
            v.Lines.Add(new() { LineNo = 1, AccountId = _db.Expense.Id, CostCenterId = _db.CostCenter.Id, Debit = 10 });
            v.Lines.Add(new() { LineNo = 2, AccountId = _db.Bank.Id, Credit = 10 });
            v.Post(DateTime.UtcNow);
            db.JournalVouchers.Add(v);
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<PostingException>(() => _coa.DeleteCostCenterAsync(_db.CostCenter.Id));

        var unused = await _coa.CreateCostCenterAsync("UNUSED", "Unused");
        await _coa.DeleteCostCenterAsync(unused.Id);
        Assert.DoesNotContain(await _coa.GetAllCostCentersAsync(), c => c.Id == unused.Id);
    }
}
