using AegisErp.Domain;
using AegisErp.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AegisErp.Tests;

public class ReportsTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ReportsService _reports;
    private readonly SalesInvoiceService _invoices;
    private readonly ReceiptService _receipts;
    private readonly PurchaseInvoiceService _purchases;
    private readonly VendorPaymentService _payments;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public ReportsTests()
    {
        _reports = new ReportsService(_db);
        _invoices = new SalesInvoiceService(_db, new EmailService(Options.Create(new SmtpOptions())));
        _receipts = new ReceiptService(_db);
        _purchases = new PurchaseInvoiceService(_db);
        _payments = new VendorPaymentService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task PostSale(decimal price = 1000) =>
        _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, _db.CostCenter.Id, 1, price, 0.05m) }, Now);

    private Task PostPurchase(decimal price = 400) =>
        _purchases.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 11), _db.May.Id, null, "tester",
            new[] { new PurchaseLineInput("Supplies", _db.Expense.Id, _db.CostCenter.Id, 1, price, 0.05m) }, Now);

    [Fact]
    public async Task Cash_flow_reconciles_to_the_actual_cash_movement()
    {
        await PostSale();
        await PostPurchase();
        await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 600, null, "tester", Now);
        await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 16), _db.May.Id,
            _db.Bank.Id, 250, null, "tester", Now);

        var cf = await _reports.GetCashFlowAsync(_db.May.Id);

        Assert.True(cf.IsReconciled);
        Assert.Equal(cf.ActualCashMovement, cf.NetChange);
        // Cash in 600, cash out 250 → net +350.
        Assert.Equal(350m, cf.NetChange);
        Assert.Equal(350m, cf.ClosingCash - cf.OpeningCash);
    }

    [Fact]
    public async Task Cash_flow_operating_section_starts_from_net_profit()
    {
        await PostSale(price: 1000);   // revenue 1000
        await PostPurchase(price: 400); // expense 400

        var cf = await _reports.GetCashFlowAsync(_db.May.Id);

        var netProfitLine = cf.Operating.Lines.First();
        Assert.Equal("Net profit for the period", netProfitLine.Label);
        Assert.Equal(600m, netProfitLine.Amount); // 1000 revenue − 400 expense
    }

    [Fact]
    public async Task Cash_flow_with_no_activity_is_still_reconciled()
    {
        var cf = await _reports.GetCashFlowAsync(_db.May.Id);

        Assert.True(cf.IsReconciled);
        Assert.Equal(0m, cf.NetChange);
        Assert.Empty(cf.Investing.Lines);
        Assert.Empty(cf.Financing.Lines);
    }

    [Fact]
    public async Task Segment_pnl_groups_revenue_and_expense_by_cost_centre()
    {
        await PostSale(price: 1000);
        await PostPurchase(price: 400);

        var rows = await _reports.GetSegmentPnlAsync(new(2026, 5, 1), new(2026, 5, 31));

        var seg = rows.Single(r => r.Code == _db.CostCenter.Code);
        Assert.Equal(1000m, seg.Revenue);
        Assert.Equal(400m, seg.Expense);
        Assert.Equal(600m, seg.Net);
    }

    [Fact]
    public async Task Segment_pnl_excludes_activity_outside_the_date_range()
    {
        await PostSale(); // dated 2026-05-10

        var outside = await _reports.GetSegmentPnlAsync(new(2026, 6, 1), new(2026, 6, 30));
        Assert.Empty(outside);
    }

    [Fact]
    public async Task Customer_revenue_is_net_of_VAT()
    {
        await PostSale(price: 1000); // net 1000, gross 1050

        var rows = await _reports.GetCustomerRevenueAsync(new(2026, 5, 1), new(2026, 5, 31));

        var row = rows.Single(r => r.Code == _db.Customer.Code);
        Assert.Equal(1000m, row.Amount); // excludes the 50 VAT
        Assert.Equal(1, row.DocumentCount);
    }

    [Fact]
    public async Task Vendor_spend_is_net_of_VAT()
    {
        await PostPurchase(price: 400); // net 400, gross 420

        var rows = await _reports.GetVendorSpendAsync(new(2026, 5, 1), new(2026, 5, 31));

        var row = rows.Single(r => r.Code == _db.Vendor.Code);
        Assert.Equal(400m, row.Amount);
        Assert.Equal(1, row.DocumentCount);
    }

    [Fact]
    public async Task Customer_revenue_is_reduced_by_credit_notes()
    {
        await PostSale(price: 1000);
        var creditNotes = new CreditNoteService(_db);
        await creditNotes.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 20), _db.May.Id,
            "Discount", null, "tester",
            new[] { new CreditNoteLineInput("Discount", _db.Revenue.Id, null, 1, 150, 0.05m) }, Now);

        var rows = await _reports.GetCustomerRevenueAsync(new(2026, 5, 1), new(2026, 5, 31));

        Assert.Equal(850m, rows.Single(r => r.Code == _db.Customer.Code).Amount); // 1000 − 150
    }

    // --- Salesperson revenue: attribution follows who owned the customer on each document's own date ---

    [Fact]
    public async Task Salesperson_revenue_credits_each_invoice_to_whoever_owned_the_customer_at_the_time()
    {
        var customers = new CustomerService(_db);

        // Alice owns the customer from 1 May; an invoice on 10 May is hers.
        await customers.UpdateAsync(_db.Customer.Id,
            new NewCustomerInput(_db.Customer.Name, null, "AED", 0, 30, null, null, null, null, "Alice"),
            changedBy: "tester", nowUtc: new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, _db.CostCenter.Id, 1, 1000, 0.05m) }, Now);

        // Ownership moves to Bob on 1 June; an invoice on 10 June is his — not retroactively Alice's or Bob's for the May one.
        await customers.UpdateAsync(_db.Customer.Id,
            new NewCustomerInput(_db.Customer.Name, null, "AED", 0, 30, null, null, null, null, "Bob"),
            changedBy: "tester", nowUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 6, 10), _db.Jun.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, _db.CostCenter.Id, 1, 2000, 0.05m) }, Now);

        var rows = await _reports.GetSalespersonRevenueAsync(new(2026, 1, 1), new(2026, 12, 31));

        Assert.Equal(1000m, rows.Single(r => r.Salesperson == "Alice").Amount);
        Assert.Equal(2000m, rows.Single(r => r.Salesperson == "Bob").Amount);
    }

    [Fact]
    public async Task Salesperson_revenue_groups_customers_with_no_history_under_their_current_assignment()
    {
        // _db.Customer is seeded directly (bypassing CustomerService), so it has no history rows at all.
        await using (var db = _db.CreateUnscopedDbContext())
        {
            var customer = await db.Customers.FindAsync(_db.Customer.Id);
            customer!.Salesperson = "Carol";
            await db.SaveChangesAsync();
        }
        await PostSale(price: 500);

        var rows = await _reports.GetSalespersonRevenueAsync(new(2026, 5, 1), new(2026, 5, 31));

        Assert.Equal(500m, rows.Single(r => r.Salesperson == "Carol").Amount);
    }

    [Fact]
    public async Task Salesperson_revenue_groups_customers_with_no_salesperson_as_unassigned()
    {
        await PostSale(price: 300);

        var rows = await _reports.GetSalespersonRevenueAsync(new(2026, 5, 1), new(2026, 5, 31));

        Assert.Equal(300m, rows.Single(r => r.Salesperson == "Unassigned").Amount);
    }
}
