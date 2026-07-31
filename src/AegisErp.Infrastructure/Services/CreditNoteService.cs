using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Input line for creating a credit note from the UI.</summary>
public record CreditNoteLineInput(string Description, int RevenueAccountId, int? CostCenterId,
    decimal Quantity, decimal UnitPrice, decimal VatRate);

public class CreditNoteService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public CreditNoteService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<CreditNote>> GetRecentAsync(int take = 50)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await db.CreditNotes.AsNoTracking()
            .Include(n => n.Customer).Include(n => n.Lines).Include(n => n.SalesInvoice)
            .Include(n => n.JournalVoucher).Include(n => n.BankAccount)
            .OrderByDescending(n => n.Date).ThenByDescending(n => n.Id)
            .Take(take).ToListAsync();
    }

    /// <summary>
    /// Creates and posts a credit note in one transaction: the note, its lines and the generated GL
    /// voucher are persisted atomically — the mirror image of a sales invoice (Dr revenue net per
    /// line / Dr VAT payable, then either Cr AR — <see cref="CreditNoteSettlementMethod.ApplyToInvoice"/>
    /// or <see cref="CreditNoteSettlementMethod.CreditOnAccount"/> — or Cr the chosen bank account
    /// for <see cref="CreditNoteSettlementMethod.CashRefund"/>). When applied to an invoice, the
    /// total credited against it — across every credit note, regardless of settlement method —
    /// may never exceed that invoice's original total; <see cref="CreditNoteSettlementMethod.ApplyToInvoice"/>
    /// and <see cref="CreditNoteSettlementMethod.CreditOnAccount"/> are additionally capped by
    /// what's still outstanding (unpaid), since only a <see cref="CreditNoteSettlementMethod.CashRefund"/>
    /// pays out of the bank rather than reducing AR.
    /// </summary>
    public async Task<CreditNote> CreateAndPostAsync(
        int customerId, int? salesInvoiceId, DateOnly date, int fiscalPeriodId, string? reason,
        string? narration, string createdBy, IEnumerable<CreditNoteLineInput> lines, DateTime nowUtc,
        CreditNoteSettlementMethod settlementMethod = CreditNoteSettlementMethod.CreditOnAccount,
        int? bankAccountId = null)
    {
        if (settlementMethod == CreditNoteSettlementMethod.ApplyToInvoice && salesInvoiceId is null)
            throw new PostingException("Applying to an invoice requires an invoice to be selected.");
        if (settlementMethod == CreditNoteSettlementMethod.CashRefund && bankAccountId is null)
            throw new PostingException("A cash refund requires a bank/cash account to be selected.");

        await using var db = await _dbf.CreateDbContextAsync();
        await using var tx = await db.Database.BeginTransactionAsync();

        var customer = await db.Customers.FindAsync(customerId)
            ?? throw new PostingException("Customer not found.");

        var note = new CreditNote
        {
            CreditNoteNo = await JournalPoster.NextDocNoAsync(db, "CN", date.Year),
            CustomerId = customerId,
            SalesInvoiceId = salesInvoiceId,
            Date = date,
            FiscalPeriodId = fiscalPeriodId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            Narration = string.IsNullOrWhiteSpace(narration) ? $"Credit note — {customer.Name}" : narration,
            CreatedBy = createdBy,
            CreatedAtUtc = nowUtc,
            SettlementMethod = settlementMethod,
            BankAccountId = settlementMethod == CreditNoteSettlementMethod.CashRefund ? bankAccountId : null,
        };

        var no = 1;
        foreach (var l in lines)
            note.Lines.Add(new CreditNoteLine
            {
                LineNo = no++,
                Description = l.Description,
                RevenueAccountId = l.RevenueAccountId,
                CostCenterId = l.CostCenterId,
                Quantity = l.Quantity,
                UnitPrice = l.UnitPrice,
                VatRate = l.VatRate,
            });

        note.Post(nowUtc); // domain validation

        if (salesInvoiceId is int invId)
        {
            var invoice = await db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
                .FirstOrDefaultAsync(i => i.Id == invId)
                ?? throw new PostingException("Invoice not found.");
            if (invoice.CustomerId != customerId)
                throw new PostingException("Invoice belongs to a different customer.");
            if (invoice.Status != VoucherStatus.Posted)
                throw new PostingException("Only posted invoices can be credited.");

            var priorCredited = (await db.CreditNotes.Include(n => n.Lines)
                .Where(n => n.SalesInvoiceId == invId && n.Status == VoucherStatus.Posted)
                .ToListAsync()).Sum(n => n.TotalGross);

            // Hard ceiling regardless of settlement method: an invoice can never be credited, in
            // total across every credit note against it, for more than it was originally billed.
            if (priorCredited + note.TotalGross > invoice.TotalGross)
                throw new PostingException(
                    $"Credit {note.TotalGross:N2} plus {priorCredited:N2} already credited would exceed " +
                    $"{invoice.InvoiceNo}'s total of {invoice.TotalGross:N2}.");

            // A cash refund pays out of the bank, not off the invoice's AR balance, so it isn't
            // additionally capped by what's still outstanding — a fully-paid invoice can still be
            // refunded, as long as the ceiling above holds.
            if (settlementMethod != CreditNoteSettlementMethod.CashRefund)
            {
                var receipts = (await db.CustomerReceipts
                    .Where(r => r.SalesInvoiceId == invId && r.Status == VoucherStatus.Posted)
                    .Select(r => r.Amount).ToListAsync()).Sum();
                var outstanding = invoice.TotalGross - receipts - priorCredited;
                if (note.TotalGross > outstanding)
                    throw new PostingException(
                        $"Credit {note.TotalGross:N2} exceeds the outstanding {outstanding:N2} on {invoice.InvoiceNo}.");
            }
        }

        // Build the GL voucher: Dr revenue per line, Dr VAT payable for the tax, then credit
        // either AR (reduces what the customer owes) or the chosen bank account (cash paid
        // straight back out), depending on the settlement method.
        var voucherLines = new List<VoucherLineInput>();
        voucherLines.AddRange(note.Lines.Where(l => l.Net > 0).Select(l =>
            new VoucherLineInput(l.RevenueAccountId, l.CostCenterId, l.Description, l.Net, 0)));
        if (note.TotalVat > 0)
        {
            var vat = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.VatPayable);
            voucherLines.Add(new VoucherLineInput(vat.Id, null, $"VAT reversal — {note.CreditNoteNo}", note.TotalVat, 0));
        }

        if (settlementMethod == CreditNoteSettlementMethod.CashRefund)
        {
            var bank = await db.Accounts.FindAsync(bankAccountId)
                ?? throw new PostingException("Bank/cash account not found.");
            if (bank.Type != AccountType.Asset)
                throw new PostingException($"{bank.Code} — {bank.Name} is not a bank/cash account.");
            voucherLines.Add(new VoucherLineInput(bank.Id, null, $"Cash refund — {customer.Name} — {note.CreditNoteNo}", 0, note.TotalGross));
        }
        else
        {
            var ar = await JournalPoster.RequireAccountAsync(db, WellKnownAccounts.AccountsReceivable);
            voucherLines.Add(new VoucherLineInput(ar.Id, null, $"{customer.Name} — {note.CreditNoteNo}", 0, note.TotalGross));
        }

        note.JournalVoucher = await JournalPoster.PostAsync(
            db, VoucherType.CreditNote, note.CreditNoteNo, date, fiscalPeriodId,
            note.Narration, note.CreditNoteNo, createdBy, voucherLines, nowUtc);

        db.CreditNotes.Add(note);
        await JournalPoster.SaveAndCommitAsync(db, tx);
        return note;
    }
}
