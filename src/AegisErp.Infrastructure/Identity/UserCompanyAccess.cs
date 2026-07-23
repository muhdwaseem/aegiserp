using AegisErp.Domain.Entities;

namespace AegisErp.Infrastructure.Identity;

/// <summary>
/// Grants one user access to one company, with the role they hold *in that company*. A user can
/// be an Accountant for one client and a Viewer (or absent entirely) for another — which is how
/// staff are restricted to the engagements they are assigned to.
/// </summary>
public class UserCompanyAccess
{
    public int Id { get; set; }

    public string UserId { get; set; } = string.Empty;
    public AppUser User { get; set; } = null!;

    public int CompanyId { get; set; }
    public CompanySetup Company { get; set; } = null!;

    /// <summary>Role held in this company: Admin, Accountant or Viewer.</summary>
    public string Role { get; set; } = AppRoles.Viewer;
}
