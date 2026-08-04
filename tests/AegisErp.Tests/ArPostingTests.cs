using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AegisErp.Tests;

public class ArPostingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SalesInvoiceService _invoices;
    private readonly ReceiptService _receipts;
    private readonly CustomerService _customers;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public ArPostingTests()
    {
        _invoices = new SalesInvoiceService(_db, new EmailService(Options.Create(new SmtpOptions())));
        _receipts = new ReceiptService(_db);
        _customers = new CustomerService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<AegisErp.Domain.Entities.SalesInvoice> PostInvoice(
        decimal qty = 1, decimal price = 1000, decimal vat = 0.05m, DateOnly? date = null) =>
        _invoices.CreateAndPostAsync(_db.Customer.Id, date ?? new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, qty, price, vat) }, Now);

    // Three services, each net 1000 / vat 50 / gross 1050 — for line-level receipt allocation tests.
    private Task<AegisErp.Domain.Entities.SalesInvoice> PostMultiLineInvoice(DateOnly? date = null) =>
        _invoices.CreateAndPostAsync(_db.Customer.Id, date ?? new(2026, 5, 10), _db.May.Id, null, "tester",
            new[]
            {
                new InvoiceLineInput("Service1", _db.Revenue.Id, null, 1, 1000, 0.05m),
                new InvoiceLineInput("Service2", _db.Revenue.Id, null, 1, 1000, 0.05m),
                new InvoiceLineInput("Service3", _db.Revenue.Id, null, 1, 1000, 0.05m),
            }, Now);

    [Fact]
    public async Task Posting_an_invoice_generates_a_balanced_GL_voucher()
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
        Assert.Equal(1050m, voucher.Lines.Single(l => l.AccountId == _db.Ar.Id).Debit);       // Dr AR gross
        Assert.Equal(1000m, voucher.Lines.Single(l => l.AccountId == _db.Revenue.Id).Credit); // Cr revenue net
        Assert.Equal(50m, voucher.Lines.Single(l => l.AccountId == _db.Vat.Id).Credit);       // Cr VAT
    }

    [Fact]
    public async Task A_deferred_line_credits_deferred_revenue_instead_of_the_revenue_account()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("12-month plan", _db.Revenue.Id, null, 1, 1200, 0.05m,
                Recognition: RevenueRecognition.Deferred) }, Now);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == inv.JournalVoucherId);

        Assert.Equal(1200m, voucher.Lines.Single(l => l.AccountId == _db.DeferredRevenue.Id).Credit); // Cr deferred, not revenue
        Assert.DoesNotContain(voucher.Lines, l => l.AccountId == _db.Revenue.Id);
    }

    [Fact]
    public async Task Direct_and_deferred_lines_on_the_same_invoice_post_to_their_own_accounts()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[]
            {
                new InvoiceLineInput("One-time setup", _db.Revenue.Id, null, 1, 500, 0.05m), // Direct (default)
                new InvoiceLineInput("12-month plan", _db.Revenue.Id, null, 1, 1200, 0.05m,
                    Recognition: RevenueRecognition.Deferred),
            }, Now);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == inv.JournalVoucherId);

        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Revenue.Id).Credit);
        Assert.Equal(1200m, voucher.Lines.Single(l => l.AccountId == _db.DeferredRevenue.Id).Credit);
    }

    [Fact]
    public async Task Zero_rated_invoice_has_no_VAT_line()
    {
        var inv = await PostInvoice(vat: 0m);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines)
            .SingleAsync(v => v.Id == inv.JournalVoucherId);
        Assert.DoesNotContain(voucher.Lines, l => l.AccountId == _db.Vat.Id);
    }

    [Fact]
    public async Task Invoice_with_a_free_zero_amount_line_posts_and_omits_it_from_the_GL_voucher()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[]
            {
                new InvoiceLineInput("Visa processing", _db.Revenue.Id, null, 1, 1000, 0.05m),
                new InvoiceLineInput("Complimentary courier", _db.Revenue.Id, null, 1, 0, 0m),
            }, Now);

        Assert.Equal(VoucherStatus.Posted, inv.Status);
        Assert.Equal(2, inv.Lines.Count); // invoice keeps the free line

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines)
            .SingleAsync(v => v.Id == inv.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit); // still balanced
        Assert.DoesNotContain(voucher.Lines, l => l.Debit == 0 && l.Credit == 0); // no zero GL line
    }

    [Fact]
    public async Task Invoice_with_no_lines_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _invoices.CreateAndPostAsync(
            _db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            Array.Empty<InvoiceLineInput>(), Now));
    }

    [Fact]
    public async Task Allocated_receipt_reduces_the_invoice_outstanding()
    {
        var inv = await PostInvoice(); // gross 1050
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 400, null, "tester", Now);

        var open = await _customers.GetOpenInvoicesAsync(_db.Customer.Id);
        Assert.Equal(650m, open.Single(o => o.Id == inv.Id).Outstanding);
    }

    [Fact]
    public async Task Receipt_exceeding_the_outstanding_is_rejected()
    {
        var inv = await PostInvoice(); // gross 1050
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1000, null, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, inv.Id, new(2026, 5, 20), _db.May.Id, _db.Bank.Id, 100, null, "tester", Now));
    }

    [Fact]
    public async Task Receipt_generates_Dr_bank_Cr_AR()
    {
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines)
            .SingleAsync(v => v.Id == r.JournalVoucherId);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Debit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Ar.Id).Credit);
    }

    [Fact]
    public async Task Books_stay_balanced_after_a_full_invoice_and_receipt_cycle()
    {
        var inv = await PostInvoice();
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now);

        var ledger = new LedgerService(_db);
        var tb = await ledger.GetTrialBalanceAsync(_db.May.Id);
        Assert.True(tb.IsBalanced);

        // Fully settled invoice no longer appears as open.
        var open = await _customers.GetOpenInvoicesAsync(_db.Customer.Id);
        Assert.Empty(open);
    }

    [Fact]
    public async Task GetTrialBalanceAsync_by_date_matches_the_period_end_result()
    {
        var inv = await PostInvoice();
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now);

        var ledger = new LedgerService(_db);
        var byPeriod = await ledger.GetTrialBalanceAsync(_db.May.Id);
        var byDate = await ledger.GetTrialBalanceAsync(_db.May.EndDate);

        Assert.True(byDate.IsBalanced);
        Assert.Equal(byPeriod.TotalDebit, byDate.TotalDebit);
        Assert.Equal(byPeriod.TotalCredit, byDate.TotalCredit);
        Assert.Equal(byPeriod.Rows.Count, byDate.Rows.Count);

        // A cut-off before any postings existed has nothing to report.
        var beforeAnyPostings = await ledger.GetTrialBalanceAsync(new DateOnly(2026, 5, 1).AddDays(-1));
        Assert.Empty(beforeAnyPostings.Rows);
    }

    [Fact]
    public async Task Aging_buckets_open_invoices_by_days_past_due()
    {
        // Terms 30 days. Due 2026-06-09.
        await PostInvoice(date: new(2026, 5, 10)); // gross 1050

        var notYetDue = await _customers.GetAgingAsync(new(2026, 6, 1));
        Assert.Equal(1050m, notYetDue.Single().Current);

        var overdue45 = await _customers.GetAgingAsync(new(2026, 7, 24)); // 45 days past due
        Assert.Equal(1050m, overdue45.Single().Days31To60);

        var overdue100 = await _customers.GetAgingAsync(new(2026, 9, 17)); // 100 days past due
        Assert.Equal(1050m, overdue100.Single().Over90);
    }

    [Fact]
    public async Task Customer_statement_shows_documents_with_running_balance()
    {
        var inv = await PostInvoice(); // gross 1050 on 2026-05-10
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 400, null, "tester", Now);

        var stmt = await _customers.GetStatementAsync(_db.Customer.Id);
        Assert.Equal(2, stmt.Count);
        Assert.Equal(1050m, stmt[0].RunningBalance);
        Assert.Equal(650m, stmt[1].RunningBalance);
    }

    [Fact]
    public async Task Receipt_for_a_different_customers_invoice_is_rejected()
    {
        var inv = await PostInvoice();
        var other = await _customers.CreateAsync(new NewCustomerInput("Other Co", null, "AED", 0, 30, null, null, null, null));

        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            other.Id, inv.Id, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 100, null, "tester", Now));
    }

    // --- Line-level receipt allocation (paying for only some services on a multi-line invoice) ---

    [Fact]
    public async Task Receipt_against_a_multiline_invoice_without_allocations_is_rejected()
    {
        var inv = await PostMultiLineInvoice();
        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1050, null, "tester", Now));
    }

    [Fact]
    public async Task Receipt_allocations_must_sum_to_the_receipt_amount()
    {
        var inv = await PostMultiLineInvoice();
        var line1 = inv.Lines.OrderBy(l => l.LineNo).First();

        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1050, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(line1.Id, 500) })); // only 500 allocated of a 1050 receipt
    }

    [Fact]
    public async Task Receipt_allocation_exceeding_a_lines_balance_is_rejected()
    {
        var inv = await PostMultiLineInvoice();
        var lines = inv.Lines.OrderBy(l => l.LineNo).ToList(); // each line's Gross is 1050

        // Sums to the receipt amount (2000), but line1's own share (1100) exceeds its Gross (1050).
        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 2000, null, "tester", Now,
            new[]
            {
                new ReceiptLineAllocationInput(lines[0].Id, 1100),
                new ReceiptLineAllocationInput(lines[1].Id, 900),
            }));
    }

    [Fact]
    public async Task Valid_multiline_receipt_allocation_updates_only_the_targeted_lines_balance()
    {
        var inv = await PostMultiLineInvoice();
        var lines = inv.Lines.OrderBy(l => l.LineNo).ToList();

        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lines[0].Id, 1050) }); // pay Service1 in full only

        var balances = await _invoices.GetLineBalancesAsync(inv.Id);
        Assert.Equal(0m, balances.Single(b => b.LineId == lines[0].Id).Balance);
        Assert.Equal(1050m, balances.Single(b => b.LineId == lines[1].Id).Balance); // untouched
        Assert.Equal(1050m, balances.Single(b => b.LineId == lines[2].Id).Balance); // untouched

        // The GL impact is unaffected by which line(s) the payment was attributed to.
        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == r.JournalVoucherId);
        Assert.Equal(1050m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Debit);
        Assert.Equal(1050m, voucher.Lines.Single(l => l.AccountId == _db.Ar.Id).Credit);
    }

    [Fact]
    public async Task Single_line_invoice_receipt_auto_allocates_without_requiring_input()
    {
        var inv = await PostInvoice(); // single line, gross 1050
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now); // no allocations passed

        var balances = await _invoices.GetLineBalancesAsync(inv.Id);
        Assert.Equal(0m, balances.Single().Balance);
    }

    [Fact]
    public async Task Invoice_with_one_billable_line_and_a_free_line_auto_allocates_to_the_billable_line()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[]
            {
                new InvoiceLineInput("Visa processing", _db.Revenue.Id, null, 1, 1000, 0.05m),
                new InvoiceLineInput("Complimentary courier", _db.Revenue.Id, null, 1, 0, 0m),
            }, Now); // two lines, but only one is billable

        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now); // no allocations — should auto-allocate to the billable line

        var billable = inv.Lines.OrderBy(l => l.LineNo).First();
        var balances = await _invoices.GetLineBalancesAsync(inv.Id);
        Assert.Equal(0m, balances.Single(b => b.LineId == billable.Id).Balance);
    }

    [Fact]
    public async Task On_account_receipt_creates_no_line_allocations()
    {
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);

        await using var db = _db.CreateDbContext();
        var count = await db.ReceiptAllocations.CountAsync(a => a.CustomerReceiptId == r.Id);
        Assert.Equal(0, count);
    }

    // --- Multi-invoice receipt allocation (one payment settling parts of several different invoices) ---

    [Fact]
    public async Task Receipt_spanning_two_invoices_settles_both_by_their_own_line_allocations()
    {
        var invA = await PostInvoice(price: 1000); // gross 1050
        var invB = await PostInvoice(price: 500);  // gross 525
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineA.Id, 1050), new ReceiptLineAllocationInput(lineB.Id, 525) });

        var open = await _customers.GetOpenInvoicesAsync(_db.Customer.Id);
        Assert.DoesNotContain(open, o => o.Id == invA.Id); // fully settled, no longer open
        Assert.DoesNotContain(open, o => o.Id == invB.Id);
    }

    [Fact]
    public async Task Multi_invoice_receipt_sets_SalesInvoiceId_null_when_it_spans_two_distinct_invoices()
    {
        var invA = await PostInvoice(price: 1000);
        var invB = await PostInvoice(price: 500);
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineA.Id, 1050), new ReceiptLineAllocationInput(lineB.Id, 525) });

        Assert.Null(r.SalesInvoiceId);
    }

    [Fact]
    public async Task Multi_invoice_receipt_call_that_resolves_to_one_distinct_invoice_still_sets_SalesInvoiceId()
    {
        var inv = await PostMultiLineInvoice();
        var lines = inv.Lines.OrderBy(l => l.LineNo).ToList();

        // New (salesInvoiceId: null, allocations: ...) call shape, but every line belongs to the
        // same invoice — should still resolve and set SalesInvoiceId, not leave it null.
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 2100, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lines[0].Id, 1050), new ReceiptLineAllocationInput(lines[1].Id, 1050) });

        Assert.Equal(inv.Id, r.SalesInvoiceId);
    }

    [Fact]
    public async Task Multi_invoice_allocation_exceeding_one_invoices_outstanding_is_rejected_without_touching_the_other()
    {
        var invA = await PostInvoice(price: 1000); // gross 1050
        var invB = await PostInvoice(price: 500);  // gross 525
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, null, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1600, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineA.Id, 1100), new ReceiptLineAllocationInput(lineB.Id, 500) })); // 1100 > invA's 1050 outstanding

        // The whole receipt must have rolled back — invB is untouched.
        var open = await _customers.GetOpenInvoicesAsync(_db.Customer.Id);
        Assert.Equal(525m, open.Single(o => o.Id == invB.Id).Outstanding);
    }

    [Fact]
    public async Task Multi_invoice_allocation_exceeding_one_lines_balance_is_rejected()
    {
        var invA = await PostMultiLineInvoice(); // 3 lines, each gross 1050
        var invB = await PostInvoice(price: 500); // gross 525
        var linesA = invA.Lines.OrderBy(l => l.LineNo).ToList();
        var lineB = invB.Lines.Single();

        // Sums to the receipt amount, but linesA[0]'s own share (1100) exceeds its own Gross (1050).
        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, null, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1625, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(linesA[0].Id, 1100), new ReceiptLineAllocationInput(lineB.Id, 525) }));
    }

    [Fact]
    public async Task Multi_invoice_allocations_not_summing_to_the_receipt_amount_are_rejected()
    {
        var invA = await PostInvoice(price: 1000);
        var invB = await PostInvoice(price: 500);
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, null, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 2000, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineA.Id, 1050), new ReceiptLineAllocationInput(lineB.Id, 525) })); // sums to 1575, not 2000
    }

    [Fact]
    public async Task Multi_invoice_receipt_against_a_different_customers_invoice_is_rejected()
    {
        var invA = await PostInvoice(price: 1000);
        var other = await _customers.CreateAsync(new NewCustomerInput("Other Co", null, "AED", 0, 30, null, null, null, null));
        var invOther = await _invoices.CreateAndPostAsync(other.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 500, 0.05m) }, Now);

        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, null, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(invA.Lines.Single().Id, 1050), new ReceiptLineAllocationInput(invOther.Lines.Single().Id, 525) }));
    }

    [Fact]
    public async Task Multi_invoice_receipt_against_a_voided_invoice_is_rejected()
    {
        var invA = await PostInvoice(price: 1000);
        var draftInv = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 500, 0.05m) }, Now);
        await _invoices.VoidDraftAsync(draftInv.Id, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _receipts.CreateAndPostAsync(
            _db.Customer.Id, null, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(invA.Lines.Single().Id, 1050), new ReceiptLineAllocationInput(draftInv.Lines.Single().Id, 525) }));
    }

    [Fact]
    public async Task Multi_invoice_allocation_processes_invoice_groups_in_ascending_id_order_regardless_of_input_order()
    {
        var invA = await PostInvoice(price: 1000); // lower id
        var invB = await PostInvoice(price: 500);  // higher id
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        // B's line passed before A's line — outcome should be identical regardless of input order.
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineB.Id, 525), new ReceiptLineAllocationInput(lineA.Id, 1050) });

        Assert.Null(r.SalesInvoiceId); // spans two invoices either way
        var open = await _customers.GetOpenInvoicesAsync(_db.Customer.Id);
        Assert.DoesNotContain(open, o => o.Id == invA.Id);
        Assert.DoesNotContain(open, o => o.Id == invB.Id);
    }

    // --- Read-model correctness: the actual bug a single SalesInvoiceId FK could no longer represent ---

    [Fact]
    public async Task GetOpenInvoicesAsync_reflects_a_multi_invoice_receipts_allocation_on_both_invoices()
    {
        var invA = await PostInvoice(price: 1000); // gross 1050
        var invB = await PostInvoice(price: 500);  // gross 525
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1000, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineA.Id, 700), new ReceiptLineAllocationInput(lineB.Id, 300) });

        var open = await _customers.GetOpenInvoicesAsync(_db.Customer.Id);
        Assert.Equal(350m, open.Single(o => o.Id == invA.Id).Outstanding); // 1050 - 700
        Assert.Equal(225m, open.Single(o => o.Id == invB.Id).Outstanding); // 525 - 300
    }

    [Fact]
    public async Task GetAgingAsync_does_not_double_count_a_fully_applied_multi_invoice_receipt_as_unallocated()
    {
        var invA = await PostInvoice(price: 1000); // gross 1050
        var invB = await PostInvoice(price: 500);  // gross 525
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineA.Id, 1050), new ReceiptLineAllocationInput(lineB.Id, 525) }); // fully settles both

        var aging = await _customers.GetAgingAsync(new(2026, 6, 1));
        // Both invoices fully paid: no outstanding bucket, and the receipt must NOT also show as an unallocated credit.
        Assert.Empty(aging);
    }

    [Fact]
    public async Task GetAgingAsync_still_reports_a_pure_on_account_receipt_as_unallocated()
    {
        await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 300, null, "tester", Now); // no invoice at all

        var aging = await _customers.GetAgingAsync(new(2026, 6, 1));
        Assert.Equal(300m, aging.Single().UnallocatedCredits);
    }

    [Fact]
    public async Task SalesInvoiceService_GetAllAsync_balance_reflects_a_multi_invoice_receipts_allocation()
    {
        var invA = await PostInvoice(price: 1000); // gross 1050
        var invB = await PostInvoice(price: 500);  // gross 525
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1000, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineA.Id, 700), new ReceiptLineAllocationInput(lineB.Id, 300) });

        var rows = await _invoices.GetAllAsync();
        Assert.Equal(350m, rows.Single(r => r.Invoice.Id == invA.Id).Balance);
        Assert.Equal(225m, rows.Single(r => r.Invoice.Id == invB.Id).Balance);
    }

    [Fact]
    public async Task GetStatementAsync_and_GetSummariesAsync_unaffected_by_multi_invoice_receipts()
    {
        var invA = await PostInvoice(price: 1000);
        var invB = await PostInvoice(price: 500);
        var lineA = invA.Lines.Single();
        var lineB = invB.Lines.Single();

        await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1575, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineA.Id, 1050), new ReceiptLineAllocationInput(lineB.Id, 525) });

        var stmt = await _customers.GetStatementAsync(_db.Customer.Id);
        Assert.Equal(3, stmt.Count); // invA, invB, one receipt row
        Assert.Equal(0m, stmt[^1].RunningBalance); // fully settled

        var summaries = await _customers.GetSummariesAsync();
        var summary = summaries.Single(s => s.Id == _db.Customer.Id);
        Assert.Equal(1575m, summary.Invoiced);
        Assert.Equal(1575m, summary.Received);
        Assert.Equal(0m, summary.Outstanding);
    }

    // --- Payment Mode / Draft receipt workflow ---

    [Fact]
    public async Task Receipt_persists_the_selected_PaymentMode()
    {
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now, null, PaymentMode.Cheque);

        Assert.Equal(PaymentMode.Cheque, r.PaymentMode);
    }

    [Fact]
    public async Task CreateDraftAsync_reserves_a_receipt_number_and_generates_no_GL_voucher()
    {
        var r = await _receipts.CreateDraftAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);

        Assert.Equal(VoucherStatus.Draft, r.Status);
        Assert.Null(r.JournalVoucherId);
        Assert.StartsWith("RV-", r.ReceiptNo);
    }

    [Fact]
    public async Task PostDraftAsync_posts_the_reserved_receipt_number_and_generates_the_GL_voucher()
    {
        var draft = await _receipts.CreateDraftAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);
        var receiptNo = draft.ReceiptNo;

        var posted = await _receipts.PostDraftAsync(draft.Id, "tester", Now);

        Assert.Equal(VoucherStatus.Posted, posted.Status);
        Assert.Equal(receiptNo, posted.ReceiptNo);
        Assert.Equal(receiptNo, posted.JournalVoucher!.VoucherNo);
    }

    [Fact]
    public async Task PostDraftAsync_against_a_single_line_invoice_auto_allocates()
    {
        var inv = await PostInvoice(); // gross 1050
        var draft = await _receipts.CreateDraftAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050, null, "tester", Now); // no allocations given at draft time

        await _receipts.PostDraftAsync(draft.Id, "tester", Now);

        var balances = await _invoices.GetLineBalancesAsync(inv.Id);
        Assert.Equal(0m, balances.Single().Balance);
    }

    [Fact]
    public async Task PostDraftAsync_on_an_already_posted_receipt_is_rejected()
    {
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _receipts.PostDraftAsync(r.Id, "tester", Now));
    }

    [Fact]
    public async Task VoidDraftAsync_flips_status_and_is_rejected_for_posted_receipts()
    {
        var draft = await _receipts.CreateDraftAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);
        await _receipts.VoidDraftAsync(draft.Id);
        await Assert.ThrowsAsync<PostingException>(() => _receipts.PostDraftAsync(draft.Id, "tester", Now));

        var posted = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);
        await Assert.ThrowsAsync<PostingException>(() => _receipts.VoidDraftAsync(posted.Id));
    }

    [Fact]
    public async Task NextReceiptNoAsync_never_collides_between_a_pending_draft_and_a_directly_posted_receipt()
    {
        var draft = await _receipts.CreateDraftAsync(_db.Customer.Id, null, new(2026, 5, 10), _db.May.Id,
            _db.Bank.Id, 300, null, "tester", Now);
        var posted = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 12), _db.May.Id,
            _db.Bank.Id, 300, null, "tester", Now);

        Assert.NotEqual(draft.ReceiptNo, posted.ReceiptNo);

        var draftPosted = await _receipts.PostDraftAsync(draft.Id, "tester", Now);
        Assert.NotEqual(draftPosted.ReceiptNo, posted.ReceiptNo);
    }

    // --- Invoice Detail View support: GetByIdAsync / UpdateDraftAsync ---

    [Fact]
    public async Task GetByIdAsync_returns_the_invoice_with_full_detail_and_null_for_a_missing_id()
    {
        var inv = await PostInvoice();

        var found = await _invoices.GetByIdAsync(inv.Id);
        Assert.NotNull(found);
        Assert.Equal(inv.InvoiceNo, found!.InvoiceNo);
        Assert.NotNull(found.Customer);
        Assert.Single(found.Lines);
        Assert.NotNull(found.JournalVoucher);

        Assert.Null(await _invoices.GetByIdAsync(-1));
    }

    [Fact]
    public async Task UpdateDraftAsync_replaces_a_drafts_header_and_lines()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Original", _db.Revenue.Id, null, 1, 500, 0.05m) }, Now);

        var updated = await _invoices.UpdateDraftAsync(draft.Id, _db.Customer.Id, new(2026, 5, 12), _db.May.Id, "Updated narration",
            new[] { new InvoiceLineInput("Revised", _db.Revenue.Id, null, 2, 300, 0.05m) });

        Assert.Equal(draft.InvoiceNo, updated.InvoiceNo); // number never changes
        Assert.Equal("Updated narration", updated.Narration);
        Assert.Single(updated.Lines);
        Assert.Equal("Revised", updated.Lines.Single().Description);
        Assert.Equal(600m, updated.TotalNet); // 2 * 300
    }

    [Fact]
    public async Task UpdateDraftAsync_rejects_an_already_posted_invoice()
    {
        var inv = await PostInvoice();
        await Assert.ThrowsAsync<PostingException>(() => _invoices.UpdateDraftAsync(
            inv.Id, _db.Customer.Id, new(2026, 5, 10), _db.May.Id, null,
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }));
    }

    [Fact]
    public async Task UpdateDraftAsync_rejects_zero_lines()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Original", _db.Revenue.Id, null, 1, 500, 0.05m) }, Now);

        await Assert.ThrowsAsync<PostingException>(() => _invoices.UpdateDraftAsync(
            draft.Id, _db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, Array.Empty<InvoiceLineInput>()));
    }

    [Fact]
    public async Task Editing_then_posting_a_draft_reflects_the_edited_values()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Original", _db.Revenue.Id, null, 1, 500, 0.05m) }, Now);

        await _invoices.UpdateDraftAsync(draft.Id, _db.Customer.Id, new(2026, 5, 10), _db.May.Id, null,
            new[] { new InvoiceLineInput("Revised", _db.Revenue.Id, null, 1, 1000, 0.05m) });
        var posted = await _invoices.PostDraftAsync(draft.Id, "tester", Now);

        Assert.Equal(VoucherStatus.Posted, posted.Status);
        Assert.Equal(1050m, posted.TotalGross); // reflects the edit (1000 + 5% vat), not the original 500
        Assert.NotNull(posted.JournalVoucher);
        Assert.Equal(1050m, posted.JournalVoucher!.TotalDebit);
    }

    // --- Customer PO No. / Discount % / UOM / Delivery & Sales Order refs / Notes ---

    [Fact]
    public async Task Customer_PO_No_persists_through_create_and_post()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now,
            customerPoNo: "PO-12345");

        Assert.Equal("PO-12345", inv.CustomerPoNo);
        var found = await _invoices.GetByIdAsync(inv.Id);
        Assert.Equal("PO-12345", found!.CustomerPoNo);
    }

    [Fact]
    public async Task Delivery_note_ref_sales_order_ref_and_notes_persist_through_create_and_post()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now,
            deliveryNoteRef: "DN-2026-0038", salesOrderRef: "SO-2026-0021", notes: "Payment due within 30 days.");

        var found = await _invoices.GetByIdAsync(inv.Id);
        Assert.Equal("DN-2026-0038", found!.DeliveryNoteRef);
        Assert.Equal("SO-2026-0021", found.SalesOrderRef);
        Assert.Equal("Payment due within 30 days.", found.Notes);
    }

    [Fact]
    public async Task Discount_percent_reduces_Net_Vat_and_Gross()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m, DiscountValue: 10) }, Now);

        var line = inv.Lines.Single();
        Assert.Equal(900m, line.Net);   // 1000 * (1 - 10%)
        Assert.Equal(45m, line.Vat);    // 900 * 5%
        Assert.Equal(945m, line.Gross);
        Assert.Equal(945m, inv.TotalGross);
    }

    [Fact]
    public async Task Negative_discount_percent_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _invoices.CreateAndPostAsync(
            _db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m, DiscountValue: -1) }, Now));
    }

    [Fact]
    public async Task Discount_amount_reduces_Net_Vat_and_Gross()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m,
                DiscountValue: 150, DiscountType: DiscountType.Amount) }, Now);

        var line = inv.Lines.Single();
        Assert.Equal(DiscountType.Amount, line.DiscountType);
        Assert.Equal(850m, line.Net);   // 1000 - 150 flat
        Assert.Equal(42.5m, line.Vat);  // 850 * 5%
        Assert.Equal(892.5m, line.Gross);
    }

    [Fact]
    public async Task Negative_discount_amount_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _invoices.CreateAndPostAsync(
            _db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m,
                DiscountValue: -1, DiscountType: DiscountType.Amount) }, Now));
    }

    [Fact]
    public void Discount_amount_exceeding_line_subtotal_floors_net_at_zero()
    {
        var line = new SalesInvoiceLine
        {
            Quantity = 1, UnitPrice = 100, VatRate = 0.05m,
            DiscountType = DiscountType.Amount, DiscountValue = 500,
        };

        Assert.Equal(0m, line.Net);
        Assert.Equal(0m, line.Vat);
    }

    [Fact]
    public async Task Subject_terms_and_conditions_and_salesperson_persist_through_create_and_post()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now,
            subject: "Website redesign — phase 1", termsAndConditions: "Payment due within 15 days of receipt.",
            salesperson: "Ahmed Al Mansoori");

        var found = await _invoices.GetByIdAsync(inv.Id);
        Assert.Equal("Website redesign — phase 1", found!.Subject);
        Assert.Equal("Payment due within 15 days of receipt.", found.TermsAndConditions);
        Assert.Equal("Ahmed Al Mansoori", found.Salesperson);
    }

    [Fact]
    public async Task Uom_persists_through_create_and_post()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m, Uom: "hrs") }, Now);

        Assert.Equal("hrs", inv.Lines.Single().Uom);
    }

    // --- Approval workflow ---

    [Fact]
    public async Task Submitting_a_draft_for_approval_blocks_posting_until_a_decision_is_made()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);

        await _invoices.SubmitForApprovalAsync(draft.Id, "tester", Now);
        await Assert.ThrowsAsync<PostingException>(() => _invoices.PostDraftAsync(draft.Id, "approver", Now));

        await _invoices.ApproveAsync(draft.Id, "approver", Now);
        var posted = await _invoices.PostDraftAsync(draft.Id, "approver", Now);
        Assert.Equal(VoucherStatus.Posted, posted.Status);
        Assert.Equal(ApprovalStatus.Approved, posted.ApprovalStatus);
    }

    [Fact]
    public async Task Rejecting_a_pending_invoice_leaves_it_editable_and_resubmittable()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);

        await _invoices.SubmitForApprovalAsync(draft.Id, "tester", Now);
        await _invoices.RejectAsync(draft.Id, "approver", Now, "Wrong account");

        var found = await _invoices.GetByIdAsync(draft.Id);
        Assert.Equal(ApprovalStatus.Rejected, found!.ApprovalStatus);
        Assert.Equal("Wrong account", found.ApprovalNote);
        Assert.Equal(VoucherStatus.Draft, found.Status); // still editable

        await _invoices.SubmitForApprovalAsync(draft.Id, "tester", Now);
        await _invoices.ApproveAsync(draft.Id, "approver", Now);
        var posted = await _invoices.PostDraftAsync(draft.Id, "approver", Now);
        Assert.Equal(VoucherStatus.Posted, posted.Status);
    }

    [Fact]
    public async Task Approving_or_rejecting_an_invoice_that_isnt_pending_is_rejected()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);

        await Assert.ThrowsAsync<PostingException>(() => _invoices.ApproveAsync(draft.Id, "approver", Now));
        await Assert.ThrowsAsync<PostingException>(() => _invoices.RejectAsync(draft.Id, "approver", Now, null));
    }

    [Fact]
    public async Task Voiding_an_invoice_records_who_and_when()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);

        await _invoices.VoidDraftAsync(draft.Id, "tester", Now);

        var found = await _invoices.GetByIdAsync(draft.Id);
        Assert.Equal(VoucherStatus.Void, found!.Status);
        Assert.Equal("tester", found.VoidedBy);
        Assert.Equal(Now, found.VoidedAtUtc);
    }

    [Fact]
    public async Task Posting_records_who_posted_it()
    {
        var inv = await PostInvoice();
        Assert.Equal("tester", inv.CreatedBy);

        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);
        var posted = await _invoices.PostDraftAsync(draft.Id, "poster", Now);
        Assert.Equal("poster", posted.PostedBy);
    }

    // --- Attachment ---

    [Fact]
    public async Task Attachment_can_be_set_downloaded_and_removed()
    {
        var inv = await PostInvoice();
        var bytes = new byte[] { 1, 2, 3, 4 };

        await _invoices.SetAttachmentAsync(inv.Id, "proof.pdf", "application/pdf", bytes);
        var att = await _invoices.GetAttachmentAsync(inv.Id);
        Assert.NotNull(att);
        Assert.Equal("proof.pdf", att!.Value.FileName);
        Assert.Equal(bytes, att.Value.Data);

        await _invoices.RemoveAttachmentAsync(inv.Id);
        Assert.Null(await _invoices.GetAttachmentAsync(inv.Id));
    }

    [Fact]
    public async Task Attachment_over_the_size_limit_is_rejected()
    {
        var inv = await PostInvoice();
        var tooBig = new byte[SalesInvoiceService.MaxAttachmentBytes + 1];

        await Assert.ThrowsAsync<PostingException>(() =>
            _invoices.SetAttachmentAsync(inv.Id, "big.pdf", "application/pdf", tooBig));
    }

    // --- Receipt Voucher: Reference No. / Cheque Date / PDC mode / Attachment ---

    [Fact]
    public async Task Receipt_reference_no_and_cheque_date_persist_through_create_and_post()
    {
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now, null, PaymentMode.Cheque,
            referenceNo: "CHQ-00981", chequeDate: new(2026, 5, 20));

        var found = await _receipts.GetByIdAsync(r.Id);
        Assert.Equal("CHQ-00981", found!.ReferenceNo);
        Assert.Equal(new DateOnly(2026, 5, 20), found.ChequeDate);
        Assert.Equal(PaymentMode.Cheque, found.PaymentMode);
    }

    [Fact]
    public async Task Receipt_supports_the_post_dated_cheque_payment_mode()
    {
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now, null, PaymentMode.PostDatedCheque);

        Assert.Equal(PaymentMode.PostDatedCheque, r.PaymentMode);
    }

    [Fact]
    public async Task Receipt_attachment_can_be_set_downloaded_and_removed()
    {
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);
        var bytes = new byte[] { 9, 8, 7 };

        await _receipts.SetAttachmentAsync(r.Id, "cheque.jpg", "image/jpeg", bytes);
        var att = await _receipts.GetAttachmentAsync(r.Id);
        Assert.NotNull(att);
        Assert.Equal("cheque.jpg", att!.Value.FileName);
        Assert.Equal(bytes, att.Value.Data);

        await _receipts.RemoveAttachmentAsync(r.Id);
        Assert.Null(await _receipts.GetAttachmentAsync(r.Id));
    }

    [Fact]
    public async Task Receipt_attachment_over_the_size_limit_is_rejected()
    {
        var r = await _receipts.CreateAndPostAsync(_db.Customer.Id, null, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 500, null, "tester", Now);
        var tooBig = new byte[ReceiptService.MaxAttachmentBytes + 1];

        await Assert.ThrowsAsync<PostingException>(() =>
            _receipts.SetAttachmentAsync(r.Id, "big.jpg", "image/jpeg", tooBig));
    }

    // --- Lock Invoice: blocks further edits/void/attachment changes ---

    [Fact]
    public async Task Locking_a_draft_invoice_blocks_edit_void_and_post()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);

        await _invoices.LockAsync(draft.Id, "admin", Now);

        await Assert.ThrowsAsync<PostingException>(() => _invoices.UpdateDraftAsync(
            draft.Id, _db.Customer.Id, new(2026, 5, 10), _db.May.Id, null,
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 2000, 0.05m) }));
        await Assert.ThrowsAsync<PostingException>(() => _invoices.PostDraftAsync(draft.Id, "tester", Now));
        await Assert.ThrowsAsync<PostingException>(() => _invoices.VoidDraftAsync(draft.Id, "tester", Now));
        await Assert.ThrowsAsync<PostingException>(() =>
            _invoices.SetAttachmentAsync(draft.Id, "a.pdf", "application/pdf", new byte[] { 1 }));
    }

    [Fact]
    public async Task Unlocking_an_invoice_restores_normal_behavior()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);
        await _invoices.LockAsync(draft.Id, "admin", Now);

        await _invoices.UnlockAsync(draft.Id);
        var posted = await _invoices.PostDraftAsync(draft.Id, "tester", Now);

        Assert.Equal(VoucherStatus.Posted, posted.Status);
    }

    [Fact]
    public async Task Locking_an_already_locked_invoice_is_rejected()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);
        await _invoices.LockAsync(draft.Id, "admin", Now);

        await Assert.ThrowsAsync<PostingException>(() => _invoices.LockAsync(draft.Id, "admin", Now));
    }

    // --- Reminders ---

    [Fact]
    public async Task Reminder_on_a_draft_invoice_is_rejected()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);

        await Assert.ThrowsAsync<PostingException>(() => _invoices.SendReminderAsync(draft.Id, "tester", Now, isAutomated: false));
    }

    [Fact]
    public async Task Reminder_on_a_fully_paid_invoice_is_rejected()
    {
        var inv = await PostInvoice(); // gross 1050
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id, _db.Bank.Id, 1050, null, "tester", Now);

        await Assert.ThrowsAsync<PostingException>(() => _invoices.SendReminderAsync(inv.Id, "tester", Now, isAutomated: false));
    }

    [Fact]
    public async Task Reminder_without_a_customer_email_is_rejected()
    {
        var inv = await PostInvoice();
        Assert.Null(inv.Customer.Email); // seeded test customer has no email

        await Assert.ThrowsAsync<PostingException>(() => _invoices.SendReminderAsync(inv.Id, "tester", Now, isAutomated: false));
        Assert.Null(await _invoices.GetLastReminderSentAtAsync(inv.Id));
    }

    // --- Share ---

    [Fact]
    public async Task Share_token_is_generated_once_and_stays_stable()
    {
        var inv = await PostInvoice();

        var token1 = await _invoices.GetOrCreateShareTokenAsync(inv.Id);
        var token2 = await _invoices.GetOrCreateShareTokenAsync(inv.Id);

        Assert.False(string.IsNullOrWhiteSpace(token1));
        Assert.Equal(token1, token2);
    }

    [Fact]
    public async Task Shared_invoice_resolves_by_token_and_unknown_tokens_return_null()
    {
        var inv = await PostInvoice();
        var token = await _invoices.GetOrCreateShareTokenAsync(inv.Id);

        var found = await _invoices.GetBySharedTokenAsync(token);
        Assert.NotNull(found);
        Assert.Equal(inv.Id, found!.Id);

        Assert.Null(await _invoices.GetBySharedTokenAsync("not-a-real-token"));
    }
}
