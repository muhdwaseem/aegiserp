using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public record FixedAssetInput(
    string Name, string? Category, DateOnly PurchaseDate, decimal PurchaseCost, decimal SalvageValue,
    int UsefulLifeMonths, int AssetAccountId, int DepreciationExpenseAccountId, int? CostCenterId);

public class FixedAssetService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public FixedAssetService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<FixedAsset>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.FixedAssets.AsNoTracking()
            .Include(a => a.AssetAccount).Include(a => a.DepreciationExpenseAccount).Include(a => a.CostCenter)
            .Include(a => a.DepreciationEntries)
            .OrderBy(a => a.AssetCode)
            .ToListAsync();
    }

    public async Task<FixedAsset?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.FixedAssets.AsNoTracking()
            .Include(a => a.AssetAccount).Include(a => a.DepreciationExpenseAccount).Include(a => a.CostCenter)
            .Include(a => a.DepreciationEntries).ThenInclude(e => e.FiscalPeriod)
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    /// <summary>
    /// Registers a fixed asset. Purely administrative — does NOT post to the GL. Capitalizing the
    /// purchase cost is assumed to already have happened via a normal Purchase Invoice or Journal
    /// Voucher coded to <paramref name="input"/>'s <see cref="FixedAssetInput.AssetAccountId"/>;
    /// this record is a subsidiary tracking register on top of that, not a second posting of it.
    /// </summary>
    public async Task<FixedAsset> CreateAsync(FixedAssetInput input, string createdBy, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Asset name is required.");
        if (input.PurchaseCost <= 0) throw new PostingException("Purchase cost must be positive.");
        if (input.SalvageValue < 0) throw new PostingException("Salvage value cannot be negative.");
        if (input.SalvageValue >= input.PurchaseCost) throw new PostingException("Salvage value must be less than purchase cost.");
        if (input.UsefulLifeMonths <= 0) throw new PostingException("Useful life must be at least 1 month.");

        await using var db = await _dbf.CreateDbContextAsync();

        // Next code = max numeric suffix + 1, same convention as Customer/Vendor codes.
        var codes = await db.FixedAssets.Select(a => a.AssetCode).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("FA-") && int.TryParse(code.AsSpan(3), out var n) && n > max)
                max = n;

        var asset = new FixedAsset
        {
            AssetCode = $"FA-{max + 1:0000}",
            Name = input.Name.Trim(),
            Category = string.IsNullOrWhiteSpace(input.Category) ? null : input.Category.Trim(),
            PurchaseDate = input.PurchaseDate,
            PurchaseCost = input.PurchaseCost,
            SalvageValue = input.SalvageValue,
            UsefulLifeMonths = input.UsefulLifeMonths,
            AssetAccountId = input.AssetAccountId,
            DepreciationExpenseAccountId = input.DepreciationExpenseAccountId,
            CostCenterId = input.CostCenterId,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        db.FixedAssets.Add(asset);
        await db.SaveChangesAsync();
        return asset;
    }

    /// <summary>
    /// Posts one period's straight-line depreciation for every Active asset that hasn't already
    /// been depreciated for <paramref name="fiscalPeriodId"/> — one combined <see cref="JournalVoucher"/>
    /// for the whole run (Dr each asset's own Depreciation Expense account / Cr the shared
    /// AccumulatedDepreciation control account), not one voucher per asset. Returns null if no
    /// asset was due (e.g. everything's already posted, or every asset is fully depreciated) —
    /// a zero-line voucher would fail <see cref="JournalVoucher"/>'s own validation anyway.
    /// Safe to call repeatedly for the same period: the DB has a unique (FixedAssetId,
    /// FiscalPeriodId) index, and this method also checks first so a repeat call is a no-op.
    /// </summary>
    public async Task<JournalVoucher?> RunDepreciationAsync(int fiscalPeriodId, string postedBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var period = await db.FiscalPeriods.FindAsync(fiscalPeriodId)
            ?? throw new PostingException("Fiscal period not found.");

        // Route through the company-scoped FixedAssets set (not db.AssetDepreciationEntries
        // directly) so a fiscalPeriodId can never surface another company's already-posted rows —
        // AssetDepreciationEntry has no CompanyId/filter of its own, same reasoning as every other
        // line-level entity in this codebase.
        var alreadyPostedAssetIds = (await db.FixedAssets.SelectMany(a => a.DepreciationEntries)
            .Where(e => e.FiscalPeriodId == fiscalPeriodId)
            .Select(e => e.FixedAssetId)
            .ToListAsync())
            .ToHashSet();

        var assets = await db.FixedAssets
            .Where(a => a.Status == FixedAssetStatus.Active && !alreadyPostedAssetIds.Contains(a.Id))
            .ToListAsync();

        var due = assets.Select(a => (Asset: a, Amount: a.NextDepreciationAmount()))
            .Where(x => x.Amount > 0).ToList();
        if (due.Count == 0) return null;

        var lines = due.Select(x => new VoucherLineInput(
            x.Asset.DepreciationExpenseAccountId, x.Asset.CostCenterId,
            $"{x.Asset.AssetCode} — {x.Asset.Name} — {period.Name} depreciation", x.Amount, 0)).ToList();

        // Credit total is the sum of the already-rounded per-asset debit amounts — never an
        // independently recomputed figure — so the voucher can never be a cent off balanced.
        var total = lines.Sum(l => l.Debit);
        var accDep = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccumulatedDepreciation);
        lines.Add(new VoucherLineInput(accDep.Id, null, $"Accumulated depreciation — {period.Name}", 0, total));

        var voucher = await JournalPoster.PostAsync(db, VoucherType.Journal, null, period.EndDate, fiscalPeriodId,
            $"Depreciation run — {period.Name}", null, postedBy, lines, nowUtc);

        foreach (var (asset, amount) in due)
            db.AssetDepreciationEntries.Add(new AssetDepreciationEntry
            {
                FixedAssetId = asset.Id,
                FiscalPeriodId = fiscalPeriodId,
                Date = period.EndDate,
                Amount = amount,
                JournalVoucher = voucher,
            });

        await JournalPoster.SaveAndCommitAsync(db, tx);
        return voucher;
    }
}
