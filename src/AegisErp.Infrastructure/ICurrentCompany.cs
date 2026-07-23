namespace AegisErp.Infrastructure;

/// <summary>
/// The company (tenant) the current user is working in. The DbContext reads this to filter
/// every query, so it is the single point that enforces data isolation between companies.
/// </summary>
public interface ICurrentCompany
{
    /// <summary>Active company id, or null for an unscoped context (seeding / firm-wide admin queries).</summary>
    int? CompanyId { get; }
}

/// <summary>
/// Scoped, mutable holder for the active company. The web layer sets it from the signed-in
/// user's claims when a circuit starts, and updates it when the user switches company.
/// </summary>
public class CurrentCompany : ICurrentCompany
{
    public int? CompanyId { get; set; }

    /// <summary>True when the signed-in user may act across all companies (firm administrator).</summary>
    public bool IsFirmAdmin { get; set; }
}
