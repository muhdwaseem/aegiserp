using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record EmployeeInput(
    string FullName, string? Designation, int? CostCenterId, DateOnly JoiningDate,
    decimal BasicSalary, decimal HousingAllowance, decimal TransportAllowance, decimal OtherAllowance,
    string? Mobile, string? Email, string? BankName, string? Iban, int EmployeeExpenseAccountId, string? Notes);

public class EmployeeService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public EmployeeService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<Employee>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Employees.AsNoTracking()
            .Include(m => m.CostCenter).Include(m => m.EmployeeExpenseAccount)
            .OrderBy(m => m.EmployeeCode)
            .ToListAsync();
    }

    public async Task<Employee?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Employees.AsNoTracking()
            .Include(m => m.CostCenter).Include(m => m.EmployeeExpenseAccount)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<Employee> CreateAsync(EmployeeInput input, string createdBy, DateTime nowUtc)
    {
        ValidateInput(input);

        await using var db = await _dbf.CreateDbContextAsync();

        var codes = await db.Employees.Select(m => m.EmployeeCode).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("EMP-") && int.TryParse(code.AsSpan(4), out var n) && n > max)
                max = n;

        var employee = new Employee { EmployeeCode = $"EMP-{max + 1:0000}", CreatedBy = createdBy, CreatedAtUtc = nowUtc };
        ApplyInput(employee, input);

        db.Employees.Add(employee);
        await db.SaveChangesAsync();
        return employee;
    }

    public async Task UpdateAsync(int id, EmployeeInput input)
    {
        ValidateInput(input);

        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == id)
            ?? throw new PostingException("Employee not found.");
        ApplyInput(employee, input);
        await db.SaveChangesAsync();
    }

    /// <summary>Marks an employee Terminated (or reactivates them) — does not touch any past
    /// payroll run, since <see cref="PayrollRunLine"/> snapshots salary at run creation time.</summary>
    public async Task SetStatusAsync(int id, EmployeeStatus status, DateOnly? terminationDate)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var employee = await db.Employees.FirstOrDefaultAsync(m => m.Id == id)
            ?? throw new PostingException("Employee not found.");
        employee.Status = status;
        employee.TerminationDate = status == EmployeeStatus.Terminated ? terminationDate : null;
        await db.SaveChangesAsync();
    }

    private static void ValidateInput(EmployeeInput input)
    {
        if (string.IsNullOrWhiteSpace(input.FullName)) throw new PostingException("Employee name is required.");
        if (input.BasicSalary < 0) throw new PostingException("Basic salary cannot be negative.");
        if (input.HousingAllowance < 0 || input.TransportAllowance < 0 || input.OtherAllowance < 0)
            throw new PostingException("Allowances cannot be negative.");
        if (input.EmployeeExpenseAccountId == 0) throw new PostingException("Select the salary expense account.");
    }

    private static void ApplyInput(Employee employee, EmployeeInput input)
    {
        employee.FullName = input.FullName.Trim();
        employee.Designation = string.IsNullOrWhiteSpace(input.Designation) ? null : input.Designation.Trim();
        employee.CostCenterId = input.CostCenterId;
        employee.JoiningDate = input.JoiningDate;
        employee.BasicSalary = input.BasicSalary;
        employee.HousingAllowance = input.HousingAllowance;
        employee.TransportAllowance = input.TransportAllowance;
        employee.OtherAllowance = input.OtherAllowance;
        employee.Mobile = string.IsNullOrWhiteSpace(input.Mobile) ? null : input.Mobile.Trim();
        employee.Email = string.IsNullOrWhiteSpace(input.Email) ? null : input.Email.Trim();
        employee.BankName = string.IsNullOrWhiteSpace(input.BankName) ? null : input.BankName.Trim();
        employee.Iban = string.IsNullOrWhiteSpace(input.Iban) ? null : input.Iban.Trim();
        employee.EmployeeExpenseAccountId = input.EmployeeExpenseAccountId;
        employee.Notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
    }
}
