using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

/// <summary>
/// Formula-only tests against UAE Federal Decree-Law No. 33 of 2021, Article 51's boundary cases —
/// written before any posting code exists, same discipline as FixedAssetService's depreciation
/// tests. Uses AED 10,000/month basic salary throughout so results are easy to hand-verify:
/// daily rate = 10,000 / 30 = AED 333.33, so 1 year at the 21-day rate = 21 * 333.33 = AED 7,000
/// (exactly 70% of one month's basic salary) — a useful sanity anchor for every other case.
/// </summary>
public class GratuityServiceTests
{
    private static readonly DateOnly Joined = new(2016, 1, 1);

    [Fact]
    public void Under_one_year_of_service_is_not_eligible()
    {
        var terminated = Joined.AddDays(328); // ~0.9 years
        Assert.Equal(0m, GratuityService.CalculateGratuity(10000m, Joined, terminated));
    }

    [Fact]
    public void Exactly_one_year_pays_21_days_at_the_basic_daily_rate()
    {
        var terminated = Joined.AddDays(365);
        Assert.Equal(7000m, GratuityService.CalculateGratuity(10000m, Joined, terminated));
    }

    [Fact]
    public void Exactly_five_years_still_uses_the_21_day_rate_throughout()
    {
        var terminated = Joined.AddDays(365 * 5);
        // 21 days * 5 years at the 333.33 daily rate = 35,000 — the 30-day rate must not kick in yet.
        Assert.Equal(35000m, GratuityService.CalculateGratuity(10000m, Joined, terminated));
    }

    [Fact]
    public void Just_past_five_years_switches_to_the_30_day_rate_for_the_remainder()
    {
        var terminated = Joined.AddDays((int)(365 * 5.5));
        // 35,000 (first 5 years) + 0.5 year * 30 days * 333.33 = 35,000 + 5,000 = 40,000.
        var result = GratuityService.CalculateGratuity(10000m, Joined, terminated);
        Assert.InRange(result, 39900m, 40100m); // day-count rounding tolerance
    }

    [Fact]
    public void Ten_years_combines_both_rate_tiers_correctly()
    {
        var terminated = Joined.AddDays(365 * 10);
        // 35,000 (first 5 years @ 21 days) + 5 years * 30 days * 333.33 = 35,000 + 50,000 = 85,000.
        Assert.Equal(85000m, GratuityService.CalculateGratuity(10000m, Joined, terminated));
    }

    [Fact]
    public void Long_tenure_is_capped_at_two_years_total_basic_wage()
    {
        var terminated = Joined.AddDays(365 * 30); // uncapped would be 285,000 — see class doc math
        Assert.Equal(240000m, GratuityService.CalculateGratuity(10000m, Joined, terminated)); // 24 * 10,000
    }

    [Fact]
    public void Zero_or_negative_basic_salary_yields_zero_gratuity()
    {
        var terminated = Joined.AddDays(365 * 3);
        Assert.Equal(0m, GratuityService.CalculateGratuity(0m, Joined, terminated));
        Assert.Equal(0m, GratuityService.CalculateGratuity(-500m, Joined, terminated));
    }

    [Fact]
    public void YearsOfService_is_zero_when_termination_is_not_after_joining()
    {
        Assert.Equal(0m, GratuityService.YearsOfService(Joined, Joined));
        Assert.Equal(0m, GratuityService.YearsOfService(Joined, Joined.AddDays(-10)));
    }
}

public class GratuityServicePreviewTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EmployeeService _employees;
    private readonly GratuityService _gratuity;
    private static readonly DateTime Now = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    public GratuityServicePreviewTests()
    {
        _employees = new EmployeeService(_db);
        _gratuity = new GratuityService(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task PreviewAsync_uses_the_employees_current_salary_and_joining_date()
    {
        var employee = await _employees.CreateAsync(
            new EmployeeInput("Ahmed", null, null, new(2020, 1, 1), 10000, 0, 0, 0, null, null, null, null, _db.Expense.Id, null),
            "tester", Now);

        var preview = await _gratuity.PreviewAsync(employee.Id, new DateOnly(2026, 1, 1)); // 6 years

        Assert.True(preview.Eligible);
        Assert.Equal(10000m, preview.BasicSalaryUsed);
        Assert.True(preview.CalculatedAmount > 35000m); // past the 5-year mark, 30-day rate applies
    }

    [Fact]
    public async Task PreviewAsync_reports_ineligible_when_the_employee_opts_out_or_service_is_short()
    {
        var optedOut = await _employees.CreateAsync(
            new EmployeeInput("Fatima", null, null, new(2018, 1, 1), 8000, 0, 0, 0, null, null, null, null, _db.Expense.Id, null)
                with { GratuityEligible = false },
            "tester", Now);
        var tooNew = await _employees.CreateAsync(
            new EmployeeInput("New Hire", null, null, new(2026, 1, 1), 8000, 0, 0, 0, null, null, null, null, _db.Expense.Id, null),
            "tester", Now);

        var optedOutPreview = await _gratuity.PreviewAsync(optedOut.Id, new DateOnly(2026, 5, 20));
        var tooNewPreview = await _gratuity.PreviewAsync(tooNew.Id, new DateOnly(2026, 5, 20));

        Assert.False(optedOutPreview.Eligible);
        Assert.Equal(0m, optedOutPreview.CalculatedAmount);
        Assert.False(tooNewPreview.Eligible);
    }
}

public class GratuityServicePostingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EmployeeService _employees;
    private readonly GratuityService _gratuity;
    private readonly Account _gratuityPayable;
    private static readonly DateTime Now = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    public GratuityServicePostingTests()
    {
        _employees = new EmployeeService(_db);
        _gratuity = new GratuityService(_db);

        using var db = _db.CreateUnscopedDbContext();
        _gratuityPayable = new Account { CompanyId = _db.Company.Id, Code = WellKnownAccounts.GratuityPayable, Name = "Gratuity Payable", Type = AccountType.Liability };
        db.Accounts.Add(_gratuityPayable);
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private Task<Employee> Hire(DateOnly joiningDate, bool gratuityEligible = true) =>
        _employees.CreateAsync(
            new EmployeeInput("Ahmed", null, null, joiningDate, 10000, 0, 0, 0, null, null, null, null, _db.Expense.Id, null)
                with { GratuityEligible = gratuityEligible },
            "tester", Now);

    [Fact]
    public async Task PostGratuityAsync_posts_a_balanced_voucher_and_terminates_the_employee()
    {
        var employee = await Hire(new DateOnly(2020, 1, 1)); // 6+ years by termination date

        var payment = await _gratuity.PostGratuityAsync(employee.Id, new(2026, 5, 20), _db.Expense.Id, "tester", Now);

        Assert.Equal(VoucherStatus.Posted, payment.Status);
        Assert.True(payment.CalculatedAmount > 0);
        Assert.NotNull(payment.JournalVoucherId);

        var reloaded = await _employees.GetByIdAsync(employee.Id);
        Assert.Equal(EmployeeStatus.Terminated, reloaded!.Status);
        Assert.Equal(new DateOnly(2026, 5, 20), reloaded.TerminationDate);

        using var db = _db.CreateUnscopedDbContext();
        var voucher = db.JournalVouchers.Include(v => v.Lines).Single(v => v.Id == payment.JournalVoucherId);
        Assert.Equal(voucher.Lines.Sum(l => l.Debit), voucher.Lines.Sum(l => l.Credit));
        Assert.Equal(payment.CalculatedAmount, voucher.Lines.Sum(l => l.Debit));
    }

    [Fact]
    public async Task PostGratuityAsync_ignoring_eligibility_still_records_a_zero_amount_audit_row_with_no_voucher()
    {
        var optedOut = await Hire(new DateOnly(2018, 1, 1), gratuityEligible: false);

        var payment = await _gratuity.PostGratuityAsync(optedOut.Id, new(2026, 5, 20), _db.Expense.Id, "tester", Now);

        Assert.Equal(0m, payment.CalculatedAmount);
        Assert.Equal(VoucherStatus.Posted, payment.Status);
        Assert.Null(payment.JournalVoucherId);

        var reloaded = await _employees.GetByIdAsync(optedOut.Id);
        Assert.Equal(EmployeeStatus.Terminated, reloaded!.Status); // termination itself still happens
    }

    [Fact]
    public async Task PostGratuityAsync_cannot_be_posted_twice_for_the_same_employee()
    {
        var employee = await Hire(new DateOnly(2020, 1, 1));
        await _gratuity.PostGratuityAsync(employee.Id, new(2026, 5, 20), _db.Expense.Id, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() =>
            _gratuity.PostGratuityAsync(employee.Id, new(2026, 5, 21), _db.Expense.Id, "tester", Now));
    }

    [Fact]
    public async Task MarkPaidAsync_posts_Dr_gratuity_payable_Cr_bank_and_flags_paid()
    {
        var employee = await Hire(new DateOnly(2020, 1, 1));
        var payment = await _gratuity.PostGratuityAsync(employee.Id, new(2026, 5, 20), _db.Expense.Id, "tester", Now);

        var voucher = await _gratuity.MarkPaidAsync(payment.Id, _db.Bank.Id, new(2026, 5, 25), "tester", Now);

        Assert.Equal(payment.CalculatedAmount, voucher.Lines.Sum(l => l.Debit));
        Assert.Equal(payment.CalculatedAmount, voucher.Lines.Sum(l => l.Credit));

        using var db = _db.CreateUnscopedDbContext();
        var reloaded = db.GratuityPayments.Single(g => g.Id == payment.Id);
        Assert.True(reloaded.IsPaid);
        Assert.Equal(new DateOnly(2026, 5, 25), reloaded.PaidDate);
    }

    [Fact]
    public async Task MarkPaidAsync_rejects_a_zero_amount_payment_that_has_nothing_owed()
    {
        var optedOut = await Hire(new DateOnly(2018, 1, 1), gratuityEligible: false);
        var payment = await _gratuity.PostGratuityAsync(optedOut.Id, new(2026, 5, 20), _db.Expense.Id, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() =>
            _gratuity.MarkPaidAsync(payment.Id, _db.Bank.Id, new(2026, 5, 25), "tester", Now));
    }

    [Fact]
    public async Task MarkPaidAsync_cannot_be_called_twice()
    {
        var employee = await Hire(new DateOnly(2020, 1, 1));
        var payment = await _gratuity.PostGratuityAsync(employee.Id, new(2026, 5, 20), _db.Expense.Id, "tester", Now);
        await _gratuity.MarkPaidAsync(payment.Id, _db.Bank.Id, new(2026, 5, 25), "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() =>
            _gratuity.MarkPaidAsync(payment.Id, _db.Bank.Id, new(2026, 5, 26), "tester", Now));
    }
}
