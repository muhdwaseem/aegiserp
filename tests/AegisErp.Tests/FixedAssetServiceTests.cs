using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

public class FixedAssetServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FixedAssetService _assets;
    private readonly Account _assetAccount;
    private readonly Account _accDep;
    private static readonly DateTime Now = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    public FixedAssetServiceTests()
    {
        _assets = new FixedAssetService(_db);

        using var db = _db.CreateUnscopedDbContext();
        _assetAccount = new Account { CompanyId = _db.Company.Id, Code = "16010", Name = "Fixed Assets — Cost", Type = AccountType.Asset };
        _accDep = new Account { CompanyId = _db.Company.Id, Code = WellKnownAccounts.AccumulatedDepreciation, Name = "Accumulated Depreciation", Type = AccountType.Asset };
        db.Accounts.AddRange(_assetAccount, _accDep);
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Task<FixedAsset> CreateAsset(decimal cost = 12000, decimal salvage = 0, int lifeMonths = 12) =>
        _assets.CreateAsync(
            new FixedAssetInput("Office Laptop", "IT Equipment", new(2026, 1, 1), cost, salvage, lifeMonths,
                _assetAccount.Id, _db.Expense.Id, null),
            "tester", Now);

    [Fact]
    public async Task CreateAsync_assigns_sequential_asset_codes_and_does_not_post_to_gl()
    {
        var first = await CreateAsset();
        var second = await CreateAsset();

        Assert.Equal("FA-0001", first.AssetCode);
        Assert.Equal("FA-0002", second.AssetCode);

        await using var db = _db.CreateDbContext();
        Assert.Empty(await db.JournalVouchers.ToListAsync());
    }

    [Fact]
    public async Task NetBookValue_and_AccumulatedDepreciation_start_at_full_cost()
    {
        var asset = await CreateAsset(cost: 12000);

        Assert.Equal(0m, asset.AccumulatedDepreciation);
        Assert.Equal(12000m, asset.NetBookValue);
        Assert.Equal(1000m, asset.NextDepreciationAmount()); // 12000 / 12 months
    }

    [Fact]
    public async Task RunDepreciationAsync_posts_one_voucher_debiting_expense_and_crediting_accumulated_depreciation()
    {
        var asset = await CreateAsset(cost: 12000, lifeMonths: 12);

        var voucher = await _assets.RunDepreciationAsync(_db.May.Id, "tester", Now);

        Assert.NotNull(voucher);
        var expenseLine = voucher!.Lines.Single(l => l.AccountId == _db.Expense.Id);
        var accDepLine = voucher.Lines.Single(l => l.AccountId == _accDep.Id);
        Assert.Equal(1000m, expenseLine.Debit);
        Assert.Equal(1000m, accDepLine.Credit);
        Assert.Contains(asset.AssetCode, expenseLine.Description);

        var reloaded = await _assets.GetByIdAsync(asset.Id);
        Assert.Equal(1000m, reloaded!.AccumulatedDepreciation);
        Assert.Equal(11000m, reloaded.NetBookValue);
    }

    [Fact]
    public async Task RunDepreciationAsync_is_idempotent_for_the_same_period()
    {
        await CreateAsset(cost: 12000, lifeMonths: 12);

        var first = await _assets.RunDepreciationAsync(_db.May.Id, "tester", Now);
        var second = await _assets.RunDepreciationAsync(_db.May.Id, "tester", Now);

        Assert.NotNull(first);
        Assert.Null(second); // nothing left due for May — no zero-line voucher attempted
    }

    [Fact]
    public async Task RunDepreciationAsync_stops_at_salvage_value_once_fully_depreciated()
    {
        // 2000 depreciable over 2 months = 1000/month; May then Jun fully depreciates it.
        await CreateAsset(cost: 2500, salvage: 500, lifeMonths: 2);

        await _assets.RunDepreciationAsync(_db.May.Id, "tester", Now);
        var afterJun = await _assets.RunDepreciationAsync(_db.Jun.Id, "tester", Now);
        Assert.NotNull(afterJun);

        var asset = (await _assets.GetAllAsync()).Single();
        Assert.Equal(2000m, asset.AccumulatedDepreciation); // never exceeds cost - salvage
        Assert.Equal(500m, asset.NetBookValue);              // floors at salvage value
        Assert.Equal(0m, asset.NextDepreciationAmount());

        // A third period has nothing left to depreciate.
        var third = await _assets.RunDepreciationAsync(_db.Jun.Id, "tester", Now);
        Assert.Null(third);
    }

    [Fact]
    public async Task Assets_of_another_company_are_not_visible_or_depreciated()
    {
        var other = _db.SeedOtherCompany();
        using (var db = _db.CreateUnscopedDbContext())
        {
            var otherAssetAccount = new Account { CompanyId = _db.OtherCompany.Id, Code = "16010", Name = "Fixed Assets", Type = AccountType.Asset };
            var otherAccDep = new Account { CompanyId = _db.OtherCompany.Id, Code = WellKnownAccounts.AccumulatedDepreciation, Name = "Accumulated Depreciation", Type = AccountType.Asset };
            db.Accounts.AddRange(otherAssetAccount, otherAccDep);
            db.SaveChanges();

            _db.SwitchTo(_db.OtherCompany.Id);
            var otherAssets = new FixedAssetService(_db);
            await otherAssets.CreateAsync(
                new FixedAssetInput("Their Laptop", null, new(2026, 1, 1), 6000, 0, 12, otherAssetAccount.Id, other.Expense.Id, null),
                "tester", Now);
            _db.SwitchTo(_db.Company.Id);
        }

        var ours = await _assets.GetAllAsync();
        Assert.Empty(ours);
    }
}
