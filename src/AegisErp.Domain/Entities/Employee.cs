namespace AegisErp.Domain.Entities;

/// <summary>
/// An HR & Payroll staff record. Registering an employee has no GL impact by itself — only a
/// <see cref="PayrollRun"/> posts anything, and only once it's actually posted.
/// </summary>
public class Employee : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    /// <summary>Auto-numbered, e.g. "EMP-0001" — same "max numeric suffix + 1" convention as Customer/Vendor/FixedAsset codes.</summary>
    public string EmployeeCode { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;
    public string? Designation { get; set; }

    /// <summary>Reused as "Department" — a Cost Center already represents a company segment in
    /// this app's existing P&amp;L reporting, so this avoids a second, parallel department concept.</summary>
    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public DateOnly JoiningDate { get; set; }
    public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;
    public DateOnly? TerminationDate { get; set; }

    public decimal BasicSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal OtherAllowance { get; set; }

    public string? Mobile { get; set; }
    public string? Email { get; set; }
    public string? BankName { get; set; }
    public string? Iban { get; set; }

    /// <summary>The P&amp;L account this employee's salary posts to when a payroll run is posted.</summary>
    public int EmployeeExpenseAccountId { get; set; }
    public Account EmployeeExpenseAccount { get; set; } = null!;

    public string? Notes { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }

    public decimal GrossSalary => BasicSalary + HousingAllowance + TransportAllowance + OtherAllowance;
}

public enum EmployeeStatus
{
    Active = 1,
    Terminated = 2,
}
