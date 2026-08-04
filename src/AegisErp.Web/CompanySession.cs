using System.Security.Claims;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Identity;
using AegisErp.Infrastructure.Services;
using Microsoft.AspNetCore.Components.Authorization;

namespace AegisErp.Web;

/// <summary>
/// Per-circuit state for the active company. Resolves which companies the signed-in user may open,
/// picks one, and keeps <see cref="CurrentCompany"/> in step — which is what the DbContext query
/// filters read, so switching here re-scopes every subsequent query.
/// </summary>
public class CompanySession
{
    private readonly CurrentCompany _current;
    private readonly CompanyAccessService _access;
    private readonly AuthenticationStateProvider _authProvider;
    private bool _initialised;

    public CompanySession(CurrentCompany current, CompanyAccessService access, AuthenticationStateProvider authProvider)
    {
        _current = current;
        _access = access;
        _authProvider = authProvider;
    }

    /// <summary>Companies the signed-in user may open.</summary>
    public IReadOnlyList<CompanyAccessRow> Companies { get; private set; } = Array.Empty<CompanyAccessRow>();

    /// <summary>The company currently being worked in.</summary>
    public CompanyAccessRow? Active { get; private set; }

    /// <summary>The user's role in the active company (Admin / Accountant / Viewer).</summary>
    public string? ActiveRole => Active?.Role;

    /// <summary>True when the user administers the firm and therefore reaches every company.</summary>
    public bool IsFirmAdmin { get; private set; }

    /// <summary>
    /// True when the signed-in user may post/create documents in the <em>active</em> company —
    /// a FirmAdmin, or someone granted Admin/Accountant there. This is evaluated per company
    /// (via <see cref="ActiveRole"/>), not from the user's account overall, so the same person can
    /// be an Accountant in one client's books and only a Viewer in another's.
    /// </summary>
    public bool CanPost => IsFirmAdmin || ActiveRole is AppRoles.Admin or AppRoles.Accountant;

    /// <summary>
    /// True when the signed-in user may administer the <em>active</em> company's settings and
    /// team — a FirmAdmin, or someone granted Admin there. See <see cref="CanPost"/> for why this
    /// is per-company rather than a single account-wide flag.
    /// </summary>
    public bool CanAdminister => IsFirmAdmin || ActiveRole == AppRoles.Admin;

    /// <summary>Raised when the active company changes so pages can reload their data.</summary>
    public event Action? Changed;

    /// <summary>Resolves access and selects a company. Safe to call repeatedly; only the first call works.</summary>
    public async Task EnsureInitialisedAsync()
    {
        if (_initialised) return;

        var user = (await _authProvider.GetAuthenticationStateAsync()).User;
        if (user.Identity?.IsAuthenticated != true) return;

        var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId)) return;

        IsFirmAdmin = user.IsInRole(AppRoles.FirmAdmin);
        Companies = await _access.GetCompaniesForUserAsync(userId, IsFirmAdmin);
        _initialised = true;

        // With exactly one company there is nothing to choose, so enter it directly. With several,
        // leave nothing selected — the user must pick, so it is never ambiguous which client's
        // books they are working in.
        SetActive(Companies.Count == 1 ? Companies[0] : null);
    }

    /// <summary>True when the user must still choose which company to work in.</summary>
    public bool NeedsSelection => _initialised && Active is null && Companies.Count > 0;

    /// <summary>True when the user is signed in but has not been granted any company.</summary>
    public bool HasNoCompanies => _initialised && Companies.Count == 0;

    /// <summary>Switches the active company. Ignores companies the user has no access to.</summary>
    public void SwitchTo(int companyId)
    {
        var target = Companies.FirstOrDefault(c => c.CompanyId == companyId);
        if (target is null || target.CompanyId == Active?.CompanyId) return;
        SetActive(target);
    }

    private void SetActive(CompanyAccessRow? company)
    {
        Active = company;
        _current.CompanyId = company?.CompanyId;   // re-scopes every DbContext created from here on
        _current.IsFirmAdmin = IsFirmAdmin;
        Changed?.Invoke();
    }

    /// <summary>Re-reads the access list, e.g. after a company is created or a grant changes.</summary>
    public async Task RefreshAsync()
    {
        _initialised = false;
        var keep = Active?.CompanyId;
        await EnsureInitialisedAsync();
        if (keep is int id && Companies.Any(c => c.CompanyId == id)) SwitchTo(id);
    }
}
