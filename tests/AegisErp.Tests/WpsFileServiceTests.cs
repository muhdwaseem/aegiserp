using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class WpsFileServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EmployeeService _employees;
    private readonly PayrollService _payroll;
    private readonly WpsFileService _wps;
    private static readonly DateTime Now = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    public WpsFileServiceTests()
    {
        _employees = new EmployeeService(_db);
        _payroll = new PayrollService(_db);
        _wps = new WpsFileService(_db, new CurrentCompany { CompanyId = _db.Company.Id });

        using var db = _db.CreateUnscopedDbContext();
        var salariesPayable = new Account { CompanyId = _db.Company.Id, Code = WellKnownAccounts.SalariesPayable, Name = "Salaries Payable", Type = AccountType.Liability };
        db.Accounts.Add(salariesPayable);
        var company = db.CompanySetups.Single(c => c.Id == _db.Company.Id);
        company.MohreEstablishmentId = "1234567890123";
        company.WpsBankAgentId = "123456789";
        db.SaveChanges();
    }

    public void Dispose() => _db.Dispose();

    private async Task<PayrollRun> CreatePostedRun(string? labourCard, string? agentId, string? iban,
        decimal basic = 5000, decimal housing = 1500, decimal transport = 500, decimal other = 200)
    {
        await _employees.CreateAsync(
            new EmployeeInput("Ahmed Al Mansoori", null, null, new(2026, 1, 1), basic, housing, transport, other, null, null, null, iban, _db.Expense.Id, null)
                with { LabourCardNumber = labourCard, WpsAgentId = agentId },
            "tester", Now);
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);
        await _payroll.PostRunAsync(run.Id, null, "tester", Now);
        return (await _payroll.GetByIdAsync(run.Id))!;
    }

    [Fact]
    public async Task GenerateSifAsync_produces_the_exact_scr_and_edr_lines()
    {
        var run = await CreatePostedRun("12345678901234", "987654321", "AE070331234567890123456");

        var file = await _wps.GenerateSifAsync(run.Id, Now);

        var lines = file.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("SCR,1234567890123,123456789,2026-05-20,1200,052026,1,7200.00,AED", lines[0]);
        Assert.Equal("EDR,12345678901234,987654321,AE070331234567890123456,2026-05-01,2026-05-31,31,7000.00,200.00,", lines[1]);
    }

    [Fact]
    public async Task GenerateSifAsync_filename_matches_the_25_character_spec()
    {
        var run = await CreatePostedRun("12345678901234", "987654321", "AE070331234567890123456");

        var file = await _wps.GenerateSifAsync(run.Id, Now);

        Assert.Equal("1234567890123" + "260520" + "120000" + ".sif", file.FileName);
    }

    [Fact]
    public async Task GenerateSifAsync_pads_a_short_labour_card_number_to_14_digits()
    {
        var run = await CreatePostedRun("555", "987654321", "AE070331234567890123456");

        var file = await _wps.GenerateSifAsync(run.Id, Now);

        Assert.Contains("EDR,00000000000555,", file.Content);
    }

    [Fact]
    public async Task GenerateSifAsync_throws_naming_the_employee_missing_a_required_field()
    {
        await _employees.CreateAsync(
            new EmployeeInput("Fatima", null, null, new(2026, 1, 1), 5000, 0, 0, 0, null, null, null, null, _db.Expense.Id, null),
            "tester", Now); // no LabourCardNumber/WpsAgentId/Iban
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);
        await _payroll.PostRunAsync(run.Id, null, "tester", Now);

        var ex = await Assert.ThrowsAsync<PostingException>(() => _wps.GenerateSifAsync(run.Id, Now));
        Assert.Contains("Fatima", ex.Message);
    }

    [Fact]
    public async Task GenerateSifAsync_rejects_a_draft_run()
    {
        await _employees.CreateAsync(
            new EmployeeInput("Ahmed", null, null, new(2026, 1, 1), 5000, 0, 0, 0, null, null, null, "AE07033", _db.Expense.Id, null)
                with { LabourCardNumber = "1", WpsAgentId = "1" },
            "tester", Now);
        var run = await _payroll.CreateDraftRunAsync(_db.May.Id, new(2026, 5, 31), "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _wps.GenerateSifAsync(run.Id, Now));
    }

    [Fact]
    public async Task GenerateSifAsync_requires_the_companys_mohre_and_bank_agent_ids()
    {
        using (var db = _db.CreateUnscopedDbContext())
        {
            var company = db.CompanySetups.Single(c => c.Id == _db.Company.Id);
            company.MohreEstablishmentId = null;
            db.SaveChanges();
        }

        var run = await CreatePostedRun("12345678901234", "987654321", "AE070331234567890123456");

        await Assert.ThrowsAsync<PostingException>(() => _wps.GenerateSifAsync(run.Id, Now));
    }
}
