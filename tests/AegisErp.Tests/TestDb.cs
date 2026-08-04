using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

/// <summary>
/// SQLite in-memory database shared across contexts through one open connection, pre-seeded with
/// two companies. Contexts handed out by <see cref="CreateDbContext"/> are scoped to
/// <see cref="Company"/>, so tests exercise the same query filtering the app uses.
/// <see cref="OtherCompany"/> exists so isolation can be proved.
/// </summary>
public sealed class TestDb : IDbContextFactory<AegisDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly CurrentCompany _current = new();

    public CompanySetup Company { get; }
    public CompanySetup OtherCompany { get; }

    public FiscalPeriod May { get; }
    public FiscalPeriod Jun { get; }
    public Account Bank { get; }
    public Account Ar { get; }
    public Account Vat { get; }
    public Account Revenue { get; }
    public Account Expense { get; }
    public Account Ap { get; }
    public Account VatInput { get; }
    public Account DeferredRevenue { get; }
    public Customer Customer { get; }
    public Vendor Vendor { get; }
    public CostCenter CostCenter { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var db = CreateUnscopedDbContext();
        db.Database.EnsureCreated();

        Company = new CompanySetup { LegalName = "Test Company", CompanyCode = "TEST", BaseCurrency = "AED" };
        OtherCompany = new CompanySetup { LegalName = "Other Company", CompanyCode = "OTHER", BaseCurrency = "AED" };
        db.CompanySetups.AddRange(Company, OtherCompany);
        db.SaveChanges();

        var c = Company.Id;
        May = new FiscalPeriod { CompanyId = c, Name = "May 2026", Year = 2026, PeriodNo = 11, StartDate = new(2026, 5, 1), EndDate = new(2026, 5, 31) };
        Jun = new FiscalPeriod { CompanyId = c, Name = "Jun 2026", Year = 2026, PeriodNo = 12, StartDate = new(2026, 6, 1), EndDate = new(2026, 6, 30) };
        Bank = new Account { CompanyId = c, Code = "11020", Name = "Bank", Type = AccountType.Asset };
        Ar = new Account { CompanyId = c, Code = WellKnownAccounts.AccountsReceivable, Name = "Accounts Receivable", Type = AccountType.Asset };
        Vat = new Account { CompanyId = c, Code = WellKnownAccounts.VatPayable, Name = "VAT Payable", Type = AccountType.Liability };
        Revenue = new Account { CompanyId = c, Code = "41010", Name = "Revenue", Type = AccountType.Income };
        Expense = new Account { CompanyId = c, Code = "51010", Name = "Expense", Type = AccountType.Expense };
        Ap = new Account { CompanyId = c, Code = WellKnownAccounts.AccountsPayable, Name = "Accounts Payable", Type = AccountType.Liability };
        VatInput = new Account { CompanyId = c, Code = WellKnownAccounts.VatInput, Name = "VAT Input", Type = AccountType.Asset };
        DeferredRevenue = new Account { CompanyId = c, Code = WellKnownAccounts.DeferredRevenue, Name = "Deferred Revenue", Type = AccountType.Liability };
        Customer = new Customer { CompanyId = c, Code = "C-0001", Name = "Test Customer", PaymentTermsDays = 30 };
        Vendor = new Vendor { CompanyId = c, Code = "V-0001", Name = "Test Vendor", PaymentTermsDays = 30 };
        CostCenter = new CostCenter { CompanyId = c, Code = "OPS", Name = "Operations" };

        db.FiscalPeriods.AddRange(May, Jun);
        db.Accounts.AddRange(Bank, Ar, Vat, Revenue, Expense, Ap, VatInput, DeferredRevenue);
        db.Customers.Add(Customer);
        db.Vendors.Add(Vendor);
        db.CostCenters.Add(CostCenter);
        db.SaveChanges();

        _current.CompanyId = c; // everything handed out from here is scoped to the test company
    }

    private DbContextOptions<AegisDbContext> Options =>
        new DbContextOptionsBuilder<AegisDbContext>().UseSqlite(_connection).Options;

    /// <summary>A context scoped to the active company — what application code gets.</summary>
    public AegisDbContext CreateDbContext() => new(Options, _current);

    /// <summary>A context with no company filter, for arranging cross-company fixtures.</summary>
    public AegisDbContext CreateUnscopedDbContext() => new(Options);

    public Task<AegisDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(CreateDbContext());

    /// <summary>Switches the active company, as the company switcher does in the app.</summary>
    public void SwitchTo(int companyId) => _current.CompanyId = companyId;

    /// <summary>Seeds a minimal chart of accounts, a customer and a vendor for the secondary company.</summary>
    public (Account Bank, Account Ar, Account Vat, Account Revenue, Account Ap, Account Expense,
        FiscalPeriod May, Customer Customer, Vendor Vendor) SeedOtherCompany()
    {
        using var db = CreateUnscopedDbContext();
        var o = OtherCompany.Id;

        var may = new FiscalPeriod { CompanyId = o, Name = "May 2026", Year = 2026, PeriodNo = 11, StartDate = new(2026, 5, 1), EndDate = new(2026, 5, 31) };
        // Same account codes as the primary company — legal, because codes are unique per company.
        var bank = new Account { CompanyId = o, Code = "11020", Name = "Bank", Type = AccountType.Asset };
        var ar = new Account { CompanyId = o, Code = WellKnownAccounts.AccountsReceivable, Name = "Accounts Receivable", Type = AccountType.Asset };
        var vat = new Account { CompanyId = o, Code = WellKnownAccounts.VatPayable, Name = "VAT Payable", Type = AccountType.Liability };
        var revenue = new Account { CompanyId = o, Code = "41010", Name = "Revenue", Type = AccountType.Income };
        var ap = new Account { CompanyId = o, Code = WellKnownAccounts.AccountsPayable, Name = "Accounts Payable", Type = AccountType.Liability };
        var expense = new Account { CompanyId = o, Code = "51010", Name = "Expense", Type = AccountType.Expense };
        var customer = new Customer { CompanyId = o, Code = "C-0001", Name = "Other Customer", PaymentTermsDays = 30 };
        var vendor = new Vendor { CompanyId = o, Code = "V-0001", Name = "Other Vendor", PaymentTermsDays = 30 };

        db.FiscalPeriods.Add(may);
        db.Accounts.AddRange(bank, ar, vat, revenue, ap, expense);
        db.Customers.Add(customer);
        db.Vendors.Add(vendor);
        db.SaveChanges();

        return (bank, ar, vat, revenue, ap, expense, may, customer, vendor);
    }

    public void Dispose() => _connection.Dispose();
}
