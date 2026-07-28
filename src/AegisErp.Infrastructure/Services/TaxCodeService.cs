using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Tax Code" form collects.</summary>
public record NewTaxCodeInput(string Code, string Description, decimal Rate, TaxCodeKind Kind, int? GlAccountId, DateOnly EffectiveFrom);

public class TaxCodeService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public TaxCodeService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<TaxCode>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.TaxCodes.AsNoTracking().Include(t => t.GlAccount)
            .OrderBy(t => t.Code).ToListAsync();
    }

    public async Task<TaxCode> AddAsync(NewTaxCodeInput input)
    {
        var code = input.Code.Trim().ToUpperInvariant();
        var description = input.Description.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PostingException("Tax code is required.");
        if (string.IsNullOrWhiteSpace(description)) throw new PostingException("Description is required.");
        if (input.Rate < 0) throw new PostingException("Rate cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.TaxCodes.AnyAsync(t => t.Code == code))
            throw new PostingException($"Tax code {code} already exists.");

        var tax = new TaxCode
        {
            Code = code,
            Description = description,
            Rate = input.Rate,
            Kind = input.Kind,
            GlAccountId = input.GlAccountId,
            EffectiveFrom = input.EffectiveFrom,
        };
        db.TaxCodes.Add(tax);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return tax;
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var tax = await db.TaxCodes.FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new PostingException("Tax code not found.");
        tax.IsActive = isActive;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var tax = await db.TaxCodes.FirstOrDefaultAsync(t => t.Id == id)
            ?? throw new PostingException("Tax code not found.");
        db.TaxCodes.Remove(tax);
        await db.SaveChangesAsync();
    }
}
