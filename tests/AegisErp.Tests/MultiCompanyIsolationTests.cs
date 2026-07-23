using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

/// <summary>
/// Proves the isolation guarantee that multi-company hinges on: while working in one company you
/// cannot read, number against, or write into another company's books.
/// </summary>
public class MultiCompanyIsolationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SalesInvoiceService _invoices;
    private readonly CustomerService _customers;
    private readonly ChartOfAccountsService _coa;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public MultiCompanyIsolationTests()
    {
        _invoices = new SalesInvoiceService(_db);
        _customers = new CustomerService(_db);
        _coa = new ChartOfAccountsService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<SalesInvoice> PostInvoice(decimal price = 1000) =>
        _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, price, 0.05m) }, Now);

    [Fact]
    public async Task Customers_of_another_company_are_not_visible()
    {
        _db.SeedOtherCompany(); // creates "Other Customer" in the second company

        var mine = await _customers.GetAllAsync();

        Assert.Single(mine);
        Assert.Equal("Test Customer", mine[0].Name);
        Assert.DoesNotContain(mine, c => c.Name == "Other Customer");
    }

    [Fact]
    public async Task Chart_of_accounts_is_per_company()
    {
        _db.SeedOtherCompany(); // same account codes, different company

        var mine = await _coa.GetAllAsync();

        Assert.All(mine, a => Assert.Equal(_db.Company.Id, a.CompanyId));
        // Both companies have an 11020, but this company sees exactly one of them.
        Assert.Single(mine.Where(a => a.Code == "11020"));
    }

    [Fact]
    public async Task The_same_account_code_can_exist_in_two_companies()
    {
        // The other company is seeded with the identical codes; if the unique index were global
        // rather than per company, this would already have thrown.
        var other = _db.SeedOtherCompany();

        await using var db = _db.CreateUnscopedDbContext();
        var elevenOhTwenties = await db.Accounts.Where(a => a.Code == "11020").ToListAsync();

        Assert.Equal(2, elevenOhTwenties.Count);
        Assert.Equal(new[] { _db.Company.Id, _db.OtherCompany.Id }.OrderBy(x => x),
                     elevenOhTwenties.Select(a => a.CompanyId).OrderBy(x => x));
        Assert.Equal(_db.OtherCompany.Id, other.Bank.CompanyId);
    }

    [Fact]
    public async Task Document_numbering_restarts_in_each_company()
    {
        var mine = await PostInvoice();
        Assert.Equal("INV-2026-0001", mine.InvoiceNo);

        // Switch to the other company and post its first invoice.
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);

        var theirs = await _invoices.CreateAndPostAsync(
            other.Customer.Id, new(2026, 5, 12), other.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", other.Revenue.Id, null, 1, 500, 0.05m) }, Now);

        // Same number, different company — numbering is per company, not global.
        Assert.Equal("INV-2026-0001", theirs.InvoiceNo);
        Assert.Equal(_db.OtherCompany.Id, theirs.CompanyId);
    }

    [Fact]
    public async Task Invoices_and_ledger_of_another_company_are_not_visible()
    {
        await PostInvoice(price: 1000);

        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        await _invoices.CreateAndPostAsync(other.Customer.Id, new(2026, 5, 12), other.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", other.Revenue.Id, null, 1, 7777, 0.05m) }, Now);

        // Back in the first company: only its own invoice is visible.
        _db.SwitchTo(_db.Company.Id);
        var visible = await _invoices.GetRecentAsync(50);

        Assert.Single(visible);
        Assert.Equal(1000m, visible[0].TotalNet);

        // And its trial balance contains none of the other company's 7,777.
        var tb = await new LedgerService(_db).GetTrialBalanceAsync(_db.May.Id);
        Assert.True(tb.IsBalanced);
        Assert.DoesNotContain(tb.Rows, r => r.Debit == 7777m || r.Credit == 7777m);
    }

    [Fact]
    public async Task Reports_only_cover_the_active_company()
    {
        await PostInvoice(price: 1000);

        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        await _invoices.CreateAndPostAsync(other.Customer.Id, new(2026, 5, 12), other.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", other.Revenue.Id, null, 1, 4321, 0.05m) }, Now);

        _db.SwitchTo(_db.Company.Id);
        var reports = new ReportsService(_db);

        var revenue = await reports.GetCustomerRevenueAsync(new(2026, 5, 1), new(2026, 5, 31));
        Assert.Single(revenue);
        Assert.Equal(1000m, revenue[0].Amount);

        var cf = await reports.GetCashFlowAsync(_db.May.Id);
        Assert.True(cf.IsReconciled);
        Assert.Equal(1000m, cf.Operating.Lines.First().Amount); // net profit = this company's only sale
    }

    [Fact]
    public async Task Writing_a_row_belonging_to_another_company_is_blocked()
    {
        // Arrange a customer owned by the other company, then try to modify it while scoped here.
        var other = _db.SeedOtherCompany();

        await using var db = _db.CreateDbContext(); // scoped to the primary company
        var foreign = new Customer
        {
            CompanyId = _db.OtherCompany.Id,
            Code = "C-9999",
            Name = "Smuggled In",
            PaymentTermsDays = 30,
        };
        db.Customers.Add(foreign);

        var ex = await Assert.ThrowsAsync<PostingException>(() => db.SaveChangesAsync());
        Assert.Contains("Cross-company write blocked", ex.Message);
    }

    [Fact]
    public async Task New_rows_are_stamped_with_the_active_company_automatically()
    {
        // The service never sets CompanyId; the DbContext stamps it on save.
        var created = await _customers.CreateAsync(
            new NewCustomerInput("Auto Stamped", null, "AED", 0, 30, null, null, null, null));

        Assert.Equal(_db.Company.Id, created.CompanyId);
    }

    [Fact]
    public async Task An_unscoped_context_sees_every_company()
    {
        await PostInvoice();
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        await _invoices.CreateAndPostAsync(other.Customer.Id, new(2026, 5, 12), other.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", other.Revenue.Id, null, 1, 500, 0.05m) }, Now);

        // Firm-wide/administrative view: both companies' invoices are present.
        await using var db = _db.CreateUnscopedDbContext();
        var all = await db.SalesInvoices.ToListAsync();

        Assert.Equal(2, all.Count);
        Assert.Equal(2, all.Select(i => i.CompanyId).Distinct().Count());
    }
}
