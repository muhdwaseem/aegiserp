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

    // Three charges, each net 1000 / vat 50 / gross 1050 — for line-level payment allocation tests.
    private Task<AegisErp.Domain.Entities.PurchaseInvoice> PostMultiLineInvoice(DateOnly? date = null) =>
        _invoices.CreateAndPostAsync(_db.Vendor.Id, "BILL-1", date ?? new(2026, 5, 10), _db.May.Id, null, "tester",
            new[]
            {
                new PurchaseLineInput("Charge1", _db.Expense.Id, null, 1, 1000, 0.05m),
                new PurchaseLineInput("Charge2", _db.Expense.Id, null, 1, 1000, 0.05m),
                new PurchaseLineInput("Charge3", _db.Expense.Id, null, 1, 1000, 0.05m),
            }, Now);

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

    // --- Purchase Invoice Detail View support: GetByIdAsync / GetOutstandingAsync ---

    [Fact]
    public async Task GetByIdAsync_returns_the_invoice_with_full_detail_and_null_for_a_missing_id()
    {
        var inv = await PostInvoice();

        var found = await _invoices.GetByIdAsync(inv.Id);
        Assert.NotNull(found);
        Assert.Equal(inv.InvoiceNo, found!.InvoiceNo);
        Assert.NotNull(found.Vendor);
        Assert.Single(found.Lines);
        Assert.NotNull(found.JournalVoucher);

        Assert.Null(await _invoices.GetByIdAsync(-1));
    }

    [Fact]
    public async Task GetOutstandingAsync_matches_the_payment_services_own_outstanding_calc()
    {
        var inv = await PostInvoice(); // gross 1050
        Assert.Equal(1050m, await _invoices.GetOutstandingAsync(inv.Id));

        await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 400, null, "tester", Now);
        Assert.Equal(650m, await _invoices.GetOutstandingAsync(inv.Id));

        await _debitNotes.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 16), _db.May.Id,
            "Return", null, "tester",
            new[] { new DebitNoteLineInput("Returned goods", _db.Expense.Id, null, 1, 200, 0.05m) }, Now); // gross 210
        Assert.Equal(440m, await _invoices.GetOutstandingAsync(inv.Id)); // 1050 - 400 - 210
    }

    [Fact]
    public async Task Payment_for_a_different_vendors_invoice_is_rejected()
    {
        var inv = await PostInvoice();
        var other = await _vendors.CreateAsync(new NewVendorInput("Other Supplier Co", null, "AED", 30, null, null, null, null));

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            other.Id, inv.Id, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 100, null, "tester", Now));
    }

    // --- Line-level payment allocation (paying for only some charges on a multi-line bill) ---

    [Fact]
    public async Task Payment_against_a_multiline_invoice_without_allocations_is_rejected()
    {
        var inv = await PostMultiLineInvoice();
        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            _db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1050, null, "tester", Now));
    }

    [Fact]
    public async Task Payment_allocations_must_sum_to_the_payment_amount()
    {
        var inv = await PostMultiLineInvoice();
        var line1 = inv.Lines.OrderBy(l => l.LineNo).First();

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            _db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1050, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(line1.Id, 500) })); // only 500 allocated of a 1050 payment
    }

    [Fact]
    public async Task Payment_allocation_exceeding_a_lines_balance_is_rejected()
    {
        var inv = await PostMultiLineInvoice();
        var lines = inv.Lines.OrderBy(l => l.LineNo).ToList(); // each line's Gross is 1050

        // Sums to the payment amount (2000), but line1's own share (1100) exceeds its Gross (1050).
        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            _db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 2000, null, "tester", Now,
            new[]
            {
                new PaymentLineAllocationInput(lines[0].Id, 1100),
                new PaymentLineAllocationInput(lines[1].Id, 900),
            }));
    }

    [Fact]
    public async Task Valid_multiline_payment_allocation_updates_only_the_targeted_lines_balance()
    {
        var inv = await PostMultiLineInvoice();
        var lines = inv.Lines.OrderBy(l => l.LineNo).ToList();

        var p = await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(lines[0].Id, 1050) }); // pay Charge1 in full only

        var balances = await _invoices.GetLineBalancesAsync(inv.Id);
        Assert.Equal(0m, balances.Single(b => b.LineId == lines[0].Id).Balance);
        Assert.Equal(1050m, balances.Single(b => b.LineId == lines[1].Id).Balance); // untouched
        Assert.Equal(1050m, balances.Single(b => b.LineId == lines[2].Id).Balance); // untouched

        // The GL impact is unaffected by which line(s) the payment was attributed to.
        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == p.JournalVoucherId);
        Assert.Equal(1050m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit);
        Assert.Equal(1050m, voucher.Lines.Single(l => l.AccountId == _db.Ap.Id).Debit);
    }

    [Fact]
    public async Task Single_line_invoice_payment_auto_allocates_without_requiring_input()
    {
        var inv = await PostInvoice(); // single line, gross 1050
        await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now); // no allocations passed

        var balances = await _invoices.GetLineBalancesAsync(inv.Id);
        Assert.Equal(0m, balances.Single().Balance);
    }

    [Fact]
    public async Task Invoice_with_one_billable_line_and_a_free_line_auto_allocates_to_the_billable_line()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Vendor.Id, "BILL-1", new(2026, 5, 10), _db.May.Id, null, "tester",
            new[]
            {
                new PurchaseLineInput("Consulting", _db.Expense.Id, null, 1, 1000, 0.05m),
                new PurchaseLineInput("Complimentary extra", _db.Expense.Id, null, 1, 0, 0m),
            }, Now); // two lines, but only one is billable

        await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now); // no allocations — should auto-allocate to the billable line

        var billable = inv.Lines.OrderBy(l => l.LineNo).First();
        var balances = await _invoices.GetLineBalancesAsync(inv.Id);
        Assert.Equal(0m, balances.Single(b => b.LineId == billable.Id).Balance);
    }

    [Fact]
    public async Task On_account_payment_creates_no_line_allocations()
    {
        var p = await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);

        await using var db = _db.CreateDbContext();
        var count = await db.VendorPaymentAllocations.CountAsync(a => a.VendorPaymentId == p.Id);
        Assert.Equal(0, count);
    }

    // --- Multi-invoice payment allocation (one payment settling parts of several different bills) ---

    [Fact]
    public async Task Payment_spanning_two_invoices_settles_both_by_their_own_line_allocations()
    {
        var invA = await PostInvoice(price: 1000); // gross 1050
        var invB = await PostInvoice(price: 500);  // gross 525
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(lineA.Id, 1050), new PaymentLineAllocationInput(lineB.Id, 525) });

        var open = await _vendors.GetOpenInvoicesAsync(_db.Vendor.Id);
        Assert.DoesNotContain(open, o => o.Id == invA.Id); // fully settled, no longer open
        Assert.DoesNotContain(open, o => o.Id == invB.Id);
    }

    [Fact]
    public async Task Multi_invoice_payment_sets_PurchaseInvoiceId_null_when_it_spans_two_distinct_invoices()
    {
        var invA = await PostInvoice(price: 1000);
        var invB = await PostInvoice(price: 500);
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        var p = await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(lineA.Id, 1050), new PaymentLineAllocationInput(lineB.Id, 525) });

        Assert.Null(p.PurchaseInvoiceId);
    }

    [Fact]
    public async Task Multi_invoice_payment_call_that_resolves_to_one_distinct_invoice_still_sets_PurchaseInvoiceId()
    {
        var inv = await PostMultiLineInvoice();
        var lines = inv.Lines.OrderBy(l => l.LineNo).ToList();

        // (purchaseInvoiceId: null, allocations: ...) call shape, but every line belongs to the
        // same invoice — should still resolve and set PurchaseInvoiceId, not leave it null.
        var p = await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 2100, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(lines[0].Id, 1050), new PaymentLineAllocationInput(lines[1].Id, 1050) });

        Assert.Equal(inv.Id, p.PurchaseInvoiceId);
    }

    [Fact]
    public async Task Multi_invoice_allocation_exceeding_one_invoices_outstanding_is_rejected_without_touching_the_other()
    {
        var invA = await PostInvoice(price: 1000); // gross 1050
        var invB = await PostInvoice(price: 500);  // gross 525
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            _db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1600, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(lineA.Id, 1100), new PaymentLineAllocationInput(lineB.Id, 500) })); // 1100 > invA's 1050 outstanding

        // The whole payment must have rolled back — invB is untouched.
        var open = await _vendors.GetOpenInvoicesAsync(_db.Vendor.Id);
        Assert.Equal(525m, open.Single(o => o.Id == invB.Id).Outstanding);
    }

    [Fact]
    public async Task Multi_invoice_allocation_exceeding_one_lines_balance_is_rejected()
    {
        var invA = await PostMultiLineInvoice(); // 3 lines, each gross 1050
        var invB = await PostInvoice(price: 500); // gross 525
        var linesA = invA.Lines.OrderBy(l => l.LineNo).ToList();
        var lineB = invB.Lines.Single();

        // Sums to the payment amount, but linesA[0]'s own share (1100) exceeds its own Gross (1050).
        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            _db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1625, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(linesA[0].Id, 1100), new PaymentLineAllocationInput(lineB.Id, 525) }));
    }

    [Fact]
    public async Task Multi_invoice_allocations_not_summing_to_the_payment_amount_are_rejected()
    {
        var invA = await PostInvoice(price: 1000); // gross 1050
        var invB = await PostInvoice(price: 500);  // gross 525
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await Assert.ThrowsAsync<PostingException>(() => _payments.CreateAndPostAsync(
            _db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(lineA.Id, 1050), new PaymentLineAllocationInput(lineB.Id, 400) })); // 1450 != 1575
    }

    // --- Draft round-trip: CreateDraftAsync / PostDraftAsync / VoidDraftAsync ---

    [Fact]
    public async Task Draft_payment_creates_no_GL_voucher_and_does_not_reduce_outstanding()
    {
        var inv = await PostInvoice(); // gross 1050
        var draft = await _payments.CreateDraftAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now);

        Assert.Equal(VoucherStatus.Draft, draft.Status);
        Assert.Null(draft.JournalVoucherId);
        Assert.Equal(1050m, await _invoices.GetOutstandingAsync(inv.Id)); // untouched until posted
    }

    [Fact]
    public async Task Posting_a_draft_payment_generates_the_GL_voucher_and_reduces_outstanding()
    {
        var inv = await PostInvoice(); // gross 1050
        var draft = await _payments.CreateDraftAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now);

        var posted = await _payments.PostDraftAsync(draft.Id, "poster", Now);

        Assert.Equal(VoucherStatus.Posted, posted.Status);
        Assert.NotNull(posted.JournalVoucherId);
        Assert.Equal(0m, await _invoices.GetOutstandingAsync(inv.Id));
    }

    [Fact]
    public async Task Posting_a_draft_payment_revalidates_the_outstanding_balance_at_post_time()
    {
        var inv = await PostInvoice(); // gross 1050
        var draft = await _payments.CreateDraftAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now);

        // Another payment settles the invoice in full before the draft gets posted.
        await _payments.CreateAndPostAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 16), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _payments.PostDraftAsync(draft.Id, "poster", Now));
    }

    [Fact]
    public async Task Only_draft_payments_can_be_posted_or_voided()
    {
        var p = await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _payments.PostDraftAsync(p.Id, "poster", Now));
        await Assert.ThrowsAsync<PostingException>(() => _payments.VoidDraftAsync(p.Id));
    }

    [Fact]
    public async Task NextPaymentNoAsync_never_collides_between_a_pending_draft_and_a_directly_posted_payment()
    {
        var draft = await _payments.CreateDraftAsync(_db.Vendor.Id, null, new(2026, 5, 10), _db.May.Id,
            _db.Bank.Id, 300, null, "tester", Now);
        var posted = await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 12), _db.May.Id,
            _db.Bank.Id, 300, null, "tester", Now);

        Assert.NotEqual(draft.PaymentNo, posted.PaymentNo);

        var draftPosted = await _payments.PostDraftAsync(draft.Id, "tester", Now);
        Assert.NotEqual(draftPosted.PaymentNo, posted.PaymentNo);
    }

    [Fact]
    public async Task Voiding_a_draft_payment_marks_it_void_without_touching_the_ledger()
    {
        var inv = await PostInvoice();
        var draft = await _payments.CreateDraftAsync(_db.Vendor.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now);

        await _payments.VoidDraftAsync(draft.Id);

        var found = await _payments.GetByIdAsync(draft.Id);
        Assert.Equal(VoucherStatus.Void, found!.Status);
        Assert.Equal(1050m, await _invoices.GetOutstandingAsync(inv.Id)); // never posted, so unaffected
    }

    // --- Payment Mode / Reference No. / Cheque Date / Attachment ---

    [Fact]
    public async Task Payment_reference_no_and_cheque_date_persist_through_create_and_post()
    {
        var p = await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now, null, PaymentMode.Cheque,
            referenceNo: "CHQ-00981", chequeDate: new(2026, 5, 20));

        var found = await _payments.GetByIdAsync(p.Id);
        Assert.Equal("CHQ-00981", found!.ReferenceNo);
        Assert.Equal(new DateOnly(2026, 5, 20), found.ChequeDate);
        Assert.Equal(PaymentMode.Cheque, found.PaymentMode);
    }

    [Fact]
    public async Task Payment_attachment_can_be_set_downloaded_and_removed()
    {
        var p = await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);
        var bytes = new byte[] { 9, 8, 7 };

        await _payments.SetAttachmentAsync(p.Id, "cheque.jpg", "image/jpeg", bytes);
        var att = await _payments.GetAttachmentAsync(p.Id);
        Assert.NotNull(att);
        Assert.Equal("cheque.jpg", att!.Value.FileName);
        Assert.Equal(bytes, att.Value.Data);

        await _payments.RemoveAttachmentAsync(p.Id);
        Assert.Null(await _payments.GetAttachmentAsync(p.Id));
    }

    [Fact]
    public async Task Payment_attachment_over_the_size_limit_is_rejected()
    {
        var p = await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);
        var tooBig = new byte[VendorPaymentService.MaxAttachmentBytes + 1];

        await Assert.ThrowsAsync<PostingException>(() =>
            _payments.SetAttachmentAsync(p.Id, "big.jpg", "image/jpeg", tooBig));
    }

    // --- GetOpenInvoicesAsync / GetLineBalancesAsync reflecting multi-invoice allocations ---

    [Fact]
    public async Task GetOpenInvoicesAsync_reports_outstanding_correctly_after_a_multi_invoice_payment()
    {
        var invA = await PostInvoice(price: 1000); // gross 1050
        var invB = await PostInvoice(price: 500);  // gross 525
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1200, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(lineA.Id, 700), new PaymentLineAllocationInput(lineB.Id, 500) });

        var open = await _vendors.GetOpenInvoicesAsync(_db.Vendor.Id);
        Assert.Equal(350m, open.Single(o => o.Id == invA.Id).Outstanding); // 1050 - 700
        Assert.Equal(25m, open.Single(o => o.Id == invB.Id).Outstanding);  // 525 - 500
    }

    [Fact]
    public async Task GetLineBalancesAsync_reflects_allocations_from_a_multi_invoice_payment()
    {
        var invA = await PostMultiLineInvoice();
        var invB = await PostInvoice(price: 500); // gross 525
        var linesA = invA.Lines.OrderBy(l => l.LineNo).ToList();
        var lineB = invB.Lines.Single();

        await _payments.CreateAndPostAsync(_db.Vendor.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new PaymentLineAllocationInput(linesA[0].Id, 1050), new PaymentLineAllocationInput(lineB.Id, 525) });

        var balancesA = await _invoices.GetLineBalancesAsync(invA.Id);
        Assert.Equal(0m, balancesA.Single(b => b.LineId == linesA[0].Id).Balance);
        Assert.Equal(1050m, balancesA.Single(b => b.LineId == linesA[1].Id).Balance); // untouched
        Assert.Equal(1050m, balancesA.Single(b => b.LineId == linesA[2].Id).Balance); // untouched

        var balancesB = await _invoices.GetLineBalancesAsync(invB.Id);
        Assert.Equal(0m, balancesB.Single().Balance);
    }
}
