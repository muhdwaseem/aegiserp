using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class PayrollServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EmployeeService _employees;
    private readonly PayrollService _payroll;
    private readonly Account _salariesPayable;
    private readonly Account _deductionsClearing;
    private static readonly DateTime Now = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    public PayrollServiceTests()
    {
        _employees = new EmployeeService(_db);
        _payroll = new PayrollService(_db);

        using var db = _db.CreateUnscopedDbContext();
        _salariesPayable = new Account { CompanyId = _db.Company.Id, Code = WellKnownAccounts.SalariesPayable, Name = "Salaries Payable", Type = AccountType.Liability };
        _deductionsClearing = new Account { CompanyId = _db.Company.Id, Code = "21030", Name = "Payroll Deductions — Clearing", Type = AccountType.Liability };
        db.Accounts.AddRange(_salariesPayable, _deductionsClearing);
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Task<Employee> CreateEmployee(string name = "Ahmed", decimal basic = 5000, decimal housing = 1500) =>
        _employees.CreateAsync(
            new EmployeeInput(name, null, null, new(2026, 1, 1), basic, housing, 0, 0, null, null, null, null, _db.Expense.Id, null),
            "tester", Now);

    [Fact]
    public async Task CreateDraftRunAsync_snapshots_one_line_per_active_employee()
    {
        var e1 = await CreateEmployee("Ahmed", 5000, 1500);
        var e2 = await CreateEmployee("Fatima", 6000, 2000);

        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);

        Assert.Equal(VoucherStatus.Draft, run.Status);
        Assert.Equal(2, run.Lines.Count);
        Assert.Equal(6500m + 8000m, run.TotalGross);
        Assert.Equal(run.TotalGross, run.TotalNet); // no deductions yet
    }

    [Fact]
    public async Task CreateDraftRunAsync_excludes_terminated_employees()
    {
        var active = await CreateEmployee("Ahmed");
        var terminated = await CreateEmployee("Fatima");
        await _employees.SetStatusAsync(terminated.Id, EmployeeStatus.Terminated, new(2026, 4, 30));

        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);

        var line = Assert.Single(run.Lines);
        Assert.Equal(active.Id, line.EmployeeId);
    }

    [Fact]
    public async Task CreateDraftRunAsync_rejects_a_second_run_for_the_same_period()
    {
        await CreateEmployee();
        await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() =>
            _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now));
    }

    [Fact]
    public async Task CreateDraftRunAsync_rejects_when_no_active_employees_exist()
    {
        await Assert.ThrowsAsync<PostingException>(() =>
            _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now));
    }

    [Fact]
    public async Task UpdateRunLineAsync_adjusts_deductions_and_recomputes_net_pay()
    {
        await CreateEmployee("Ahmed", 5000, 0);
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);
        var line = run.Lines.Single();

        await _payroll.UpdateRunLineAsync(line.Id, 500);

        var reloaded = await _payroll.GetByIdAsync(run.Id);
        var reloadedLine = reloaded!.Lines.Single();
        Assert.Equal(500m, reloadedLine.Deductions);
        Assert.Equal(4500m, reloadedLine.NetPay);
    }

    [Fact]
    public async Task UpdateRunLineAsync_rejects_deductions_exceeding_gross_pay()
    {
        await CreateEmployee("Ahmed", 5000, 0);
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);
        var line = run.Lines.Single();

        await Assert.ThrowsAsync<PostingException>(() => _payroll.UpdateRunLineAsync(line.Id, 5001));
        await Assert.ThrowsAsync<PostingException>(() => _payroll.UpdateRunLineAsync(line.Id, -1));
    }

    [Fact]
    public async Task PostRunAsync_with_no_deductions_debits_expense_credits_salaries_payable_only()
    {
        await CreateEmployee("Ahmed", 5000, 1500); // gross 6500
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);

        var voucher = await _payroll.PostRunAsync(run.Id, deductionsAccountId: null, "tester", Now);

        Assert.Equal(6500m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Debit);
        Assert.Equal(6500m, voucher.Lines.Single(l => l.AccountId == _salariesPayable.Id).Credit);
        Assert.DoesNotContain(voucher.Lines, l => l.AccountId == _deductionsClearing.Id);

        var reloaded = await _payroll.GetByIdAsync(run.Id);
        Assert.Equal(VoucherStatus.Posted, reloaded!.Status);
        Assert.NotNull(reloaded.JournalVoucherId);
    }

    [Fact]
    public async Task PostRunAsync_with_deductions_splits_gross_into_net_plus_deductions()
    {
        await CreateEmployee("Ahmed", 5000, 0); // gross 5000
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);
        await _payroll.UpdateRunLineAsync(run.Lines.Single().Id, 500);

        var voucher = await _payroll.PostRunAsync(run.Id, _deductionsClearing.Id, "tester", Now);

        Assert.Equal(5000m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Debit);
        Assert.Equal(4500m, voucher.Lines.Single(l => l.AccountId == _salariesPayable.Id).Credit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _deductionsClearing.Id).Credit);
    }

    [Fact]
    public async Task PostRunAsync_requires_a_deductions_account_when_any_deduction_exists()
    {
        await CreateEmployee("Ahmed", 5000, 0);
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);
        await _payroll.UpdateRunLineAsync(run.Lines.Single().Id, 500);

        await Assert.ThrowsAsync<PostingException>(() => _payroll.PostRunAsync(run.Id, null, "tester", Now));
    }

    [Fact]
    public async Task PostRunAsync_rejects_posting_the_same_run_twice()
    {
        await CreateEmployee();
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);
        await _payroll.PostRunAsync(run.Id, null, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _payroll.PostRunAsync(run.Id, null, "tester", Now));
    }

    [Fact]
    public async Task MarkPaidAsync_debits_salaries_payable_and_credits_bank()
    {
        await CreateEmployee("Ahmed", 5000, 1500);
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);
        await _payroll.PostRunAsync(run.Id, null, "tester", Now);

        var voucher = await _payroll.MarkPaidAsync(run.Id, _db.Bank.Id, new(2026, 5, 31), "tester", Now);

        Assert.Equal(6500m, voucher.Lines.Single(l => l.AccountId == _salariesPayable.Id).Debit);
        Assert.Equal(6500m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit);

        var reloaded = await _payroll.GetByIdAsync(run.Id);
        Assert.True(reloaded!.IsPaid);
        Assert.Equal(_db.Bank.Id, reloaded.PaidFromBankAccountId);
    }

    [Fact]
    public async Task MarkPaidAsync_rejects_a_draft_run_or_a_run_already_marked_paid()
    {
        await CreateEmployee();
        var draft = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);
        await Assert.ThrowsAsync<PostingException>(() => _payroll.MarkPaidAsync(draft.Id, _db.Bank.Id, new(2026, 5, 31), "tester", Now));

        await _payroll.PostRunAsync(draft.Id, null, "tester", Now);
        await _payroll.MarkPaidAsync(draft.Id, _db.Bank.Id, new(2026, 5, 31), "tester", Now);
        await Assert.ThrowsAsync<PostingException>(() => _payroll.MarkPaidAsync(draft.Id, _db.Bank.Id, new(2026, 5, 31), "tester", Now));
    }

    [Fact]
    public async Task Payroll_runs_of_another_company_are_not_visible_or_editable()
    {
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var otherEmployees = new EmployeeService(_db);
        var otherPayroll = new PayrollService(_db);
        await otherEmployees.CreateAsync(
            new EmployeeInput("Their Employee", null, null, new(2026, 1, 1), 4000, 0, 0, 0, null, null, null, null, other.Expense.Id, null),
            "tester", Now);
        var theirRun = await otherPayroll.CreateDraftRunAsync(other.May.Id, new(2026, 5, 31), "tester", Now);
        _db.SwitchTo(_db.Company.Id);

        Assert.Empty(await _payroll.GetAllAsync());
        var theirLineId = theirRun.Lines.Single().Id;
        await Assert.ThrowsAsync<PostingException>(() => _payroll.UpdateRunLineAsync(theirLineId, 100));
    }
}
