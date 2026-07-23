using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating an estimate / quotation from the UI.</summary>
public record EstimateLineInput(string Description, int RevenueAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate);

public class EstimateService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    private readonly SalesInvoiceService _invoices;
    public EstimateService(IDbContextFactory<AegisDbContext> dbf, SalesInvoiceService invoices)
    {
        _dbf = dbf;
        _invoices = invoices;
    }

    public async Task<List<Estimate>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.Estimates.AsNoTracking()
            .Include(e => e.Customer).Include(e => e.Lines).Include(e => e.ConvertedInvoice)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>Creates a quotation (non-posting). Validity defaults to 30 days if not given.</summary>
    public async Task<Estimate> CreateAsync(
        int customerId, DateOnly date, DateOnly validUntil, string? narration,
        string createdBy, IEnumerable<EstimateLineInput> lines, DateTime nowUtc)
    {
        if (customerId == 0) throw new PostingException("Estimate has no customer.");
        var lineList = lines.ToList();
        if (lineList.Count == 0) throw new PostingException("Estimate needs at least one line.");
        if (validUntil < date) throw new PostingException("Valid-until date cannot be before the estimate date.");

        await using var db = await _dbf.CreateDbContextAsync();
        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        var numbers = await db.Estimates.Select(e => e.EstimateNo).ToListAsync();

        var estimate = new Estimate
        {
            EstimateNo = JournalPoster.NextDocNo(numbers, "EST", date.Year),
            CustomerId = customerId,
            Date = date,
            ValidUntil = validUntil,
            Status = DocumentStatus.Draft,
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Quotation — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in lineList)
        {
            if (l.RevenueAccountId == 0) throw new PostingException($"Line {no} has no revenue account.");
            if (l.Quantity <= 0) throw new PostingException($"Line {no} quantity must be positive.");
            estimate.Lines.Add(new EstimateLine
            {
                LineNo = no++,
                Description = l.Description,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
            });
        }

        db.Estimates.Add(estimate);
        await JournalPoster.SaveChangesTranslatedAsync(db);
        return estimate;
    }

    /// <summary>Moves an estimate through its non-posting workflow (Draft → Sent → Accepted / Declined).</summary>
    public async Task SetStatusAsync(int estimateId, DocumentStatus status)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var estimate = await db.Estimates.FirstOrDefaultAsync(e => e.Id == estimateId)
            ?? throw new PostingException("Estimate not found.");
        if (estimate.Status == DocumentStatus.Converted)
            throw new PostingException("A converted estimate can no longer change status.");
        estimate.Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Converts an accepted estimate into a posted sales invoice (which is what hits the GL), then
    /// marks the estimate Converted and links it to the new invoice.
    /// </summary>
    public async Task<SalesInvoice> ConvertToInvoiceAsync(int estimateId, int fiscalPeriodId, string createdBy, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var estimate = await db.Estimates.Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == estimateId)
            ?? throw new PostingException("Estimate not found.");
        if (estimate.ConvertedInvoiceId is not null)
            throw new PostingException("This estimate has already been converted.");

        var inputs = estimate.Lines.OrderBy(l => l.LineNo).Select(l =>
            new InvoiceLineInput(l.Description, l.RevenueAccountId, l.CostCenterId, l.Quantity, l.UnitPrice, l.VatRate));

        var invoice = await _invoices.CreateAndPostAsync(
            estimate.CustomerId, DateOnly.FromDateTime(nowUtc.Date), fiscalPeriodId,
            $"Converted from {estimate.EstimateNo}", createdBy, inputs, nowUtc);

        estimate.Status = DocumentStatus.Converted;
        estimate.ConvertedInvoiceId = invoice.Id;
        await db.SaveChangesAsync();
        return invoice;
    }
}
