using AegisErp.Domain;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

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
        _invoices = new SalesInvoiceService(_db);
        _receipts = new ReceiptService(_db);
        _customers = new CustomerService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<AegisErp.Domain.Entities.SalesInvoice> PostInvoice(
        decimal qty = 1, decimal price = 1000, decimal vat = 0.05m, DateOnly? date = null) =>
        _invoices.CreateAndPostAsync(_db.Customer.Id, date ?? new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, qty, price, vat) }, Now);

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
}
