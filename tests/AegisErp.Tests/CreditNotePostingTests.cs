using AegisErp.Domain;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

public class CreditNotePostingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SalesInvoiceService _invoices;
    private readonly CreditNoteService _creditNotes;
    private readonly ReceiptService _receipts;
    private readonly CustomerService _customers;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public CreditNotePostingTests()
    {
        _invoices = new SalesInvoiceService(_db);
        _creditNotes = new CreditNoteService(_db);
        _receipts = new ReceiptService(_db);
        _customers = new CustomerService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<AegisErp.Domain.Entities.SalesInvoice> PostInvoice(decimal price = 1000, decimal vat = 0.05m) =>
        _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, price, vat) }, Now);

    [Fact]
    public async Task Credit_note_posts_Dr_revenue_and_VAT_Cr_AR()
    {
        var note = await _creditNotes.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 12), _db.May.Id,
            "Discount", null, "tester",
            new[] { new CreditNoteLineInput("Goodwill credit", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now); // gross 210

        Assert.Equal(VoucherStatus.Posted, note.Status);
        Assert.Equal(note.CreditNoteNo, note.JournalVoucher!.VoucherNo);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines)
            .SingleAsync(v => v.Id == note.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(200m, voucher.Lines.Single(l => l.AccountId == _db.Revenue.Id).Debit); // Dr revenue net
        Assert.Equal(10m, voucher.Lines.Single(l => l.AccountId == _db.Vat.Id).Debit);       // Dr VAT payable
        Assert.Equal(210m, voucher.Lines.Single(l => l.AccountId == _db.Ar.Id).Credit);      // Cr AR gross
    }

    [Fact]
    public async Task Credit_note_reduces_the_invoice_outstanding()
    {
        var inv = await PostInvoice(); // gross 1050
        await _creditNotes.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 12), _db.May.Id,
            "Return", null, "tester",
            new[] { new CreditNoteLineInput("Returned item", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now); // gross 210

        var open = await _customers.GetOpenInvoicesAsync(_db.Customer.Id);
        Assert.Equal(840m, open.Single(o => o.Id == inv.Id).Outstanding); // 1050 - 210
    }

    [Fact]
    public async Task Credit_note_exceeding_the_invoice_outstanding_is_rejected()
    {
        var inv = await PostInvoice(); // gross 1050
        await Assert.ThrowsAsync<PostingException>(() => _creditNotes.CreateAndPostAsync(
            _db.Customer.Id, inv.Id, new(2026, 5, 12), _db.May.Id, "Return", null, "tester",
            new[] { new CreditNoteLineInput("Too much", _db.Revenue.Id, null, 1, 2000, 0.05m) }, Now));
    }

    [Fact]
    public async Task Credit_note_and_receipt_together_cannot_over_apply_an_invoice()
    {
        var inv = await PostInvoice(); // gross 1050
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1000, null, "tester", Now); // 50 left

        // A 210-gross credit note now exceeds the 50 remaining and must be rejected.
        await Assert.ThrowsAsync<PostingException>(() => _creditNotes.CreateAndPostAsync(
            _db.Customer.Id, inv.Id, new(2026, 5, 16), _db.May.Id, "Return", null, "tester",
            new[] { new CreditNoteLineInput("Return", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now));
    }
}
