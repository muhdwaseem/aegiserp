using AegisErp.Domain;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
        _invoices = new SalesInvoiceService(_db, new EmailService(Options.Create(new SmtpOptions())));
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

    [Fact]
    public async Task Cash_refund_credits_the_bank_account_instead_of_AR()
    {
        var note = await _creditNotes.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 12), _db.May.Id,
            "Refund", null, "tester",
            new[] { new CreditNoteLineInput("Refund", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now, // gross 210
            CreditNoteSettlementMethod.CashRefund, _db.Bank.Id);

        Assert.Equal(CreditNoteSettlementMethod.CashRefund, note.SettlementMethod);
        Assert.Equal(_db.Bank.Id, note.BankAccountId);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == note.JournalVoucherId);
        Assert.Equal(210m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit); // Cr Bank, not AR
        Assert.DoesNotContain(voucher.Lines, l => l.AccountId == _db.Ar.Id);
    }

    [Fact]
    public async Task Cash_refund_can_exceed_a_fully_paid_invoices_outstanding_balance()
    {
        var inv = await PostInvoice(); // gross 1050
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now); // fully paid, 0 outstanding

        // A cash refund pays out of the bank, not off AR, so it isn't capped by the (now zero)
        // outstanding balance — this must succeed rather than throw.
        var note = await _creditNotes.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 16), _db.May.Id,
            "Return", null, "tester",
            new[] { new CreditNoteLineInput("Return", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now,
            CreditNoteSettlementMethod.CashRefund, _db.Bank.Id);

        Assert.Equal(VoucherStatus.Posted, note.Status);
    }

    [Fact]
    public async Task Cash_refund_still_cannot_exceed_the_invoices_original_total()
    {
        // Regression guard: a cash refund isn't capped by "outstanding" (since it doesn't touch
        // AR), but it must still never let an invoice be credited, in total, for more than it was
        // originally billed — otherwise a refund tied to a real invoice could drain an arbitrary
        // amount from the bank with only a cosmetic link to that invoice.
        var inv = await PostInvoice(); // gross 1050
        await Assert.ThrowsAsync<PostingException>(() => _creditNotes.CreateAndPostAsync(
            _db.Customer.Id, inv.Id, new(2026, 5, 16), _db.May.Id, "Return", null, "tester",
            new[] { new CreditNoteLineInput("Way too much", _db.Revenue.Id, null, 1, 20000, 0.05m) }, Now,
            CreditNoteSettlementMethod.CashRefund, _db.Bank.Id));
    }

    [Fact]
    public async Task Cash_refund_to_a_non_bank_account_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _creditNotes.CreateAndPostAsync(
            _db.Customer.Id, null, new(2026, 5, 12), _db.May.Id, "Refund", null, "tester",
            new[] { new CreditNoteLineInput("Refund", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now,
            CreditNoteSettlementMethod.CashRefund, _db.Revenue.Id)); // Revenue is Income, not Asset
    }

    [Fact]
    public async Task Cash_refund_without_a_bank_account_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _creditNotes.CreateAndPostAsync(
            _db.Customer.Id, null, new(2026, 5, 12), _db.May.Id, "Refund", null, "tester",
            new[] { new CreditNoteLineInput("Refund", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now,
            CreditNoteSettlementMethod.CashRefund));
    }

    [Fact]
    public async Task Apply_to_invoice_without_an_invoice_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _creditNotes.CreateAndPostAsync(
            _db.Customer.Id, null, new(2026, 5, 12), _db.May.Id, "Return", null, "tester",
            new[] { new CreditNoteLineInput("Return", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now,
            CreditNoteSettlementMethod.ApplyToInvoice));
    }

    // ── Apply Credit (post-hoc allocation of an on-account credit note) ──

    [Fact]
    public async Task An_on_account_credit_note_shows_up_as_available_credit()
    {
        await _creditNotes.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 12), _db.May.Id,
            "Goodwill", null, "tester",
            new[] { new CreditNoteLineInput("Goodwill credit", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now); // gross 210

        var available = await _creditNotes.GetAvailableCreditAsync(_db.Customer.Id);

        var row = Assert.Single(available);
        Assert.Equal(210m, row.TotalGross);
        Assert.Equal(0m, row.Allocated);
        Assert.Equal(210m, row.Available);
    }

    [Fact]
    public async Task Applying_credit_to_an_invoice_reduces_its_outstanding_and_the_credits_available()
    {
        var inv = await PostInvoice(); // gross 1050
        var note = await _creditNotes.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 12), _db.May.Id,
            "Goodwill", null, "tester",
            new[] { new CreditNoteLineInput("Goodwill credit", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now); // gross 210

        await _creditNotes.ApplyToInvoiceAsync(inv.Id, new[] { (note.Id, 210m) }, "tester", Now);

        var open = await _customers.GetOpenInvoicesAsync(_db.Customer.Id);
        Assert.Equal(840m, open.Single(o => o.Id == inv.Id).Outstanding); // 1050 - 210

        var available = await _creditNotes.GetAvailableCreditAsync(_db.Customer.Id);
        Assert.Empty(available); // fully applied, nothing left
    }

    [Fact]
    public async Task A_credit_note_can_be_split_across_two_invoices()
    {
        var inv1 = await PostInvoice(price: 100, vat: 0.05m); // gross 105
        var inv2 = await PostInvoice(price: 100, vat: 0.05m); // gross 105
        var note = await _creditNotes.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 12), _db.May.Id,
            "Goodwill", null, "tester",
            new[] { new CreditNoteLineInput("Goodwill credit", _db.Revenue.Id, null, 1, 250, 0) }, Now); // gross 250

        await _creditNotes.ApplyToInvoiceAsync(inv1.Id, new[] { (note.Id, 100m) }, "tester", Now);
        await _creditNotes.ApplyToInvoiceAsync(inv2.Id, new[] { (note.Id, 100m) }, "tester", Now);

        var open = await _customers.GetOpenInvoicesAsync(_db.Customer.Id);
        Assert.Equal(5m, open.Single(o => o.Id == inv1.Id).Outstanding);
        Assert.Equal(5m, open.Single(o => o.Id == inv2.Id).Outstanding);

        var available = await _creditNotes.GetAvailableCreditAsync(_db.Customer.Id);
        var remaining = Assert.Single(available);
        Assert.Equal(50m, remaining.Available); // 250 - 100 - 100
    }

    [Fact]
    public async Task Applying_more_than_a_credit_notes_available_balance_is_rejected()
    {
        var inv = await PostInvoice(price: 1000, vat: 0); // gross 1000
        var note = await _creditNotes.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 12), _db.May.Id,
            "Goodwill", null, "tester",
            new[] { new CreditNoteLineInput("Goodwill credit", _db.Revenue.Id, null, 1, 200, 0) }, Now); // gross 200

        await Assert.ThrowsAsync<PostingException>(() =>
            _creditNotes.ApplyToInvoiceAsync(inv.Id, new[] { (note.Id, 250m) }, "tester", Now));
    }

    [Fact]
    public async Task Applying_credit_beyond_the_invoices_outstanding_is_rejected()
    {
        var inv = await PostInvoice(price: 100, vat: 0); // gross 100
        var note = await _creditNotes.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 12), _db.May.Id,
            "Goodwill", null, "tester",
            new[] { new CreditNoteLineInput("Goodwill credit", _db.Revenue.Id, null, 1, 200, 0) }, Now); // gross 200

        // Available (200) covers it, but the invoice itself only owes 100.
        await Assert.ThrowsAsync<PostingException>(() =>
            _creditNotes.ApplyToInvoiceAsync(inv.Id, new[] { (note.Id, 150m) }, "tester", Now));
    }

    [Fact]
    public async Task A_credit_note_already_tied_to_an_invoice_at_creation_is_not_offered_as_available_credit()
    {
        var inv = await PostInvoice(); // gross 1050
        await _creditNotes.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 12), _db.May.Id,
            "Return", null, "tester",
            new[] { new CreditNoteLineInput("Returned item", _db.Revenue.Id, null, 1, 200, 0.05m) }, Now,
            CreditNoteSettlementMethod.ApplyToInvoice); // gross 210, tied to inv at creation

        var available = await _creditNotes.GetAvailableCreditAsync(_db.Customer.Id);
        Assert.Empty(available);
    }
}
