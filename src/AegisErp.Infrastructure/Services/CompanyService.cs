using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Loads and saves company records. Reads/writes the user's <em>active</em> company.</summary>
public class CompanyService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly ICurrentCompany _current;

    public CompanyService(IDbContextFactory<AegisDbContext> dbf, ICurrentCompany current)
    {
        _dbf = dbf;
        _current = current;
    }

    /// <summary>Every company in the system (firm-wide; used by the switcher and companies page).</summary>
    public async Task<List<CompanySetup>> GetAllAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CompanySetups.AsNoTracking().OrderBy(c => c.LegalName).ToListAsync();
    }

    public async Task<CompanySetup?> GetByIdAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CompanySetups.AsNoTracking().Include(c => c.BankAccounts).Include(c => c.Salespersons)
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    /// <summary>Creates a new company (client). Company code must be unique.</summary>
    public async Task<CompanySetup> CreateAsync(CompanySetup model)
    {
        if (string.IsNullOrWhiteSpace(model.LegalName))
            throw new PostingException("Company legal name is required.");

        var code = (model.CompanyCode ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(code))
            throw new PostingException("Company code is required.");

        await using var db = await _dbf.CreateDbContextAsync();
        if (await db.CompanySetups.AnyAsync(c => c.CompanyCode == code))
            throw new PostingException($"A company with code {code} already exists.");

        model.CompanyCode = code;
        db.CompanySetups.Add(model);
        await db.SaveChangesAsync();
        return model;
    }

    /// <summary>The active company's setup record.</summary>
    public async Task<CompanySetup> GetAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var query = db.CompanySetups.AsNoTracking().Include(c => c.BankAccounts).Include(c => c.Salespersons).AsQueryable();

        var company = _current.CompanyId is int id
            ? await query.FirstOrDefaultAsync(c => c.Id == id)
            : await query.FirstOrDefaultAsync();

        return company ?? throw new PostingException(
            "No company is selected. Pick a company before opening company setup.");
    }

    /// <summary>
    /// Saves the company's setup. <paramref name="model"/> must carry the RowVersion it was
    /// loaded with (from <see cref="GetAsync"/>) — if another user has saved a change since, this
    /// throws a recoverable <see cref="PostingException"/> instead of silently overwriting it.
    /// </summary>
    public async Task SaveAsync(CompanySetup model, string updatedBy)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.CompanySetups.Include(c => c.BankAccounts).Include(c => c.Salespersons)
            .FirstOrDefaultAsync(c => c.Id == model.Id);

        if (existing is null)
        {
            db.CompanySetups.Add(model);
            await db.SaveChangesAsync();
            return;
        }

        var expectedRowVersion = model.RowVersion;

        // Copy scalar fields; then rebuild the bank-account and salesperson lists.
        db.Entry(existing).CurrentValues.SetValues(model);
        db.CompanyBankAccounts.RemoveRange(existing.BankAccounts);
        existing.BankAccounts = model.BankAccounts.Select(b => new CompanyBankAccount
        {
            BankName = b.BankName,
            AccountName = b.AccountName,
            AccountNumber = b.AccountNumber,
            Iban = b.Iban,
            Swift = b.Swift,
            Currency = string.IsNullOrWhiteSpace(b.Currency) ? "AED" : b.Currency,
            IsPrimary = b.IsPrimary,
        }).ToList();

        db.Salespersons.RemoveRange(existing.Salespersons);
        existing.Salespersons = model.Salespersons
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new Salesperson { Name = s.Name.Trim() })
            .ToList();

        existing.UpdatedBy = updatedBy;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        existing.RowVersion = Guid.NewGuid();
        // Check against the version the editor actually loaded, not existing's freshly-read value
        // (SetValues above only touches CurrentValues; OriginalValues still holds the DB's current
        // row, which would defeat the check unless overridden here).
        db.Entry(existing).Property(c => c.RowVersion).OriginalValue = expectedRowVersion;

        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

}
