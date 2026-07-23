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
        return await db.CompanySetups.AsNoTracking().Include(c => c.BankAccounts)
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
        var query = db.CompanySetups.AsNoTracking().Include(c => c.BankAccounts).AsQueryable();

        var company = _current.CompanyId is int id
            ? await query.FirstOrDefaultAsync(c => c.Id == id)
            : await query.FirstOrDefaultAsync();

        return company ?? throw new PostingException(
            "No company is selected. Pick a company before opening company setup.");
    }

    public async Task SaveAsync(CompanySetup model)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var existing = await db.CompanySetups.Include(c => c.BankAccounts)
            .FirstOrDefaultAsync(c => c.Id == model.Id);

        if (existing is null)
        {
            db.CompanySetups.Add(model);
            await db.SaveChangesAsync();
            return;
        }

        // Copy scalar fields; then rebuild the bank-account list.
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

        await db.SaveChangesAsync();
    }

}
