namespace AegisErp.Domain.Entities;

/// <summary>A reusable VAT/tax code that can be applied to sales and purchase lines.</summary>
public class TaxCode : ICompanyScoped
{
    public int Id { get; set; }
    public int CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Rate { get; set; }
    public TaxCodeKind Kind { get; set; } = TaxCodeKind.Output;

    /// <summary>The GL account this tax posts to, if any (exempt/zero-rated codes may have none).</summary>
    public int? GlAccountId { get; set; }
    public Account? GlAccount { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public bool IsActive { get; set; } = true;
}
