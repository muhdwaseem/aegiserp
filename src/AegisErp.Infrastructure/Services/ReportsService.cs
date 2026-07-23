using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>
/// Cash flow and MIS / segment reporting, derived from posted vouchers. Decimal aggregates are
/// done in memory after materialising so the same code works on SQLite and PostgreSQL alike.
/// </summary>
public class ReportsService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public ReportsService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    private sealed record PostedLine(DateOnly Date, int AccountId, int? CostCenterId, decimal Debit, decimal Credit);

    private static Task<List<PostedLine>> PostedLinesAsync(AegisDbContext db) =>
        db.JournalLines.AsNoTracking()
            .Where(l => l.JournalVoucher.Status == VoucherStatus.Posted)
            .Select(l => new PostedLine(l.JournalVoucher.Date, l.AccountId, l.CostCenterId, l.Debit, l.Credit))
            .ToListAsync();

    /// <summary>Which cash-flow section an account belongs to. Every account lands in exactly one.</summary>
    private enum Bucket { Cash, Operating, Investing, Financing }

    private static Bucket Classify(Account a) =>
        a.Type == AccountType.Asset && a.Code.StartsWith("110") ? Bucket.Cash        // cash & bank
        : a.Type == AccountType.Asset && a.Code.StartsWith("15") ? Bucket.Investing  // fixed assets
        : a.Type == AccountType.Equity ? Bucket.Financing                            // capital
        : Bucket.Operating;                                                          // working capital + P&L

    /// <summary>
    /// Indirect-method cash flow for a fiscal period. Because every posted voucher balances, the
    /// movements across all accounts sum to zero — so each section's cash effect is simply the
    /// negative of that group's movement, and the three sections reconcile to the cash accounts.
    /// </summary>
    public async Task<CashFlowStatement> GetCashFlowAsync(int periodId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var period = await db.FiscalPeriods.AsNoTracking().FirstAsync(p => p.Id == periodId);
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var lines = await PostedLinesAsync(db);

        var inPeriod = lines.Where(l => l.Date >= period.StartDate && l.Date <= period.EndDate).ToList();

        // Signed, debit-positive movement per account inside the period.
        var movement = inPeriod
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));

        // ── Cash position ──
        var cashIds = accounts.Values.Where(a => Classify(a) == Bucket.Cash).Select(a => a.Id).ToHashSet();
        var openingCash = lines
            .Where(l => cashIds.Contains(l.AccountId) && l.Date < period.StartDate)
            .Sum(l => l.Debit - l.Credit);
        var closingCash = openingCash + inPeriod
            .Where(l => cashIds.Contains(l.AccountId))
            .Sum(l => l.Debit - l.Credit);

        bool IsPnl(int accountId) => accounts[accountId].Type is AccountType.Income or AccountType.Expense;

        // ── A. Operating: net profit, then working-capital movements ──
        var netProfit = -movement.Where(kv => IsPnl(kv.Key)).Sum(kv => kv.Value);

        var operating = new List<CashFlowLine> { new("Net profit for the period", netProfit) };
        foreach (var kv in movement
                     .Where(kv => Classify(accounts[kv.Key]) == Bucket.Operating && !IsPnl(kv.Key))
                     .OrderBy(kv => accounts[kv.Key].Code))
        {
            var effect = -kv.Value; // cash effect is the opposite of the balance movement
            if (effect == 0) continue;
            var a = accounts[kv.Key];
            // An asset going up consumes cash; a liability going up releases it.
            var direction = a.Type == AccountType.Asset
                ? (effect < 0 ? "Increase in" : "Decrease in")
                : (effect > 0 ? "Increase in" : "Decrease in");
            operating.Add(new CashFlowLine($"{direction} {a.Name}", effect));
        }

        // ── B. Investing / C. Financing ──
        List<CashFlowLine> Section(Bucket bucket, Func<decimal, Account, string> label) =>
            movement
                .Where(kv => Classify(accounts[kv.Key]) == bucket)
                .Select(kv => new CashFlowLine(label(-kv.Value, accounts[kv.Key]), -kv.Value))
                .Where(l => l.Amount != 0)
                .OrderBy(l => l.Label)
                .ToList();

        var investing = Section(Bucket.Investing,
            (amt, a) => amt < 0 ? $"Purchase of {a.Name}" : $"Disposal of {a.Name}");
        var financing = Section(Bucket.Financing,
            (amt, a) => amt > 0 ? $"Capital introduced — {a.Name}" : $"Drawings / distributions — {a.Name}");

        return new CashFlowStatement(
            period.Name,
            new CashFlowSection("Operating Activities", operating, operating.Sum(l => l.Amount)),
            new CashFlowSection("Investing Activities", investing, investing.Sum(l => l.Amount)),
            new CashFlowSection("Financing Activities", financing, financing.Sum(l => l.Amount)),
            openingCash, closingCash);
    }

    /// <summary>Revenue, expense and net per segment (cost centre) over a date range.</summary>
    public async Task<List<SegmentPnlRow>> GetSegmentPnlAsync(DateOnly from, DateOnly to)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var accounts = await db.Accounts.AsNoTracking().ToDictionaryAsync(a => a.Id);
        var costCenters = await db.CostCenters.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var lines = (await PostedLinesAsync(db))
            .Where(l => l.Date >= from && l.Date <= to
                        && accounts[l.AccountId].Type is AccountType.Income or AccountType.Expense)
            .ToList();

        var rows = new List<SegmentPnlRow>();
        foreach (var group in lines.GroupBy(l => l.CostCenterId))
        {
            var revenue = -group.Where(l => accounts[l.AccountId].Type == AccountType.Income)
                                .Sum(l => l.Debit - l.Credit);
            var expense = group.Where(l => accounts[l.AccountId].Type == AccountType.Expense)
                               .Sum(l => l.Debit - l.Credit);
            if (revenue == 0 && expense == 0) continue;

            var (code, name) = group.Key is int id && costCenters.TryGetValue(id, out var cc)
                ? (cc.Code, cc.Name)
                : ("—", "Unassigned");
            rows.Add(new SegmentPnlRow(code, name, revenue, expense));
        }
        return rows.OrderByDescending(r => r.Revenue).ThenBy(r => r.Name).ToList();
    }

    /// <summary>Revenue per customer over a date range: posted invoices net of credit notes, excluding VAT.</summary>
    public async Task<List<PartyAmountRow>> GetCustomerRevenueAsync(DateOnly from, DateOnly to)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var customers = await db.Customers.AsNoTracking().ToListAsync();
        var invoices = await db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Status == VoucherStatus.Posted && i.Date >= from && i.Date <= to).ToListAsync();
        var credits = await db.CreditNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => n.Status == VoucherStatus.Posted && n.Date >= from && n.Date <= to).ToListAsync();

        return customers
            .Select(c =>
            {
                var inv = invoices.Where(i => i.CustomerId == c.Id).ToList();
                var cn = credits.Where(n => n.CustomerId == c.Id).ToList();
                return new PartyAmountRow(c.Code, c.Name,
                    inv.Sum(i => i.TotalNet) - cn.Sum(n => n.TotalNet), inv.Count + cn.Count);
            })
            .Where(r => r.DocumentCount > 0)
            .OrderByDescending(r => r.Amount)
            .ToList();
    }

    /// <summary>Spend per vendor over a date range: posted purchase invoices net of debit notes, excluding VAT.</summary>
    public async Task<List<PartyAmountRow>> GetVendorSpendAsync(DateOnly from, DateOnly to)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var vendors = await db.Vendors.AsNoTracking().ToListAsync();
        var invoices = await db.PurchaseInvoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Status == VoucherStatus.Posted && i.Date >= from && i.Date <= to).ToListAsync();
        var debits = await db.DebitNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => n.Status == VoucherStatus.Posted && n.Date >= from && n.Date <= to).ToListAsync();

        return vendors
            .Select(v =>
            {
                var inv = invoices.Where(i => i.VendorId == v.Id).ToList();
                var dn = debits.Where(n => n.VendorId == v.Id).ToList();
                return new PartyAmountRow(v.Code, v.Name,
                    inv.Sum(i => i.TotalNet) - dn.Sum(n => n.TotalNet), inv.Count + dn.Count);
            })
            .Where(r => r.DocumentCount > 0)
            .OrderByDescending(r => r.Amount)
            .ToList();
    }
}
