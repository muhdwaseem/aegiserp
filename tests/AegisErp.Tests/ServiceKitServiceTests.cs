using AegisErp.Domain;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class ServiceKitServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ServiceKitService _kits;

    public ServiceKitServiceTests() => _kits = new ServiceKitService(_db);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateAsync_round_trips_a_kit_with_its_lines()
    {
        var kit = await _kits.CreateAsync("New Company Formation", new[]
        {
            new ServiceKitLineInput("Trade License Fee", _db.Revenue.Id, 5000m, 1000m, 50m, 0.05m),
            new ServiceKitLineInput("Visa Processing", _db.Revenue.Id, 3000m, 500m, 0m, 0.05m),
        });

        var all = await _kits.GetAllAsync();
        var found = Assert.Single(all, k => k.Id == kit.Id);
        Assert.Equal("New Company Formation", found.Name);
        Assert.Equal(2, found.Lines.Count);
        var first = found.Lines.OrderBy(l => l.SortOrder).First();
        Assert.Equal("Trade License Fee", first.Description);
        Assert.Equal(5000m, first.GovtFee);
        Assert.Equal(1000m, first.CenterFee);
        Assert.Equal(50m, first.BankCharge);
    }

    [Fact]
    public async Task UpdateAsync_replaces_lines_rather_than_appending()
    {
        var kit = await _kits.CreateAsync("Kit", new[]
        {
            new ServiceKitLineInput("Line A", null, 100m, 50m, 0m, 0.05m),
            new ServiceKitLineInput("Line B", null, 200m, 50m, 0m, 0.05m),
        });

        await _kits.UpdateAsync(kit.Id, "Kit Renamed", new[]
        {
            new ServiceKitLineInput("Line C", null, 300m, 50m, 0m, 0.05m),
        });

        var all = await _kits.GetAllAsync();
        var found = Assert.Single(all, k => k.Id == kit.Id);
        Assert.Equal("Kit Renamed", found.Name);
        var line = Assert.Single(found.Lines);
        Assert.Equal("Line C", line.Description);
    }

    [Fact]
    public async Task A_blank_line_row_is_ignored_not_rejected()
    {
        var kit = await _kits.CreateAsync("Kit", new[]
        {
            new ServiceKitLineInput("", null, 0m, 0m, 0m, 0.05m),
            new ServiceKitLineInput("Real Line", null, 100m, 50m, 0m, 0.05m),
        });

        var all = await _kits.GetAllAsync();
        var found = Assert.Single(all, k => k.Id == kit.Id);
        Assert.Single(found.Lines);
        Assert.Equal("Real Line", found.Lines.Single().Description);
    }

    [Fact]
    public async Task SetActiveAsync_and_DeleteAsync_work()
    {
        var kit = await _kits.CreateAsync("Kit", new[] { new ServiceKitLineInput("Line", null, 0m, 100m, 0m, 0.05m) });

        await _kits.SetActiveAsync(kit.Id, false);
        Assert.False((await _kits.GetAllAsync()).Single(k => k.Id == kit.Id).IsActive);

        await _kits.DeleteAsync(kit.Id);
        Assert.Empty(await _kits.GetAllAsync());
    }

    [Fact]
    public async Task Kits_do_not_leak_across_companies()
    {
        await _kits.CreateAsync("Kit", new[] { new ServiceKitLineInput("Line", null, 0m, 100m, 0m, 0.05m) });

        _db.SwitchTo(_db.OtherCompany.Id);
        Assert.Empty(await _kits.GetAllAsync());
    }

    [Fact]
    public async Task CreateAsync_rejects_a_blank_name()
    {
        await Assert.ThrowsAsync<PostingException>(() => _kits.CreateAsync("   "));
    }
}
