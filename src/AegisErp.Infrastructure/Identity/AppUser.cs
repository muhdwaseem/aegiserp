using Microsoft.AspNetCore.Identity;

namespace AegisErp.Infrastructure.Identity;

/// <summary>Application user. DisplayName is what appears on vouchers as CreatedBy.</summary>
public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = string.Empty;
}

/// <summary>Role names used across the app. Admin ⊃ Accountant ⊃ Viewer in practice.</summary>
public static class AppRoles
{
    /// <summary>
    /// Firm-level administrator: spans every company, creates companies and assigns staff to them.
    /// This is the only role that is genuinely global — the others are held per company.
    /// </summary>
    public const string FirmAdmin = "FirmAdmin";

    public const string Admin = "Admin";
    public const string Accountant = "Accountant";
    public const string Viewer = "Viewer";

    /// <summary>Roles allowed to post documents to the ledger.</summary>
    public const string Posters = FirmAdmin + "," + Admin + "," + Accountant;

    /// <summary>Roles allowed to administer a company's settings and users.</summary>
    public const string Admins = FirmAdmin + "," + Admin;

    /// <summary>Every role that can be granted on a specific company.</summary>
    public static readonly string[] CompanyRoles = { Admin, Accountant, Viewer };
}
