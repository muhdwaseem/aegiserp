namespace AegisErp.Domain.Entities;

/// <summary>An AP vendor (supplier). Purchase invoices and payments reference the vendor to form its subledger.</summary>
public class Vendor : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Human-facing vendor number, e.g. "V-0001". Unique within the company.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>UAE Tax Registration Number (optional).</summary>
    public string? Trn { get; set; }

    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }

    /// <summary>Vendor group / category, e.g. Supplier, Contractor, Utility, Government.</summary>
    public string? Group { get; set; }

    /// <summary>Account currency. AED-only for now (stored, no FX logic).</summary>
    public string Currency { get; set; } = "AED";

    /// <summary>Days until a purchase invoice falls due (drives the invoice DueDate and aging).</summary>
    public int PaymentTermsDays { get; set; } = 30;

    public bool IsActive { get; set; } = true;
}
