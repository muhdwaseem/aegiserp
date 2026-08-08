using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

public class DirectExpensePaymentServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DirectExpenseService _expenses;
    private readonly DirectExpensePaymentService _payments;
    private static readonly DateTime Now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    public DirectExpensePaymentServiceTests()
    {
        _expenses = new DirectExpenseService(_db);
        _payments = new DirectExpensePaymentService(_db);

        using var seedDb = _db.CreateUnscopedDbContext();
        seedDb.Accounts.Add(new Account { CompanyId = _db.Company.Id, Code = WellKnownAccounts.ExpensesPayable, Name = "Expenses Payable", Type = AccountType.Liability });
        seedDb.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Task<DirectExpense> PostPayLaterExpense(params decimal[] lineAmounts) =>
        _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, null,
            null, null, "tester", Now,
            lineAmounts.Select(a => new DirectExpenseLineInput(_db.Expense.Id, null, null, a)),
            payLater: true);

    [Fact]
    public async Task Single_line_expense_auto_allocates_the_full_payment()
    {
        var e = await PostPayLaterExpense(500);

        var payment = await _payments.CreateAndPostAsync(
            e.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 500, null, "tester", Now);

        Assert.Equal(500m, payment.Amount);
        Assert.Single(payment.Allocations);
        Assert.Equal(e.Lines[0].Id, payment.Allocations[0].DirectExpenseLineId);

        await using var db = _db.CreateDbContext();
        var payable = await db.Accounts.SingleAsync(a => a.Code == WellKnownAccounts.ExpensesPayable);
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == payment.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == payable.Id).Debit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit);

        var balances = await _payments.GetLineBalancesAsync(e.Id);
        Assert.Equal(0m, balances.Single().Balance);
    }

    [Fact]
    public async Task Multi_line_expense_can_be_paid_off_split_across_lines()
    {
        var e = await PostPayLaterExpense(300, 200);

        var payment = await _payments.CreateAndPostAsync(
            e.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 500, null, "tester", Now,
            new[]
            {
                new ExpenseLineAllocationInput(e.Lines[0].Id, 300),
                new ExpenseLineAllocationInput(e.Lines[1].Id, 200),
            });

        Assert.Equal(2, payment.Allocations.Count);
        var balances = await _payments.GetLineBalancesAsync(e.Id);
        Assert.All(balances, b => Assert.Equal(0m, b.Balance));
    }

    [Fact]
    public async Task Partial_payment_leaves_the_remaining_balance_on_the_unpaid_line()
    {
        var e = await PostPayLaterExpense(300, 200);

        await _payments.CreateAndPostAsync(
            e.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 300, null, "tester", Now,
            new[] { new ExpenseLineAllocationInput(e.Lines[0].Id, 300) });

        var balances = await _payments.GetLineBalancesAsync(e.Id);
        Assert.Equal(0m, balances.Single(b => b.LineId == e.Lines[0].Id).Balance);
        Assert.Equal(200m, balances.Single(b => b.LineId == e.Lines[1].Id).Balance);
    }

    [Fact]
    public async Task Multi_line_expense_requires_an_explicit_allocation()
    {
        var e = await PostPayLaterExpense(300, 200);

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            e.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 500, null, "tester", Now));
    }

    [Fact]
    public async Task Overallocating_a_single_line_is_rejected()
    {
        var e = await PostPayLaterExpense(300, 200);

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            e.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 500, null, "tester", Now,
            new[]
            {
                new ExpenseLineAllocationInput(e.Lines[0].Id, 350), // line only has 300
                new ExpenseLineAllocationInput(e.Lines[1].Id, 150),
            }));
    }

    [Fact]
    public async Task Paying_more_than_the_expense_total_is_rejected()
    {
        var e = await PostPayLaterExpense(500);

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            e.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 600, null, "tester", Now));
    }

    [Fact]
    public async Task Paying_an_already_fully_settled_expense_is_rejected()
    {
        var e = await PostPayLaterExpense(500);
        await _payments.CreateAndPostAsync(e.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 500, null, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            e.Id, new(2026, 6, 10), _db.Jun.Id, _db.Bank.Id, 1, null, "tester", Now));
    }

    [Fact]
    public async Task Paying_a_pay_now_expense_is_rejected()
    {
        var e = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, 500) });

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            e.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 500, null, "tester", Now));
    }

    [Fact]
    public async Task A_payment_cannot_target_another_companys_expense()
    {
        var e = await PostPayLaterExpense(500);
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            e.Id, new(2026, 6, 5), other.May.Id, other.Bank.Id, 500, null, "tester", Now));

        _db.SwitchTo(_db.Company.Id);
    }

    [Fact]
    public async Task Sequential_payments_get_distinct_payment_numbers()
    {
        var e1 = await PostPayLaterExpense(100);
        var e2 = await PostPayLaterExpense(200);

        var p1 = await _payments.CreateAndPostAsync(e1.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 100, null, "tester", Now);
        var p2 = await _payments.CreateAndPostAsync(e2.Id, new(2026, 6, 5), _db.Jun.Id, _db.Bank.Id, 200, null, "tester", Now);

        Assert.NotEqual(p1.PaymentNo, p2.PaymentNo);
        Assert.StartsWith("EXP-PMT-", p1.PaymentNo);
    }
}
