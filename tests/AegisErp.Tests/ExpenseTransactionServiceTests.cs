using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

/// <summary>The AP mirror of <see cref="TransactionServiceTests"/> — same shape, sourced from
/// Purchase Invoice lines instead of Sales Invoice lines.</summary>
public class ExpenseTransactionServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly PurchaseInvoiceService _invoices;
    private readonly VendorPaymentService _payments;
    private readonly DirectExpenseService _expenses;
    private readonly DirectExpensePaymentService _expensePayments;
    private readonly ExpenseTransactionService _transactions;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public ExpenseTransactionServiceTests()
    {
        _invoices = new PurchaseInvoiceService(_db);
        _payments = new VendorPaymentService(_db);
        _expenses = new DirectExpenseService(_db);
        _expensePayments = new DirectExpensePaymentService(_db);
        _transactions = new ExpenseTransactionService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<PurchaseInvoice> PostInvoice(decimal price = 1000) =>
        _invoices.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new PurchaseLineInput("Visa stamping fee", _db.Expense.Id, null, 1, price, 0.05m) }, Now,
            lpoNo: "LPO-100");

    [Fact]
    public async Task GetAllAsync_flattens_invoice_lines_with_a_tran_ref_per_line()
    {
        var inv = await PostInvoice();

        var rows = await _transactions.GetAllAsync();

        var row = Assert.Single(rows);
        Assert.Equal($"{inv.InvoiceNo}-L1", row.TranRef);
        Assert.Equal(inv.Id, row.InvoiceId);
        Assert.Equal(_db.Vendor.Name, row.VendorName);
        Assert.Equal("LPO-100", row.LpoNo);
        Assert.Equal("Visa stamping fee", row.Description);
        Assert.False(row.IsCompleted);
        Assert.Null(row.PaidToAccountName);
    }

    [Fact]
    public async Task GetAllAsync_resolves_paid_to_account_via_line_level_payment_allocation()
    {
        var inv = await PostInvoice(1000);
        var lineId = inv.Lines.Single().Id;
        await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050m, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(lineId, 1050m) });

        var row = Assert.Single(await _transactions.GetAllAsync());

        Assert.Equal(_db.Bank.Name, row.PaidToAccountName);
    }

    [Fact]
    public async Task SetCompletionAsync_marks_and_reopens_a_line()
    {
        var inv = await PostInvoice();
        var lineId = inv.Lines.Single().Id;

        await _transactions.SetCompletionAsync(lineId, true, "Ahmed", Now);
        var completed = Assert.Single(await _transactions.GetAllAsync());
        Assert.True(completed.IsCompleted);
        Assert.Equal("Ahmed", completed.CompletedBy);
        Assert.Equal(Now, completed.CompletedAtUtc);

        await _transactions.SetCompletionAsync(lineId, false, "Ahmed", Now);
        var reopened = Assert.Single(await _transactions.GetAllAsync());
        Assert.False(reopened.IsCompleted);
        Assert.Null(reopened.CompletedBy);
        Assert.Null(reopened.CompletedAtUtc);
    }

    [Fact]
    public async Task Pay_later_direct_expenses_are_included_and_pay_now_ones_are_not()
    {
        using (var seedDb = _db.CreateUnscopedDbContext())
        {
            seedDb.Accounts.Add(new Account { CompanyId = _db.Company.Id, Code = WellKnownAccounts.ExpensesPayable, Name = "Expenses Payable", Type = AccountType.Liability });
            seedDb.SaveChanges();
        }

        await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, "Pay now expense", 500) });

        var payLater = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 11), _db.May.Id, null,
            null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, "Pay later expense", 300) },
            payLater: true);

        var rows = await _transactions.GetAllAsync();

        var row = Assert.Single(rows);
        Assert.True(row.IsDirectExpense);
        Assert.Equal($"{payLater.ExpenseNo}-L1", row.TranRef);
        Assert.Equal("Pay later expense", row.Description);
        Assert.Null(row.PaidToAccountName);
    }

    [Fact]
    public async Task Pay_later_direct_expense_resolves_paid_to_account_via_payment_allocation()
    {
        using (var seedDb = _db.CreateUnscopedDbContext())
        {
            seedDb.Accounts.Add(new Account { CompanyId = _db.Company.Id, Code = WellKnownAccounts.ExpensesPayable, Name = "Expenses Payable", Type = AccountType.Liability });
            seedDb.SaveChanges();
        }

        var e = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 11), _db.May.Id, null,
            null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, "Pay later expense", 300) },
            payLater: true);
        await _expensePayments.CreateAndPostAsync(e.Id, new(2026, 6, 1), _db.Jun.Id, _db.Bank.Id, 300, null, "tester", Now);

        var row = Assert.Single(await _transactions.GetAllAsync());
        Assert.Equal(_db.Bank.Name, row.PaidToAccountName);
    }

    [Fact]
    public async Task Transactions_of_another_company_are_not_visible_or_writable()
    {
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        // OtherCompany's fixture doesn't seed a VAT Input account, so use a zero VAT rate here —
        // irrelevant to what this test is proving (cross-company isolation).
        var theirs = await _invoices.CreateAndPostAsync(other.Vendor.Id, null, new(2026, 5, 12), other.May.Id, null, "tester",
            new[] { new PurchaseLineInput("Service", other.Expense.Id, null, 1, 500, 0m) }, Now);
        _db.SwitchTo(_db.Company.Id);

        var rows = await _transactions.GetAllAsync();
        Assert.Empty(rows);

        var theirLineId = theirs.Lines.Single().Id;
        await Assert.ThrowsAsync<PostingException>(() => _transactions.SetCompletionAsync(theirLineId, true, "tester", Now));
    }
}
