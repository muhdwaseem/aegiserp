using AegisErp.Domain;
using AegisErp.Infrastructure.Identity;
using AegisErp.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AegisErp.Tests;

public class CompanyAccessServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly CompanyAccessService _access;

    public CompanyAccessServiceTests() => _access = new CompanyAccessService(_db, BuildUserManager(_db));

    public void Dispose() => _db.Dispose();

    /// <summary>
    /// A real UserManager bound to the same in-memory database as the rest of the test, built by
    /// hand (no ASP.NET host) — mirrors the defaults Program.cs configures (8-char minimum,
    /// otherwise Identity's standard complexity rules) so password-validation behaviour matches
    /// production.
    /// </summary>
    private static UserManager<AppUser> BuildUserManager(TestDb db)
    {
        var store = new UserStore<AppUser>(db.CreateUnscopedDbContext());
        var options = Options.Create(new IdentityOptions { Password = { RequiredLength = 8 } });
        return new UserManager<AppUser>(
            store, options, new PasswordHasher<AppUser>(),
            new[] { new UserValidator<AppUser>() }, new[] { new PasswordValidator<AppUser>() },
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<AppUser>>.Instance);
    }

    [Fact]
    public async Task CreateUserAndGrantAsync_creates_a_new_login_and_grants_the_chosen_role()
    {
        var result = await _access.CreateUserAndGrantAsync("new.hire@client.com", "New Hire", "Passw0rd!", _db.Company.Id, AppRoles.Accountant);

        Assert.True(result.UserWasCreated);
        var members = await _access.GetMembersAsync(_db.Company.Id);
        var member = Assert.Single(members);
        Assert.Equal("new.hire@client.com", member.Email);
        Assert.Equal(AppRoles.Accountant, member.Role);
    }

    [Fact]
    public async Task CreateUserAndGrantAsync_reuses_an_existing_login_instead_of_duplicating_it()
    {
        var first = await _access.CreateUserAndGrantAsync("shared@client.com", "Shared User", "Passw0rd!", _db.Company.Id, AppRoles.Viewer);
        Assert.True(first.UserWasCreated);

        var second = await _access.CreateUserAndGrantAsync("shared@client.com", "Shared User", "", _db.OtherCompany.Id, AppRoles.Admin);
        Assert.False(second.UserWasCreated);

        var companyMembers = await _access.GetMembersAsync(_db.Company.Id);
        var otherMembers = await _access.GetMembersAsync(_db.OtherCompany.Id);
        Assert.Equal(AppRoles.Viewer, Assert.Single(companyMembers).Role);
        Assert.Equal(AppRoles.Admin, Assert.Single(otherMembers).Role);
        Assert.Equal(Assert.Single(companyMembers).UserId, Assert.Single(otherMembers).UserId);
    }

    [Fact]
    public async Task CreateUserAndGrantAsync_rejects_a_role_outside_the_grantable_set()
    {
        await Assert.ThrowsAsync<PostingException>(() =>
            _access.CreateUserAndGrantAsync("x@client.com", "X", "Passw0rd!", _db.Company.Id, AppRoles.FirmAdmin));
    }

    [Fact]
    public async Task RevokeAsync_refuses_to_remove_the_last_admin()
    {
        var solo = await _access.CreateUserAndGrantAsync("solo.admin@client.com", "Solo Admin", "Passw0rd!", _db.Company.Id, AppRoles.Admin);

        var userId = Assert.Single(await _access.GetMembersAsync(_db.Company.Id)).UserId;
        await Assert.ThrowsAsync<PostingException>(() => _access.RevokeAsync(userId, _db.Company.Id));

        // Untouched — still there as Admin.
        Assert.Equal(AppRoles.Admin, Assert.Single(await _access.GetMembersAsync(_db.Company.Id)).Role);
    }

    [Fact]
    public async Task RevokeAsync_allows_removing_an_admin_when_another_admin_remains()
    {
        await _access.CreateUserAndGrantAsync("admin.one@client.com", "Admin One", "Passw0rd!", _db.Company.Id, AppRoles.Admin);
        await _access.CreateUserAndGrantAsync("admin.two@client.com", "Admin Two", "Passw0rd!", _db.Company.Id, AppRoles.Admin);

        var members = await _access.GetMembersAsync(_db.Company.Id);
        var toRemove = members.First(m => m.Email == "admin.one@client.com");

        await _access.RevokeAsync(toRemove.UserId, _db.Company.Id);

        var remaining = Assert.Single(await _access.GetMembersAsync(_db.Company.Id));
        Assert.Equal("admin.two@client.com", remaining.Email);
    }

    [Fact]
    public async Task GrantAsync_refuses_to_demote_the_last_admin()
    {
        await _access.CreateUserAndGrantAsync("solo.admin@client.com", "Solo Admin", "Passw0rd!", _db.Company.Id, AppRoles.Admin);
        var userId = Assert.Single(await _access.GetMembersAsync(_db.Company.Id)).UserId;

        await Assert.ThrowsAsync<PostingException>(() => _access.GrantAsync(userId, _db.Company.Id, AppRoles.Viewer));

        Assert.Equal(AppRoles.Admin, Assert.Single(await _access.GetMembersAsync(_db.Company.Id)).Role);
    }

    [Fact]
    public async Task SetPayrollAccessAsync_grants_and_revokes_the_extra_flag()
    {
        await _access.CreateUserAndGrantAsync("book.keeper@client.com", "Book Keeper", "Passw0rd!", _db.Company.Id, AppRoles.Accountant);
        var userId = Assert.Single(await _access.GetMembersAsync(_db.Company.Id)).UserId;

        await _access.SetPayrollAccessAsync(userId, _db.Company.Id, true);
        Assert.True(Assert.Single(await _access.GetMembersAsync(_db.Company.Id)).CanAccessPayroll);

        await _access.SetPayrollAccessAsync(userId, _db.Company.Id, false);
        Assert.False(Assert.Single(await _access.GetMembersAsync(_db.Company.Id)).CanAccessPayroll);
    }

    [Fact]
    public async Task CreateUserAndGrantAsync_can_grant_payroll_access_at_creation_time()
    {
        var result = await _access.CreateUserAndGrantAsync(
            "book.keeper2@client.com", "Book Keeper 2", "Passw0rd!", _db.Company.Id, AppRoles.Accountant, canAccessPayroll: true);

        Assert.True(result.UserWasCreated);
        Assert.True(Assert.Single(await _access.GetMembersAsync(_db.Company.Id)).CanAccessPayroll);
    }

    [Fact]
    public async Task GetMembersAsync_does_not_leak_grants_across_companies()
    {
        await _access.CreateUserAndGrantAsync("a@client.com", "A", "Passw0rd!", _db.Company.Id, AppRoles.Accountant);
        await _access.CreateUserAndGrantAsync("b@client.com", "B", "Passw0rd!", _db.OtherCompany.Id, AppRoles.Viewer);

        Assert.Equal("a@client.com", Assert.Single(await _access.GetMembersAsync(_db.Company.Id)).Email);
        Assert.Equal("b@client.com", Assert.Single(await _access.GetMembersAsync(_db.OtherCompany.Id)).Email);
    }
}
