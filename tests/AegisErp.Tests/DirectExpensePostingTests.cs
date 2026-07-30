using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

public class DirectExpensePostingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DirectExpenseService _expenses;
    private static readonly DateTime Now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    public DirectExpensePostingTests() => _expenses = new DirectExpenseService(_db);
    public void Dispose() => _db.Dispose();

    private Task<DirectExpense> PostSimpleExpense(decimal amount = 500, int? vendorId = null, int? customerId = null) =>
        _expenses.CreateAndPostAsync(vendorId, customerId, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            "REF-001", "Fuel", "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, amount) });

    [Fact]
    public async Task Posting_a_simple_expense_generates_a_balanced_Dr_expense_Cr_bank_voucher()
    {
        var e = await PostSimpleExpense(500);

        Assert.Equal(VoucherStatus.Posted, e.Status);
        Assert.Equal(500m, e.TotalAmount);
        Assert.NotNull(e.JournalVoucher);
        Assert.Equal(e.ExpenseNo, e.JournalVoucher!.VoucherNo);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == e.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Debit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit);
    }

    [Fact]
    public async Task Itemized_expense_across_two_accounts_stays_balanced()
    {
        await using var db = _db.CreateDbContext();
        var travel = new Account { CompanyId = _db.Company.Id, Code = "51020", Name = "Travel", Type = AccountType.Expense };
        db.Accounts.Add(travel);
        await db.SaveChangesAsync();

        var e = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            null, null, "tester", Now,
            new[]
            {
                new DirectExpenseLineInput(_db.Expense.Id, null, "Office supplies", 300),
                new DirectExpenseLineInput(travel.Id, null, "Taxi", 200),
            });

        Assert.Equal(500m, e.TotalAmount);

        await using var verifyDb = _db.CreateDbContext();
        var voucher = await verifyDb.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == e.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(300m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Debit);
        Assert.Equal(200m, voucher.Lines.Single(l => l.AccountId == travel.Id).Debit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit);
    }

    [Fact]
    public async Task Expense_with_no_lines_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _expenses.CreateAndPostAsync(
            null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id, null, null, "tester", Now,
            Array.Empty<DirectExpenseLineInput>()));
    }

    [Fact]
    public async Task Expense_line_with_a_non_positive_amount_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _expenses.CreateAndPostAsync(
            null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id, null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, 0) }));
    }

    [Fact]
    public async Task Vendor_is_genuinely_optional()
    {
        var e = await PostSimpleExpense(vendorId: null);
        Assert.Null(e.VendorId);
    }

    [Fact]
    public async Task Customer_is_an_optional_tag_with_no_other_effect()
    {
        var customer = await new CustomerService(_db).CreateAsync(
            new NewCustomerInput("Acme Co", null, "AED", 0, 30, null, null, null, null));

        var e = await PostSimpleExpense(customerId: customer.Id);
        Assert.Equal(customer.Id, e.CustomerId);
    }

    [Fact]
    public async Task Sequential_expenses_get_distinct_expense_numbers()
    {
        var e1 = await PostSimpleExpense(100);
        var e2 = await PostSimpleExpense(200);
        Assert.NotEqual(e1.ExpenseNo, e2.ExpenseNo);
        Assert.StartsWith("EXP-", e1.ExpenseNo);
    }
}
