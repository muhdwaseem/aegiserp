using AegisErp.Domain;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class FiscalPeriodTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly FiscalPeriodService _periods;
    private readonly CompanyService _companies;

    public FiscalPeriodTests()
    {
        _periods = new FiscalPeriodService(_db);
        _companies = new CompanyService(_db, new CurrentCompany { CompanyId = _db.Company.Id });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task CreateAsync_on_a_new_company_generates_its_first_twelve_monthly_periods()
    {
        var company = await _companies.CreateAsync(new()
        {
            LegalName = "New Client LLC",
            CompanyCode = "NEWCO",
            BaseCurrency = "AED",
            FinancialYearStart = new DateOnly(2027, 1, 1),
        });

        _db.SwitchTo(company.Id);
        var generated = await _periods.GetAllAsync();

        Assert.Equal(12, generated.Count);
        Assert.All(generated, p => Assert.Equal(company.Id, p.CompanyId));
        Assert.Equal(new DateOnly(2027, 1, 1), generated[0].StartDate);
        Assert.Equal(new DateOnly(2027, 1, 31), generated[0].EndDate);
        Assert.Equal(new DateOnly(2027, 12, 1), generated[11].StartDate);
        Assert.Equal(new DateOnly(2027, 12, 31), generated[11].EndDate);
    }

    [Fact]
    public async Task CreateAsync_without_a_financial_year_start_leaves_periods_empty_instead_of_guessing()
    {
        var company = await _companies.CreateAsync(new()
        {
            LegalName = "No Year Yet LLC",
            CompanyCode = "NOYEAR",
            BaseCurrency = "AED",
        });

        _db.SwitchTo(company.Id);
        Assert.Empty(await _periods.GetAllAsync());
    }

    [Fact]
    public async Task GenerateMonthlyYearAsync_rejects_a_year_that_already_has_periods()
    {
        // TestDb already seeds May/Jun 2026 for the primary company (see TestDb.cs).
        await Assert.ThrowsAsync<PostingException>(() => _periods.GenerateMonthlyYearAsync(new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public async Task CreateAsync_rejects_a_period_overlapping_an_existing_one()
    {
        await _periods.CreateAsync("Jan 2026", 2026, 1, new(2026, 1, 1), new(2026, 1, 31));
        await Assert.ThrowsAsync<PostingException>(() =>
            _periods.CreateAsync("Late Jan 2026", 2026, 1, new(2026, 1, 15), new(2026, 2, 5)));
    }

    [Fact]
    public async Task SetClosedAsync_toggles_the_period()
    {
        // TestDb's primary company already has May/Jun 2026 seeded, so pick a distinct period.
        var created = await _periods.CreateAsync("Jan 2027", 2027, 1, new(2027, 1, 1), new(2027, 1, 31));

        await _periods.SetClosedAsync(created.Id, true);
        Assert.True((await _periods.GetAllAsync()).Single(p => p.Id == created.Id).IsClosed);

        await _periods.SetClosedAsync(created.Id, false);
        Assert.False((await _periods.GetAllAsync()).Single(p => p.Id == created.Id).IsClosed);
    }

    [Fact]
    public async Task GetAllAsync_does_not_leak_periods_across_companies()
    {
        var mine = await _periods.CreateAsync("Jan 2027", 2027, 1, new(2027, 1, 1), new(2027, 1, 31));

        // SeedOtherCompany seeds its own May 2026 period — the point is that MY period above
        // must not show up over there, not that the other company has none at all.
        _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);

        var otherPeriods = await new FiscalPeriodService(_db).GetAllAsync();
        Assert.DoesNotContain(otherPeriods, p => p.Id == mine.Id);
    }
}
