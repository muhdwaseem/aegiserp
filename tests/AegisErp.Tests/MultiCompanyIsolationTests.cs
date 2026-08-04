using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
    private readonly PurchaseInvoiceService _purchaseInvoices;
    private readonly ReceiptService _receipts;
    private readonly TagService _tags;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public MultiCompanyIsolationTests()
    {
        _invoices = new SalesInvoiceService(_db, new EmailService(Options.Create(new SmtpOptions())));
        _customers = new CustomerService(_db);
        _coa = new ChartOfAccountsService(_db);
        _purchaseInvoices = new PurchaseInvoiceService(_db);
        _receipts = new ReceiptService(_db);
        _tags = new TagService(_db);
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
    public async Task Invoice_line_attachment_methods_reject_a_lineId_from_another_company()
    {
        // SalesInvoiceLine has no CompanyId/query filter of its own — these methods must route
        // through the company-scoped SalesInvoices, not query db.SalesInvoiceLines directly by id.
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var theirs = await _invoices.CreateAndPostAsync(other.Customer.Id, new(2026, 5, 12), other.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", other.Revenue.Id, null, 1, 500, 0.05m) }, Now);
        _db.SwitchTo(_db.Company.Id);

        var theirLineId = theirs.Lines.Single().Id;

        await Assert.ThrowsAsync<PostingException>(() =>
            _invoices.SetLineAttachmentAsync(theirLineId, "spec.pdf", "application/pdf", new byte[] { 1, 2, 3 }));
        await Assert.ThrowsAsync<PostingException>(() => _invoices.RemoveLineAttachmentAsync(theirLineId));
        Assert.Null(await _invoices.GetLineAttachmentAsync(theirLineId));
    }

    [Fact]
    public async Task GetLineBalancesAsync_rejects_a_sales_invoiceId_from_another_company()
    {
        // SalesInvoiceLine has no CompanyId/query filter of its own — GetLineBalancesAsync must
        // route through the company-scoped SalesInvoices, not query db.SalesInvoiceLines directly.
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var theirs = await _invoices.CreateAndPostAsync(other.Customer.Id, new(2026, 5, 12), other.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", other.Revenue.Id, null, 1, 500, 0.05m) }, Now);
        _db.SwitchTo(_db.Company.Id);

        var balances = await _invoices.GetLineBalancesAsync(theirs.Id);

        Assert.Empty(balances);
    }

    [Fact]
    public async Task GetLineBalancesAsync_rejects_a_purchase_invoiceId_from_another_company()
    {
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var theirs = await _purchaseInvoices.CreateAndPostAsync(other.Vendor.Id, null, new(2026, 5, 12), other.May.Id,
            null, "tester", new[] { new PurchaseLineInput("Bill", other.Expense.Id, null, 1, 500, 0m) }, Now);
        _db.SwitchTo(_db.Company.Id);

        var balances = await _purchaseInvoices.GetLineBalancesAsync(theirs.Id);

        Assert.Empty(balances);
    }

    [Fact]
    public async Task Customer_document_methods_reject_a_customerId_from_another_company()
    {
        // CustomerDocument has no CompanyId/query filter of its own — every method taking a raw
        // customerId must validate it against the company-scoped Customers first.
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var theirDoc = await _customers.AddDocumentAsync(other.Customer.Id, "trade-license.pdf", "application/pdf",
            new byte[] { 1, 2, 3 }, Now);
        _db.SwitchTo(_db.Company.Id);

        await Assert.ThrowsAsync<PostingException>(() => _customers.GetDocumentsAsync(other.Customer.Id));
        Assert.Null(await _customers.GetDocumentAsync(other.Customer.Id, theirDoc.Id));
        await Assert.ThrowsAsync<PostingException>(() => _customers.RemoveDocumentAsync(other.Customer.Id, theirDoc.Id));
    }

    [Fact]
    public async Task Tag_admin_methods_reject_a_tagId_from_another_company()
    {
        // Tag has no CompanyId/query filter of its own — TagService must route through the
        // company-scoped TagGroups, not query db.Tags directly by id.
        _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var group = await _tags.CreateGroupAsync(new NewTagGroupInput("Customer", "Region", 1));
        var theirTag = await _tags.AddTagAsync(group.Id, new NewTagInput("EMEA", 1));
        _db.SwitchTo(_db.Company.Id);

        await Assert.ThrowsAsync<PostingException>(() => _tags.UpdateTagAsync(theirTag.Id, new NewTagInput("Hijacked", 1)));
        await Assert.ThrowsAsync<PostingException>(() => _tags.SetTagActiveAsync(theirTag.Id, false));
        await Assert.ThrowsAsync<PostingException>(() => _tags.DeleteTagAsync(theirTag.Id));
    }

    [Fact]
    public async Task Creating_a_customer_silently_ignores_a_tagId_from_another_company()
    {
        _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var group = await _tags.CreateGroupAsync(new NewTagGroupInput("Customer", "Region", 1));
        var theirTag = await _tags.AddTagAsync(group.Id, new NewTagInput("EMEA", 1));
        _db.SwitchTo(_db.Company.Id);

        var created = await _customers.CreateAsync(
            new NewCustomerInput("Tag Test", null, "AED", 0, 30, null, null, null, null),
            tagIds: new[] { theirTag.Id });

        var full = await _customers.GetByIdAsync(created.Id);
        Assert.Empty(full!.Tags);
    }

    [Fact]
    public async Task Receipt_allocation_does_not_leak_another_companys_invoice_number_for_a_foreign_lineId()
    {
        // ValidateMultiInvoiceAsync must route through the company-scoped SalesInvoices — otherwise
        // a lineId from another company resolves far enough to leak that company's invoice number
        // in the "belongs to a different customer" exception message.
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var theirs = await _invoices.CreateAndPostAsync(other.Customer.Id, new(2026, 5, 12), other.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", other.Revenue.Id, null, 1, 500, 0.05m) }, Now);
        _db.SwitchTo(_db.Company.Id);
        var theirLineId = theirs.Lines.Single().Id;

        var ex = await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, null, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 500, null, "tester", Now,
            allocations: new[] { new ReceiptLineAllocationInput(theirLineId, 500) }));

        Assert.DoesNotContain(theirs.InvoiceNo, ex.Message);
        Assert.Equal("An allocation refers to a line that doesn't exist.", ex.Message);
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
