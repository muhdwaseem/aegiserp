using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Read-only preview of what an employee's gratuity would be if terminated on a given
/// date — nothing is persisted until <c>GratuityService.PostGratuityAsync</c> is called.</summary>
public record GratuityPreview(
    int EmployeeId, string EmployeeName, bool Eligible, decimal YearsOfService,
    decimal BasicSalaryUsed, decimal CalculatedAmount, bool CapApplied);

public class GratuityService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public GratuityService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    /// <summary>
    /// Years of continuous service as a decimal (e.g. 5.5), from the calendar day difference over
    /// 365 — an approximation, not exact Gregorian year/month/day reckoning a court would use; see
    /// the plan's documented caveat that this needs verification against a UAE labour consultant.
    /// </summary>
    public static decimal YearsOfService(DateOnly joiningDate, DateOnly terminationDate)
    {
        var totalDays = terminationDate.DayNumber - joiningDate.DayNumber;
        return totalDays <= 0 ? 0m : totalDays / 365m;
    }

    /// <summary>
    /// UAE Federal Decree-Law No. 33 of 2021, Article 51 (mainland UAE only — DIFC/ADGM use a
    /// different DEWS scheme, out of scope): minimum 1 year continuous service to qualify; 21
    /// days' basic salary per year for the first 5 years, 30 days' basic salary per year beyond
    /// that; capped at 2 years' total basic wage. Based on basic salary only — allowances are
    /// deliberately not part of <paramref name="basicMonthlySalary"/>.
    /// </summary>
    public static decimal CalculateGratuity(decimal basicMonthlySalary, DateOnly joiningDate, DateOnly terminationDate)
    {
        if (basicMonthlySalary <= 0) return 0m;
        var years = YearsOfService(joiningDate, terminationDate);
        if (years < 1m) return 0m;

        var dailyRate = basicMonthlySalary / 30m;
        var gratuity = years <= 5m
            ? dailyRate * 21m * years
            : dailyRate * 21m * 5m + dailyRate * 30m * (years - 5m);

        var cap = basicMonthlySalary * 24m;
        return Math.Round(Math.Min(gratuity, cap), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Preview using the employee's *current* BasicSalary and JoiningDate — for the
    /// termination dialog, before anything is posted.</summary>
    public async Task<GratuityPreview> PreviewAsync(int employeeId, DateOnly terminationDate)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == employeeId)
            ?? throw new PostingException("Employee not found.");

        var years = YearsOfService(employee.JoiningDate, terminationDate);
        var amount = CalculateGratuity(employee.BasicSalary, employee.JoiningDate, terminationDate);

        var dailyRate = employee.BasicSalary / 30m;
        var uncapped = years < 1m ? 0m : years <= 5m
            ? dailyRate * 21m * years
            : dailyRate * 21m * 5m + dailyRate * 30m * (years - 5m);
        var capApplied = Math.Round(uncapped, 2, MidpointRounding.AwayFromZero) > amount;

        return new GratuityPreview(
            employee.Id, employee.FullName, employee.GratuityEligible && years >= 1m,
            years, employee.BasicSalary, employee.GratuityEligible ? amount : 0m, capApplied);
    }
}
