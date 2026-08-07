using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record EmployeeInput(
    string FullName, string? Designation, int? CostCenterId, DateOnly JoiningDate,
    decimal BasicSalary, decimal HousingAllowance, decimal TransportAllowance, decimal OtherAllowance,
    string? Mobile, string? Email, string? BankName, string? Iban, int EmployeeExpenseAccountId, string? Notes,
    DateOnly? VisaExpiryDate = null, string? EmiratesIdNumber = null, DateOnly? EmiratesIdExpiryDate = null,
    string? LabourCardNumber = null, DateOnly? LabourCardExpiryDate = null,
    string? PassportNumber = null, DateOnly? PassportExpiryDate = null,
    bool GratuityEligible = true, string? WpsAgentId = null);

/// <summary>One employee's document-compliance flag for the expiry warning banner — see
/// <see cref="EmployeeService.GetExpiringDocumentsAsync"/>.</summary>
public record ExpiringDocument(int EmployeeId, string EmployeeCode, string EmployeeName, string DocumentType, DateOnly ExpiryDate);

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
        employee.VisaExpiryDate = input.VisaExpiryDate;
        employee.EmiratesIdNumber = string.IsNullOrWhiteSpace(input.EmiratesIdNumber) ? null : input.EmiratesIdNumber.Trim();
        employee.EmiratesIdExpiryDate = input.EmiratesIdExpiryDate;
        employee.LabourCardNumber = string.IsNullOrWhiteSpace(input.LabourCardNumber) ? null : input.LabourCardNumber.Trim();
        employee.LabourCardExpiryDate = input.LabourCardExpiryDate;
        employee.PassportNumber = string.IsNullOrWhiteSpace(input.PassportNumber) ? null : input.PassportNumber.Trim();
        employee.PassportExpiryDate = input.PassportExpiryDate;
        employee.GratuityEligible = input.GratuityEligible;
        employee.WpsAgentId = string.IsNullOrWhiteSpace(input.WpsAgentId) ? null : input.WpsAgentId.Trim();
    }

    /// <summary>
    /// Every Active employee's compliance documents expiring within <paramref name="withinDays"/>
    /// (visa, Emirates ID, labour card, passport), soonest first — for a warning banner. An
    /// employee with multiple documents expiring soon appears once per document.
    /// </summary>
    public async Task<List<ExpiringDocument>> GetExpiringDocumentsAsync(int withinDays = 90, DateTime? asOfUtc = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var cutoff = DateOnly.FromDateTime(asOfUtc ?? DateTime.UtcNow).AddDays(withinDays);

        var employees = await db.Employees
            .Where(m => m.Status == EmployeeStatus.Active)
            .ToListAsync();

        var results = new List<ExpiringDocument>();
        foreach (var m in employees)
        {
            void AddIfExpiring(string docType, DateOnly? expiry)
            {
                if (expiry is DateOnly d && d <= cutoff)
                    results.Add(new ExpiringDocument(m.Id, m.EmployeeCode, m.FullName, docType, d));
            }
            AddIfExpiring("Visa", m.VisaExpiryDate);
            AddIfExpiring("Emirates ID", m.EmiratesIdExpiryDate);
            AddIfExpiring("Labour Card", m.LabourCardExpiryDate);
            AddIfExpiring("Passport", m.PassportExpiryDate);
        }

        return results.OrderBy(r => r.ExpiryDate).ToList();
    }
}
