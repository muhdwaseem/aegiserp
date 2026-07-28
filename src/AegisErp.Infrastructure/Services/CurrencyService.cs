using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

public class CurrencyService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public CurrencyService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<Currency>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Currencies.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
    }

    public async Task<Currency> AddAsync(string code, string name, decimal rateToBase)
    {
        code = code.Trim().ToUpperInvariant();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(code)) throw new PostingException("Currency code is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Currency name is required.");
        if (rateToBase <= 0) throw new PostingException("Exchange rate must be greater than zero.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.Currencies.AnyAsync(c => c.Code == code))
            throw new PostingException($"Currency {code} already exists.");

        var currency = new Currency { Code = code, Name = name, RateToBase = rateToBase, UpdatedAtUtc = DateTime.UtcNow };
        db.Currencies.Add(currency);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return currency;
    }

    public async Task UpdateRateAsync(int id, decimal rateToBase)
    {
        if (rateToBase <= 0) throw new PostingException("Exchange rate must be greater than zero.");

        await using var db = await _dbf.CreateDbContextAsync();
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Currency not found.");
        currency.RateToBase = rateToBase;
        currency.UpdatedAtUtc = DateTime.UtcNow;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Currency not found.");
        currency.IsActive = isActive;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var currency = await db.Currencies.FirstOrDefaultAsync(c => c.Id == id)
            ?? throw new PostingException("Currency not found.");
        db.Currencies.Remove(currency);
        await db.SaveChangesAsync();
    }
}
