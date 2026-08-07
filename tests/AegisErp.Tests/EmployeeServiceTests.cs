using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class EmployeeServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EmployeeService _employees;
    private static readonly DateTime Now = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    public EmployeeServiceTests() => _employees = new EmployeeService(_db);

    public void Dispose() => _db.Dispose();

    private EmployeeInput Input(decimal basic = 5000, decimal housing = 1500, decimal transport = 500, decimal other = 0) =>
        new("Ahmed Al Mansoori", "Visa Officer", null, new(2026, 1, 1), basic, housing, transport, other,
            "+971500000000", "ahmed@example.com", "ADCB", "AE0000000000000000000", _db.Expense.Id, null);

    [Fact]
    public async Task CreateAsync_assigns_sequential_employee_codes_and_computes_gross_salary()
    {
        var first = await _employees.CreateAsync(Input(), "tester", Now);
        var second = await _employees.CreateAsync(Input(), "tester", Now);

        Assert.Equal("EMP-0001", first.EmployeeCode);
        Assert.Equal("EMP-0002", second.EmployeeCode);
        Assert.Equal(7000m, first.GrossSalary); // 5000 + 1500 + 500
    }

    [Fact]
    public async Task CreateAsync_rejects_negative_salary_or_allowances()
    {
        await Assert.ThrowsAsync<PostingException>(() => _employees.CreateAsync(Input(basic: -1), "tester", Now));
        await Assert.ThrowsAsync<PostingException>(() => _employees.CreateAsync(Input(housing: -1), "tester", Now));
    }

    [Fact]
    public async Task UpdateAsync_changes_fields_without_changing_the_employee_code()
    {
        var employee = await _employees.CreateAsync(Input(), "tester", Now);

        await _employees.UpdateAsync(employee.Id, Input(basic: 6000) with { FullName = "Ahmed Al Mansoori Jr." });

        var reloaded = await _employees.GetByIdAsync(employee.Id);
        Assert.Equal("EMP-0001", reloaded!.EmployeeCode);
        Assert.Equal("Ahmed Al Mansoori Jr.", reloaded.FullName);
        Assert.Equal(6000m, reloaded.BasicSalary);
    }

    [Fact]
    public async Task SetStatusAsync_terminates_and_reactivates_without_touching_salary()
    {
        var employee = await _employees.CreateAsync(Input(), "tester", Now);

        await _employees.SetStatusAsync(employee.Id, EmployeeStatus.Terminated, new DateOnly(2026, 6, 1));
        var terminated = await _employees.GetByIdAsync(employee.Id);
        Assert.Equal(EmployeeStatus.Terminated, terminated!.Status);
        Assert.Equal(new DateOnly(2026, 6, 1), terminated.TerminationDate);

        await _employees.SetStatusAsync(employee.Id, EmployeeStatus.Active, null);
        var reactivated = await _employees.GetByIdAsync(employee.Id);
        Assert.Equal(EmployeeStatus.Active, reactivated!.Status);
        Assert.Null(reactivated.TerminationDate);
        Assert.Equal(5000m, reactivated.BasicSalary); // untouched
    }

    [Fact]
    public async Task GetExpiringDocumentsAsync_returns_only_documents_within_the_window_soonest_first()
    {
        var soon = await _employees.CreateAsync(Input(), "tester", Now); // visa expires in 10 days below
        await _employees.UpdateAsync(soon.Id, Input() with { VisaExpiryDate = new DateOnly(2026, 5, 30) });

        var later = await _employees.CreateAsync(Input(), "tester", Now);
        await _employees.UpdateAsync(later.Id, Input() with { EmiratesIdExpiryDate = new DateOnly(2026, 6, 15) });

        var farAway = await _employees.CreateAsync(Input(), "tester", Now);
        await _employees.UpdateAsync(farAway.Id, Input() with { PassportExpiryDate = new DateOnly(2027, 1, 1) });

        var results = await _employees.GetExpiringDocumentsAsync(withinDays: 90, asOfUtc: Now);

        Assert.Equal(2, results.Count);
        Assert.Equal(soon.Id, results[0].EmployeeId);
        Assert.Equal("Visa", results[0].DocumentType);
        Assert.Equal(later.Id, results[1].EmployeeId);
    }

    [Fact]
    public async Task GetExpiringDocumentsAsync_excludes_terminated_employees()
    {
        var employee = await _employees.CreateAsync(Input(), "tester", Now);
        await _employees.UpdateAsync(employee.Id, Input() with { VisaExpiryDate = new DateOnly(2026, 5, 25) });
        await _employees.SetStatusAsync(employee.Id, EmployeeStatus.Terminated, new DateOnly(2026, 5, 21));

        var results = await _employees.GetExpiringDocumentsAsync(withinDays: 90, asOfUtc: Now);

        Assert.Empty(results);
    }

    [Fact]
    public async Task Employees_of_another_company_are_not_visible()
    {
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var otherEmployees = new EmployeeService(_db);
        await otherEmployees.CreateAsync(
            new EmployeeInput("Their Employee", null, null, new(2026, 1, 1), 4000, 0, 0, 0, null, null, null, null, other.Expense.Id, null),
            "tester", Now);
        _db.SwitchTo(_db.Company.Id);

        Assert.Empty(await _employees.GetAllAsync());
    }
}
