using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public class JournalService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public JournalService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    /// <summary>
    /// Recent postings to the GL. Every posted document creates a row here — invoices, receipts,
    /// payments, credit/debit notes and journal entries alike — so pass <paramref name="type"/>
    /// to narrow to just one kind (e.g. the Journal Voucher page's own entries).
    /// </summary>
    public async Task<List<JournalVoucher>> GetRecentAsync(int take = 50, VoucherType? type = null)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.JournalVouchers.AsNoTracking().Include(v => v.Lines).AsQueryable();
        if (type is VoucherType t) q = q.Where(v => v.Type == t);
        return await q.OrderByDescending(v => v.Date).ThenByDescending(v => v.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>One voucher with full detail (lines, their accounts and cost centers) — for the
    /// "view voucher" popup opened from a General Ledger row's voucher link.</summary>
    public async Task<JournalVoucher?> GetByVoucherNoAsync(string voucherNo)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.JournalVouchers.AsNoTracking()
            .Include(v => v.Lines).ThenInclude(l => l.Account)
            .Include(v => v.Lines).ThenInclude(l => l.CostCenter)
            .FirstOrDefaultAsync(v => v.VoucherNo == voucherNo);
    }

    /// <summary>One voucher with full detail, by id — for the Journal Voucher page's detail panel.</summary>
    public async Task<JournalVoucher?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.JournalVouchers.AsNoTracking()
            .Include(v => v.Lines).ThenInclude(l => l.Account)
            .Include(v => v.Lines).ThenInclude(l => l.CostCenter)
            .FirstOrDefaultAsync(v => v.Id == id);
    }

    /// <summary>Next document number for a type within a year, e.g. "JV-2026-0007".</summary>
    public async Task<string> PeekNextVoucherNoAsync(VoucherType type, int year)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await JournalPoster.NextDocNoAsync(db, JournalPoster.Prefixes[type], year);
    }

    /// <summary>
    /// Creates a voucher and posts it in a single transaction. Validation happens in the
    /// domain (<see cref="JournalVoucher.Post"/>); the transaction guarantees all-or-nothing.
    /// </summary>
    public async Task<JournalVoucher> CreateAndPostAsync(
        VoucherType type, DateOnly date, int fiscalPeriodId,
        string? narration, string? reference, string createdBy,
        IEnumerable<VoucherLineInput> lines, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var voucher = await JournalPoster.PostAsync(
            db, type, explicitNo: null, date, fiscalPeriodId,
            narration, reference, createdBy, lines, nowUtc);

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return voucher;
    }
}
