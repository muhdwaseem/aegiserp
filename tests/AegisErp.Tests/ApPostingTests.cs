using AegisErp.Domain;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

public class ApPostingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly PurchaseInvoiceService _invoices;
    private readonly VendorPaymentService _payments;
    private readonly DebitNoteService _debitNotes;
    private readonly VendorService _vendors;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public ApPostingTests()
    {
        _invoices = new PurchaseInvoiceService(_db);
        _payments = new VendorPaymentService(_db);
        _debitNotes = new DebitNoteService(_db);
        _vendors = new VendorService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<AegisErp.Domain.Entities.PurchaseInvoice> PostInvoice(
        decimal qty = 1, decimal price = 1000, decimal vat = 0.05m, DateOnly? date = null) =>
        _invoices.CreateAndPostAsync(_db.Vendor.Id, "BILL-1", date ?? new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new PurchaseLineInput("Service", _db.Expense.Id, null, qty, price, vat) }, Now);

    [Fact]
    public async Task Posting_a_purchase_invoice_generates_a_balanced_GL_voucher()
    {
        var inv = await PostInvoice(qty: 2, price: 500, vat: 0.05m); // net 1000, vat 50, gross 1050

        Assert.Equal(VoucherStatus.Posted, inv.Status);
        Assert.Equal(1000m, inv.TotalNet);
        Assert.Equal(50m, inv.TotalVat);
        Assert.Equal(1050m, inv.TotalGross);
        Assert.NotNull(inv.JournalVoucher);
        Assert.Equal(inv.InvoiceNo, inv.JournalVoucher!.VoucherNo); // shared document number

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines)
            .SingleAsync(v => v.Id == inv.JournalVoucherId);

        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(1000m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Debit);   // Dr expense net
        Assert.Equal(50m, voucher.Lines.Single(l => l.AccountId == _db.VatInput.Id).Debit);     // Dr VAT input
        Assert.Equal(1050m, voucher.Lines.Single(l => l.AccountId == _db.Ap.Id).Credit);        // Cr AP gross
    }

    [Fact]
    public async Task Zero_rated_purchase_invoice_has_no_VAT_line()
    {
        var inv = await PostInvoice(vat: 0m);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines)
            .SingleAsync(v => v.Id == inv.JournalVoucherId);
        Assert.DoesNotContain(voucher.Lines, l => l.AccountId == _db.VatInput.Id);
    }

    [Fact]
    public async Task Purchase_invoice_with_no_lines_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _invoices.CreateAndPostAsync(
            _db.Vendor.Id, null, new(2026, 5, 10), _db.May.Id, null, "tester",
            Array.Empty<PurchaseLineInput>(), Now));
    }

    [Fact]
    public async Task Allocated_payment_reduces_the_invoice_outstanding()
    {
        var inv = await PostInvoice(); // gross 1050
        await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 400, null, "tester", Now);

        var open = await _vendors.GetOpenInvoicesAsync(_db.Vendor.Id);
        Assert.Equal(650m, open.Single(o => o.Id == inv.Id).Outstanding);
    }

    [Fact]
    public async Task Payment_exceeding_the_outstanding_is_rejected()
    {
        var inv = await PostInvoice(); // gross 1050
        await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1000, null, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            _db.Vendor.Id, inv.Id, new(2026, 5, 20), _db.May.Id, _db.Bank.Id, 100, null, "tester", Now));
    }

    [Fact]
    public async Task Payment_generates_Dr_AP_Cr_bank()
    {
        var p = await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines)
            .SingleAsync(v => v.Id == p.JournalVoucherId);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Ap.Id).Debit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit);
    }

    [Fact]
    public async Task Debit_note_posts_Dr_AP_Cr_expense_and_reduces_outstanding()
    {
        var inv = await PostInvoice(); // gross 1050
        var note = await _debitNotes.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 16), _db.May.Id,
            "Return", null, "tester",
            new[] { new DebitNoteLineInput("Returned goods", _db.Expense.Id, null, 1, 200, 0.05m) }, Now); // gross 210

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines)
            .SingleAsync(v => v.Id == note.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(210m, voucher.Lines.Single(l => l.AccountId == _db.Ap.Id).Debit);       // Dr AP gross
        Assert.Equal(200m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Credit);  // Cr expense net
        Assert.Equal(10m, voucher.Lines.Single(l => l.AccountId == _db.VatInput.Id).Credit);  // Cr VAT input

        var open = await _vendors.GetOpenInvoicesAsync(_db.Vendor.Id);
        Assert.Equal(840m, open.Single(o => o.Id == inv.Id).Outstanding); // 1050 - 210
    }

    [Fact]
    public async Task Books_stay_balanced_after_a_full_purchase_and_payment_cycle()
    {
        var inv = await PostInvoice();
        await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now);

        var ledger = new LedgerService(_db);
        var tb = await ledger.GetTrialBalanceAsync(_db.May.Id);
        Assert.True(tb.IsBalanced);

        var open = await _vendors.GetOpenInvoicesAsync(_db.Vendor.Id);
        Assert.Empty(open);
    }

    [Fact]
    public async Task Ap_aging_buckets_open_invoices_by_days_past_due()
    {
        // Terms 30 days. Due 2026-06-09.
        await PostInvoice(date: new(2026, 5, 10)); // gross 1050

        var notYetDue = await _vendors.GetAgingAsync(new(2026, 6, 1));
        Assert.Equal(1050m, notYetDue.Single().Current);

        var overdue45 = await _vendors.GetAgingAsync(new(2026, 7, 24)); // 45 days past due
        Assert.Equal(1050m, overdue45.Single().Days31To60);
    }

    [Fact]
    public async Task Vendor_statement_shows_documents_with_running_balance()
    {
        var inv = await PostInvoice(); // gross 1050 on 2026-05-10
        await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 400, null, "tester", Now);

        var stmt = await _vendors.GetStatementAsync(_db.Vendor.Id);
        Assert.Equal(2, stmt.Count);
        Assert.Equal(1050m, stmt[0].RunningBalance); // we owe 1050
        Assert.Equal(650m, stmt[1].RunningBalance);  // after paying 400
    }
}
