using AegisErp.Domain;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class PnlReportTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly JournalService _journal;
    private readonly ChartOfAccountsService _coa;
    private readonly LedgerService _ledger;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public PnlReportTests()
    {
        _journal = new JournalService(_db);
        _coa = new ChartOfAccountsService(_db);
        _ledger = new LedgerService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task GetProfitAndLossAsync_sections_and_subtotals_match_the_Zoho_style_layout()
    {
        var cogs = await _coa.CreateAsync(
            new NewAccountInput("51020", "Cost of Goods Sold", AccountType.Expense, true, null, "AED", null, null, 0,
                PnlSection.CostOfGoodsSold), "tester");
        var interestIncome = await _coa.CreateAsync(
            new NewAccountInput("42010", "Interest Income", AccountType.Income, true, null, "AED", null, null, 0,
                PnlSection.NonOperatingIncome), "tester");
        var interestExpense = await _coa.CreateAsync(
            new NewAccountInput("52010", "Interest Expense", AccountType.Expense, true, null, "AED", null, null, 0,
                PnlSection.NonOperatingExpense), "tester");

        // _db.Revenue/_db.Expense have no explicit PnlSection -> fall back to Operating Income/Expense.
        await _journal.CreateAndPostAsync(VoucherType.Journal, new(2026, 5, 10), _db.May.Id, "Sale", null, "tester",
            new[]
            {
                new VoucherLineInput(_db.Bank.Id, null, "bank", 1000, 0),
                new VoucherLineInput(_db.Revenue.Id, null, "revenue", 0, 1000),
            }, Now);
        await _journal.CreateAndPostAsync(VoucherType.Journal, new(2026, 5, 11), _db.May.Id, "Opex", null, "tester",
            new[]
            {
                new VoucherLineInput(_db.Expense.Id, null, "opex", 200, 0),
                new VoucherLineInput(_db.Bank.Id, null, "bank", 0, 200),
            }, Now);
        await _journal.CreateAndPostAsync(VoucherType.Journal, new(2026, 5, 12), _db.May.Id, "Cogs", null, "tester",
            new[]
            {
                new VoucherLineInput(cogs.Id, null, "cogs", 300, 0),
                new VoucherLineInput(_db.Bank.Id, null, "bank", 0, 300),
            }, Now);
        await _journal.CreateAndPostAsync(VoucherType.Journal, new(2026, 5, 13), _db.May.Id, "Interest earned", null, "tester",
            new[]
            {
                new VoucherLineInput(_db.Bank.Id, null, "bank", 50, 0),
                new VoucherLineInput(interestIncome.Id, null, "interest income", 0, 50),
            }, Now);
        await _journal.CreateAndPostAsync(VoucherType.Journal, new(2026, 5, 14), _db.May.Id, "Interest paid", null, "tester",
            new[]
            {
                new VoucherLineInput(interestExpense.Id, null, "interest expense", 20, 0),
                new VoucherLineInput(_db.Bank.Id, null, "bank", 0, 20),
            }, Now);

        var pnl = await _ledger.GetProfitAndLossAsync(new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31));

        Assert.Equal(1000m, pnl.OperatingIncomePeriod);
        Assert.Equal(300m, pnl.CostOfGoodsSoldPeriod);
        Assert.Equal(700m, pnl.GrossProfitPeriod); // 1000 - 300
        Assert.Equal(200m, pnl.OperatingExpensePeriod);
        Assert.Equal(500m, pnl.OperatingProfitPeriod); // 700 - 200
        Assert.Equal(50m, pnl.NonOperatingIncomePeriod);
        Assert.Equal(20m, pnl.NonOperatingExpensePeriod);
        Assert.Equal(530m, pnl.NetProfitPeriod); // 500 + 50 - 20

        Assert.Single(pnl.CostOfGoodsSold);
        Assert.Equal("51020", pnl.CostOfGoodsSold[0].Code);
        Assert.Single(pnl.NonOperatingIncome);
        Assert.Equal("42010", pnl.NonOperatingIncome[0].Code);
        Assert.Single(pnl.NonOperatingExpense);
        Assert.Equal("52010", pnl.NonOperatingExpense[0].Code);
    }

    [Fact]
    public async Task CreateAsync_defaults_PnlSection_by_account_type_when_not_specified()
    {
        var income = await _coa.CreateAsync(
            new NewAccountInput("43010", "Other Revenue", AccountType.Income, true, null, "AED", null, null, 0), "tester");
        var expense = await _coa.CreateAsync(
            new NewAccountInput("53010", "Other Expense", AccountType.Expense, true, null, "AED", null, null, 0), "tester");
        var asset = await _coa.CreateAsync(
            new NewAccountInput("14010", "Prepaid Rent", AccountType.Asset, true, null, "AED", null, null, 0), "tester");

        Assert.Equal(PnlSection.OperatingIncome, income.PnlSection);
        Assert.Equal(PnlSection.OperatingExpense, expense.PnlSection);
        Assert.Null(asset.PnlSection); // meaningless for non-Income/Expense accounts
    }

    [Fact]
    public async Task UpdateAsync_can_reclassify_an_accounts_PnlSection()
    {
        var expense = await _coa.CreateAsync(
            new NewAccountInput("53020", "Freight", AccountType.Expense, true, null, "AED", null, null, 0), "tester");
        Assert.Equal(PnlSection.OperatingExpense, expense.PnlSection);

        var updated = await _coa.UpdateAsync(expense.Id, expense.Name, expense.Category, expense.Currency,
            expense.ParentId, expense.Description, expense.IsActive, expense.RowVersion, "tester",
            PnlSection.CostOfGoodsSold);

        Assert.Equal(PnlSection.CostOfGoodsSold, updated.PnlSection);
    }
}
