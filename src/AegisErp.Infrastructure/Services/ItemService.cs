using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Item" form collects.</summary>
public record NewItemInput(
    string Name, ItemKind Kind, string Unit,
    decimal SellingPrice, int? SalesAccountId, string? SalesDescription,
    decimal? CostPrice, int? PurchaseAccountId, string? PurchaseDescription,
    int? TaxCodeId);

public class ItemService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public ItemService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<Item>> GetAllAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Items.AsNoTracking().Include(i => i.SalesAccount).Include(i => i.PurchaseAccount)
            .Include(i => i.TaxCode).OrderBy(i => i.Code).AsQueryable();
        if (activeOnly) q = q.Where(i => i.IsActive);
        return await q.ToListAsync();
    }

    /// <summary>The next item code that will be assigned (for display on the New form).</summary>
    public async Task<string> PeekNextCodeAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.Items.Select(i => i.Code).ToListAsync();
        return $"ITM-{NextSuffix(codes):0000}";
    }

    private static int NextSuffix(List<string> codes)
    {
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("ITM-") && int.TryParse(code.AsSpan(4), out var n) && n > max)
                max = n;
        return max + 1;
    }

    public async Task<Item> CreateAsync(NewItemInput input)
    {
        var name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Item name is required.");
        if (input.SellingPrice < 0) throw new PostingException("Selling price cannot be negative.");
        if (input.CostPrice is < 0) throw new PostingException("Cost price cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.Items.Select(i => i.Code).ToListAsync();

        var item = new Item
        {
            Code = $"ITM-{NextSuffix(codes):0000}",
            Name = name,
            Kind = input.Kind,
            Unit = string.IsNullOrWhiteSpace(input.Unit) ? "unit" : input.Unit.Trim(),
            SellingPrice = input.SellingPrice,
            SalesAccountId = input.SalesAccountId,
            SalesDescription = string.IsNullOrWhiteSpace(input.SalesDescription) ? null : input.SalesDescription.Trim(),
            CostPrice = input.CostPrice,
            PurchaseAccountId = input.PurchaseAccountId,
            PurchaseDescription = string.IsNullOrWhiteSpace(input.PurchaseDescription) ? null : input.PurchaseDescription.Trim(),
            TaxCodeId = input.TaxCodeId,
        };
        db.Items.Add(item);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return item;
    }

    public async Task UpdateAsync(int id, NewItemInput input)
    {
        var name = input.Name.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new PostingException("Item name is required.");
        if (input.SellingPrice < 0) throw new PostingException("Selling price cannot be negative.");
        if (input.CostPrice is < 0) throw new PostingException("Cost price cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new PostingException("Item not found.");

        item.Name = name;
        item.Kind = input.Kind;
        item.Unit = string.IsNullOrWhiteSpace(input.Unit) ? "unit" : input.Unit.Trim();
        item.SellingPrice = input.SellingPrice;
        item.SalesAccountId = input.SalesAccountId;
        item.SalesDescription = string.IsNullOrWhiteSpace(input.SalesDescription) ? null : input.SalesDescription.Trim();
        item.CostPrice = input.CostPrice;
        item.PurchaseAccountId = input.PurchaseAccountId;
        item.PurchaseDescription = string.IsNullOrWhiteSpace(input.PurchaseDescription) ? null : input.PurchaseDescription.Trim();
        item.TaxCodeId = input.TaxCodeId;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task SetActiveAsync(int id, bool isActive)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new PostingException("Item not found.");
        item.IsActive = isActive;
        await JournalPoster.SaveChangesTranslatedAsync(db);
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var item = await db.Items.FirstOrDefaultAsync(i => i.Id == id)
            ?? throw new PostingException("Item not found.");
        db.Items.Remove(item);
        await db.SaveChangesAsync();
    }
}
