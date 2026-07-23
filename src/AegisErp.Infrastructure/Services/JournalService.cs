using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public class JournalService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public JournalService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<JournalVoucher>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.JournalVouchers.AsNoTracking()
            .Include(v => v.Lines)
            .OrderByDescending(v => v.Date).ThenByDescending(v => v.Id)
            .Take(take).ToListAsync();
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
