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
