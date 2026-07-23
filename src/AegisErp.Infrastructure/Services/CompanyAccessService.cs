using AegisErp.Domain;
using AegisErp.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>A company the signed-in user may work in, and the role they hold there.</summary>
public record CompanyAccessRow(int CompanyId, string Code, string Name, string Role);

/// <summary>One user's grant on a company (for the access-management screen).</summary>
public record CompanyMemberRow(string UserId, string Email, string DisplayName, string Role);

/// <summary>
/// Resolves and manages which companies a user can work in. A firm administrator implicitly
/// reaches every company; everyone else is limited to their explicit grants.
/// </summary>
public class CompanyAccessService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public CompanyAccessService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

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
            db.UserCompanyAccess.Add(new UserCompanyAccess { UserId = userId, CompanyId = companyId, Role = role });
        else
            existing.Role = role;

        await db.SaveChangesAsync();
    }

    /// <summary>Removes a user's access to a company.</summary>
    public async Task RevokeAsync(string userId, int companyId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.UserCompanyAccess
            .FirstOrDefaultAsync(a => a.UserId == userId && a.CompanyId == companyId);
        if (existing is null) return;

        db.UserCompanyAccess.Remove(existing);
        await db.SaveChangesAsync();
    }
}
