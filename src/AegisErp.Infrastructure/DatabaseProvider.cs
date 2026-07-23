using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure;

/// <summary>
/// Centralises provider selection so the app, tests and design-time tooling all configure
/// the DbContext the same way. Switch providers via the "Database:Provider" config key.
/// </summary>
public static class DatabaseProvider
{
    public const string Sqlite = "Sqlite";
    public const string Postgres = "Postgres";
    public const string SqlServer = "SqlServer";   // SQL Server / Azure SQL Database

    public static void Configure(DbContextOptionsBuilder options, string provider, string connectionString)
    {
        switch (provider)
        {
            case SqlServer:
                // Retry on the transient faults that managed SQL (Azure SQL) occasionally raises.
                options.UseSqlServer(connectionString, sql => sql.EnableRetryOnFailure());
                break;
            case Postgres:
                options.UseNpgsql(connectionString);
                break;
            case Sqlite:
            default:
                options.UseSqlite(connectionString);
                break;
        }
    }
}
