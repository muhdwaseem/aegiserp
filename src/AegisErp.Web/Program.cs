using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Identity;
using AegisErp.Web.Components;
using AegisErp.Web.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Serve _content/* library assets (MudBlazor css/js) even outside the Development
// environment when running from source. No-op for published output.
builder.WebHost.UseStaticWebAssets();

// Honour the hosting platform's PORT env var (Render and similar) when present.
// Fly uses the Dockerfile's fixed 8080; local dev uses launch settings.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Blazor Server (interactive server components).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// ── Authentication & authorization (cookie-based Identity) ──
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
}).AddIdentityCookies();

builder.Services.AddAuthorization();

builder.Services.AddIdentityCore<AppUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = false;
    options.Password.RequiredLength = 8;
})
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AegisDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

// EF Core (provider chosen by config) + application services.
builder.Services.AddAegisInfrastructure(builder.Configuration);

// Per-circuit active-company state; drives the DbContext query filters.
builder.Services.AddScoped<AegisErp.Web.CompanySession>();

var app = builder.Build();

// Create the database (if missing) and seed roles, users and the demo company on startup.
using (var scope = app.Services.CreateScope())
{
    var sp = scope.ServiceProvider;
    var dbf = sp.GetRequiredService<IDbContextFactory<AegisDbContext>>();
    await using var db = await dbf.CreateDbContextAsync();

    // WAL mode lets readers proceed while a write is in flight (default rollback-journal mode
    // blocks everyone on a writer). Only meaningful for Sqlite; a no-op statement on other
    // providers would just error, so gate it on the configured provider.
    var provider = builder.Configuration["Database:Provider"] ?? DatabaseProvider.Sqlite;
    if (provider == DatabaseProvider.Sqlite)
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        // EnsureCreated is fine here: local dev owns this file and can delete it after a schema
        // change (see README). Never use EnsureCreated against Postgres — see below.
        await db.Database.EnsureCreatedAsync();
    }
    else
    {
        // Real deployments apply versioned migrations instead of EnsureCreated, so a schema
        // change ships as an upgrade instead of requiring the client's database to be wiped.
        await db.Database.MigrateAsync();
    }

    // A real client deployment sets Seed:DemoData=false (see render.yaml) so its database gets
    // roles + one bootstrap FirmAdmin (Seed:AdminEmail/AdminPassword) instead of the demo
    // companies/users/sample documents used for local dev and trials.
    var seedDemoData = builder.Configuration.GetValue<bool?>("Seed:DemoData") ?? true;
    var adminEmail = builder.Configuration["Seed:AdminEmail"];
    var adminPassword = builder.Configuration["Seed:AdminPassword"];
    (string, string, string)? bootstrapAdmin = !seedDemoData && !string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword)
        ? (adminEmail, adminPassword, "System Admin")
        : null;

    await SeedData.EnsureSeededAsync(db,
        sp.GetRequiredService<UserManager<AppUser>>(),
        sp.GetRequiredService<RoleManager<IdentityRole>>(),
        seedDemoData, bootstrapAdmin);
}

// Behind a reverse proxy (Fly.io / Azure) honour X-Forwarded-Proto so HTTPS redirect and
// secure auth cookies see the real https scheme. No-op when running locally (no such headers).
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedOptions.KnownNetworks.Clear();
forwardedOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedOptions);

// TEMPORARY diagnostic: log every redirect response (method, path, resulting status, Location,
// and whether the request carried an auth cookie) so a redirect-loop report shows the actual
// chain in the deploy logs instead of guessing blind. Remove once the loop is diagnosed.
app.Use(async (context, next) =>
{
    var hadAuthCookie = context.Request.Cookies.Keys.Any(k => k.Contains("Identity", StringComparison.OrdinalIgnoreCase));
    await next();
    if (context.Response.StatusCode is >= 300 and < 400)
    {
        Console.WriteLine(
            $"[Redirect] {context.Request.Scheme} {context.Request.Method} {context.Request.Path}{context.Request.QueryString} " +
            $"authCookiePresent={hadAuthCookie} -> {context.Response.StatusCode} Location={context.Response.Headers.Location}");
    }
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// NOTE: no app-level HTTPS redirect. Hosting platforms (Render, Fly.io, Azure) terminate TLS at
// their edge and force HTTPS there, then forward plain HTTP to the container. An in-app redirect
// would fire on the platform's internal HTTP health-check probe (returning a 307 instead of 200)
// and the deploy would be marked unhealthy. HSTS above still advises browsers to use HTTPS.
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Logout must be a POST so browsers/prefetchers can't trigger it via GET.
app.MapPost("/Account/Logout", async (SignInManager<AppUser> signInManager) =>
{
    await signInManager.SignOutAsync();
    return Results.LocalRedirect("~/Account/Login");
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
