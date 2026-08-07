using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public class PayrollService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public PayrollService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<PayrollRun>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayrollRuns.AsNoTracking()
            .Include(r => r.FiscalPeriod).Include(r => r.Lines).ThenInclude(l => l.Employee)
            .OrderByDescending(r => r.RunDate).ThenByDescending(r => r.Id)
            .ToListAsync();
    }

    public async Task<PayrollRun?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.PayrollRuns.AsNoTracking()
            .Include(r => r.FiscalPeriod).Include(r => r.PaidFromBankAccount)
            .Include(r => r.Lines).ThenInclude(l => l.Employee)
            .Include(r => r.Lines).ThenInclude(l => l.ExpenseAccount)
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    /// <summary>
    /// Creates a Draft payroll run for a fiscal period, with one line per Active employee —
    /// salary/allowance fields and the expense account are snapshotted from the employee record
    /// right now, so a later salary change never retroactively alters this run. At most one run
    /// (Draft or Posted) per fiscal period, enforced by a DB unique index.
    /// </summary>
    public async Task<PayrollRun> CreateDraftRunAsync(int fiscalPeriodId, DateOnly runDate, string createdBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();

        var period = await db.FiscalPeriods.FindAsync(fiscalPeriodId)
            ?? throw new PostingException("Fiscal period not found.");
        if (await db.PayrollRuns.AnyAsync(r => r.FiscalPeriodId == fiscalPeriodId))
            throw new PostingException($"A payroll run already exists for {period.Name}.");

        var employees = await db.Employees
            .Where(m => m.Status == EmployeeStatus.Active)
            .ToListAsync();
        if (employees.Count == 0)
            throw new PostingException("There are no active employees to run payroll for.");

        var run = new PayrollRun
        {
            FiscalPeriodId = fiscalPeriodId,
            RunDate = runDate,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };
        foreach (var m in employees)
            run.Lines.Add(new PayrollRunLine
            {
                EmployeeId = m.Id,
                BasicSalary = m.BasicSalary,
                HousingAllowance = m.HousingAllowance,
                TransportAllowance = m.TransportAllowance,
                OtherAllowance = m.OtherAllowance,
                ExpenseAccountId = m.EmployeeExpenseAccountId,
            });

        db.PayrollRuns.Add(run);
        await db.SaveChangesAsync();
        return run;
    }

    /// <summary>Adjusts one employee's deduction on a run that hasn't been posted yet.</summary>
    public async Task UpdateRunLineAsync(int runLineId, decimal deductions)
    {
        if (deductions < 0) throw new PostingException("Deductions cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        // Route through the company-scoped PayrollRuns (not db.PayrollRunLines directly) so a
        // runLineId can never reach another company's line — PayrollRunLine has no CompanyId/
        // filter of its own, same reasoning as every other line-level entity in this codebase.
        var line = await db.PayrollRuns.SelectMany(r => r.Lines).FirstOrDefaultAsync(l => l.Id == runLineId)
            ?? throw new PostingException("Payroll run line not found.");
        var run = await db.PayrollRuns.FirstAsync(r => r.Id == line.PayrollRunId);
        if (run.Status != VoucherStatus.Draft)
            throw new PostingException("Only a Draft run's lines can be adjusted.");
        if (deductions > line.GrossPay)
            throw new PostingException("Deductions cannot exceed gross pay.");

        line.Deductions = deductions;
        await db.SaveChangesAsync();
    }
}
