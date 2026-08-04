using AegisErp.Domain;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class BalanceSheetReportTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly JournalService _journal;
    private readonly LedgerService _ledger;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public BalanceSheetReportTests()
    {
        _journal = new JournalService(_db);
        _ledger = new LedgerService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetBalanceSheetAsync_excludes_zero_balance_accounts_by_default_but_can_include_them()
    {
        // Only the Bank and Ar accounts (both Asset) ever get posted to — VatInput never does.
        await _journal.CreateAndPostAsync(VoucherType.Journal, new(2026, 5, 10), _db.May.Id, "Fund", null, "tester",
            new[]
            {
                new VoucherLineInput(_db.Bank.Id, null, "bank", 500, 0),
                new VoucherLineInput(_db.Ar.Id, null, "ar", 0, 500),
            }, Now);

        var excluded = await _ledger.GetBalanceSheetAsync(_db.May.Id);
        Assert.DoesNotContain(excluded.Assets, l => l.AccountId == _db.VatInput.Id);

        var included = await _ledger.GetBalanceSheetAsync(_db.May.Id, excludeZeroBalances: false);
        Assert.Contains(included.Assets, l => l.AccountId == _db.VatInput.Id && l.Amount == 0m);
        // Untouched accounts still show up with a zero balance rather than being fabricated with data.
        Assert.Contains(included.Assets, l => l.AccountId == _db.Bank.Id && l.Amount == 500m);
    }
}
