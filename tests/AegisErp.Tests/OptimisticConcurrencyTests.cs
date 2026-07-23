using AegisErp.Domain;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

/// <summary>
/// Proves the lost-update guard: when two users load the same record and both try to save an
/// edit, the second save must fail cleanly instead of silently overwriting the first.
/// </summary>
public class OptimisticConcurrencyTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ChartOfAccountsService _coa;
    private readonly CompanyService _companies;

    public OptimisticConcurrencyTests()
    {
        _coa = new ChartOfAccountsService(_db);
        _companies = new CompanyService(_db, new CurrentCompany { CompanyId = _db.Company.Id });
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Second_account_edit_with_a_stale_row_version_is_rejected()
    {
        // Both "editors" load the account before either saves.
        var firstView = (await _coa.GetAllAsync()).Single(a => a.Id == _db.Bank.Id);
        var secondView = (await _coa.GetAllAsync()).Single(a => a.Id == _db.Bank.Id);

        await _coa.UpdateAsync(_db.Bank.Id, "Bank — renamed by first user", null, "AED",
            null, null, true, firstView.RowVersion, "first-user");

        var ex = await Assert.ThrowsAsync<PostingException>(() =>
            _coa.UpdateAsync(_db.Bank.Id, "Bank — renamed by second user", null, "AED",
                null, null, true, secondView.RowVersion, "second-user"));

        Assert.Contains("changed by another user", ex.Message);

        var stored = (await _coa.GetAllAsync()).Single(a => a.Id == _db.Bank.Id);
        Assert.Equal("Bank — renamed by first user", stored.Name);
        Assert.Equal("first-user", stored.UpdatedBy);
    }

    [Fact]
    public async Task Second_company_setup_save_with_a_stale_row_version_is_rejected()
    {
        var firstView = await _companies.GetAsync();
        var secondView = await _companies.GetAsync();

        firstView.LegalName = "Renamed by first user";
        await _companies.SaveAsync(firstView, "first-user");

        secondView.LegalName = "Renamed by second user";
        var ex = await Assert.ThrowsAsync<PostingException>(() =>
            _companies.SaveAsync(secondView, "second-user"));

        Assert.Contains("changed by another user", ex.Message);

        var stored = await _companies.GetAsync();
        Assert.Equal("Renamed by first user", stored.LegalName);
        Assert.Equal("first-user", stored.UpdatedBy);
    }
}
