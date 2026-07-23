using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a debit note from the UI.</summary>
public record DebitNoteLineInput(string Description, int ExpenseAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate);

public class DebitNoteService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public DebitNoteService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<DebitNote>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.DebitNotes.AsNoTracking()
            .Include(n => n.Vendor).Include(n => n.Lines).Include(n => n.PurchaseInvoice).Include(n => n.JournalVoucher)
            .OrderByDescending(n => n.Date).ThenByDescending(n => n.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>
    /// Creates and posts a debit note in one transaction: the note, its lines and the generated GL
    /// voucher (Dr AP gross / Cr expense net per line / Cr VAT input) are persisted atomically — the
    /// mirror image of a purchase invoice. When applied to a purchase invoice, the debit may not
    /// exceed that invoice's outstanding balance.
    /// </summary>
    public async Task<DebitNote> CreateAndPostAsync(
        int vendorId, int? purchaseInvoiceId, DateOnly date, int fiscalPeriodId, string? reason,
        string? narration, string createdBy, IEnumerable<DebitNoteLineInput> lines, DateTime nowUtc)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var vendor = await db.Vendors.FindAsync(vendorId)
            ?? throw new PostingException("Vendor not found.");

        var note = new DebitNote
        {
            DebitNoteNo = await JournalPoster.NextDocNoAsync(db, "DN", date.Year),
            VendorId = vendorId,
            PurchaseInvoiceId = purchaseInvoiceId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Debit note — {vendor.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
        };

        var no = 1;
        foreach (var l in lines)
            note.Lines.Add(new DebitNoteLine
            {
                LineNo = no++,
                Description = l.Description,
                ExpenseAccountId = l.ExpenseAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
            });

        note.Post(nowUtc); // domain validation

        if (purchaseInvoiceId is int invId)
        {
            var invoice = await db.PurchaseInvoices.AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == invId)
                ?? throw new PostingException("Purchase invoice not found.");
            if (invoice.VendorId != vendorId)
                throw new PostingException("Purchase invoice belongs to a different vendor.");
            if (invoice.Status != VoucherStatus.Posted)
                throw new PostingException("Only posted purchase invoices can be debited.");

            var paid = (await db.VendorPayments
                .Where(p => p.PurchaseInvoiceId == invId && p.Status == VoucherStatus.Posted)
                .Select(p => p.Amount).ToListAsync()).Sum();
            var debited = (await db.DebitNotes.Include(d => d.Lines)
                .Where(d => d.PurchaseInvoiceId == invId && d.Status == VoucherStatus.Posted)
                .ToListAsync()).Sum(d => d.TotalGross);
            var outstanding = invoice.TotalGross - paid - debited;
            if (note.TotalGross > outstanding)
                throw new PostingException(
                    $"Debit {note.TotalGross:N2} exceeds the outstanding {outstanding:N2} on {invoice.InvoiceNo}.");
        }

        // Build the GL voucher: Dr AP for the gross, Cr expense per line, Cr VAT input for the tax.
        var ap = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsPayable);
        var voucherLines = new List<VoucherLineInput>
        {
            new(ap.Id, null, $"{vendor.Name} — {note.DebitNoteNo}", note.TotalGross, 0),
        };
        voucherLines.AddRange(note.Lines.Where(l => l.Net > 0).Select(l =>
            new VoucherLineInput(l.ExpenseAccountId, l.CostCenterId, l.Description, 0, l.Net)));
        if (note.TotalVat > 0)
        {
            var vat = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatInput);
            voucherLines.Add(new VoucherLineInput(vat.Id, null, $"Input VAT reversal — {note.DebitNoteNo}", 0, note.TotalVat));
        }

        note.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.DebitNote, note.DebitNoteNo, date, fiscalPeriodId,
            note.Narration, note.DebitNoteNo, createdBy, voucherLines, nowUtc);

        db.DebitNotes.Add(note);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return note;
    }
}
