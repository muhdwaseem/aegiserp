using AegisErp.Domain;
using AegisErp.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>A company the signed-in user may work in, and the role they hold there.</summary>
public record CompanyAccessRow(int CompanyId, string Code, string Name, string Role);

/// <summary>One user's grant on a company (for the access-management screen).</summary>
public record CompanyMemberRow(string UserId, string Email, string DisplayName, string Role);

/// <summary>Result of <see cref="CompanyAccessService.CreateUserAndGrantAsync"/> — tells the caller
/// whether a brand-new login was created or an existing account was simply granted access, so the
/// UI can word its confirmation accordingly.</summary>
public record CreateUserResult(bool UserWasCreated, string Email);

/// <summary>
/// Resolves and manages which companies a user can work in. A firm administrator implicitly
/// reaches every company; everyone else is limited to their explicit grants.
/// </summary>
public class CompanyAccessService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly UserManager<AppUser> _users;

    public CompanyAccessService(IDbContextFactory<AegisDbContext> dbf, UserManager<AppUser> users)
    {
        _dbf = dbf;
        _users = users;
    }

    /// <summary>Companies this user may open, with their role in each.</summary>
    public async Task<List<CompanyAccessRow>> GetCompaniesForUserAsync(string userId, bool isFirmAdmin)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        if (isFirmAdmin)
        {
            return await db.CompanySetups.AsNoTracking()
                .OrderBy(c => c.LegalName)
                .Select(c => new CompanyAccessRow(c.Id, c.CompanyCode, c.LegalName, AppRoles.Admin))
                .ToListAsync();
        }

        return await db.UserCompanyAccess.AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderBy(a => a.Company.LegalName)
            .Select(a => new CompanyAccessRow(a.CompanyId, a.Company.CompanyCode, a.Company.LegalName, a.Role))
            .ToListAsync();
    }

    /// <summary>The user's role in one company, or null if they have no access to it.</summary>
    public async Task<string?> GetRoleAsync(string userId, int companyId, bool isFirmAdmin)
    {
        if (isFirmAdmin) return AppRoles.Admin;

        await using var db = await _dbf.CreateDbContextAsync();
        return await db.UserCompanyAccess.AsNoTracking()
            .Where(a => a.UserId == userId && a.CompanyId == companyId)
            .Select(a => a.Role)
            .FirstOrDefaultAsync();
    }

    /// <summary>True when the user may open the given company.</summary>
    public async Task<bool> CanAccessAsync(string userId, int companyId, bool isFirmAdmin) =>
        await GetRoleAsync(userId, companyId, isFirmAdmin) is not null;

    /// <summary>Everyone granted access to a company.</summary>
    public async Task<List<CompanyMemberRow>> GetMembersAsync(int companyId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.UserCompanyAccess.AsNoTracking()
            .Where(a => a.CompanyId == companyId)
            .OrderBy(a => a.User.Email)
            .Select(a => new CompanyMemberRow(a.UserId, a.User.Email ?? "", a.User.DisplayName, a.Role))
            .ToListAsync();
    }

    /// <summary>Grants or updates a user's role on a company.</summary>
    public async Task GrantAsync(string userId, int companyId, string role)
    {
        if (!AppRoles.CompanyRoles.Contains(role))
            throw new PostingException($"'{role}' is not a valid company role.");

        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.UserCompanyAccess
            .FirstOrDefaultAsync(a => a.UserId == userId && a.CompanyId == companyId);

        if (existing is null)
        {
            db.UserCompanyAccess.Add(new UserCompanyAccess { UserId = userId, CompanyId = companyId, Role = role });
        }
        else
        {
            if (existing.Role == AppRoles.Admin && role != AppRoles.Admin)
                await EnsureNotLastAdminAsync(db, companyId, userId);
            existing.Role = role;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Removes a user's access to a company.</summary>
    public async Task RevokeAsync(string userId, int companyId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.UserCompanyAccess
            .FirstOrDefaultAsync(a => a.UserId == userId && a.CompanyId == companyId);
        if (existing is null) return;

        if (existing.Role == AppRoles.Admin)
            await EnsureNotLastAdminAsync(db, companyId, userId);

        db.UserCompanyAccess.Remove(existing);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Guards against a company being left with no one who can manage it. Called before demoting
    /// or revoking an Admin — throws if <paramref name="userId"/> is the company's only remaining
    /// Admin (a FirmAdmin can always still get in, but a company should never depend on one having
    /// to intervene just to fix its own access list).
    /// </summary>
    private static async Task EnsureNotLastAdminAsync(AegisDbContext db, int companyId, string userId)
    {
        var otherAdmins = await db.UserCompanyAccess
            .CountAsync(a => a.CompanyId == companyId && a.Role == AppRoles.Admin && a.UserId != userId);
        if (otherAdmins == 0)
            throw new PostingException("This is the only Admin on this company — grant Admin to someone else first.");
    }

    /// <summary>
    /// Onboards a company's own user: creates the login if the email doesn't already have one
    /// (otherwise reuses the existing account, so someone already working in another company can
    /// simply be added to this one too), then grants the given role on <paramref name="companyId"/>.
    /// </summary>
    public async Task<CreateUserResult> CreateUserAndGrantAsync(string email, string displayName, string password, int companyId, string role)
    {
        email = email.Trim();
        if (string.IsNullOrWhiteSpace(email)) throw new PostingException("Email is required.");
        if (string.IsNullOrWhiteSpace(displayName)) throw new PostingException("Name is required.");
        if (!AppRoles.CompanyRoles.Contains(role))
            throw new PostingException($"'{role}' is not a valid company role.");

        var user = await _users.FindByEmailAsync(email);
        var created = false;

        if (user is null)
        {
            if (string.IsNullOrWhiteSpace(password))
                throw new PostingException("Password is required for a new user.");

            user = new AppUser { UserName = email, Email = email, DisplayName = displayName.Trim(), EmailConfirmed = true };
            var result = await _users.CreateAsync(user, password);
            if (!result.Succeeded)
                throw new PostingException(string.Join(" ", result.Errors.Select(e => e.Description)));
            created = true;
        }

        await GrantAsync(user.Id, companyId, role);
        return new CreateUserResult(created, email);
    }
}
