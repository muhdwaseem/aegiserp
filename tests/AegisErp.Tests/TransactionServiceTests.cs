using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AegisErp.Tests;

public class TransactionServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SalesInvoiceService _invoices;
    private readonly ReceiptService _receipts;
    private readonly TransactionService _transactions;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public TransactionServiceTests()
    {
        _invoices = new SalesInvoiceService(_db, new EmailService(Options.Create(new SmtpOptions())));
        _receipts = new ReceiptService(_db);
        _transactions = new TransactionService(_db);
    }

    public void Dispose() => _db.Dispose();

    private Task<SalesInvoice> PostInvoice(decimal price = 1000) =>
        _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Passport processing", _db.Revenue.Id, null, 1, price, 0.05m) }, Now);

    [Fact]
    public async Task GetAllAsync_flattens_invoice_lines_with_a_tran_ref_per_line()
    {
        var inv = await PostInvoice();

        var rows = await _transactions.GetAllAsync();

        var row = Assert.Single(rows);
        Assert.Equal($"{inv.InvoiceNo}-L1", row.TranRef);
        Assert.Equal(inv.Id, row.InvoiceId);
        Assert.Equal(_db.Customer.Name, row.CustomerName);
        Assert.Equal("Passport processing", row.Description);
        Assert.False(row.IsCompleted);
        Assert.Null(row.SupplierName);
        Assert.Null(row.PaidFromAccountName);
    }

    [Fact]
    public async Task GetAllAsync_excludes_voided_invoices()
    {
        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now);
        await _invoices.VoidDraftAsync(draft.Id, "tester", Now);

        var rows = await _transactions.GetAllAsync();

        Assert.Empty(rows);
    }

    [Fact]
    public async Task GetAllAsync_resolves_paid_from_account_via_line_level_receipt_allocation()
    {
        var inv = await PostInvoice(1000);
        var lineId = inv.Lines.Single().Id;
        await _receipts.CreateAndPostAsync(_db.Customer.Id, inv.Id, new(2026, 5, 15), _db.May.Id,
            _db.Bank.Id, 1050m, null, "tester", Now,
            new[] { new ReceiptLineAllocationInput(lineId, 1050m) });

        var row = Assert.Single(await _transactions.GetAllAsync());

        Assert.Equal(_db.Bank.Name, row.PaidFromAccountName);
    }

    [Fact]
    public async Task SetCompletionAsync_marks_and_reopens_a_line()
    {
        var inv = await PostInvoice();
        var lineId = inv.Lines.Single().Id;

        await _transactions.SetCompletionAsync(lineId, true, "Ahmed", Now);
        var completed = Assert.Single(await _transactions.GetAllAsync());
        Assert.True(completed.IsCompleted);
        Assert.Equal("Ahmed", completed.CompletedBy);
        Assert.Equal(Now, completed.CompletedAtUtc);

        await _transactions.SetCompletionAsync(lineId, false, "Ahmed", Now);
        var reopened = Assert.Single(await _transactions.GetAllAsync());
        Assert.False(reopened.IsCompleted);
        Assert.Null(reopened.CompletedBy);
        Assert.Null(reopened.CompletedAtUtc);
    }

    [Fact]
    public async Task SetSupplierAsync_assigns_and_clears_a_vendor()
    {
        var inv = await PostInvoice();
        var lineId = inv.Lines.Single().Id;

        await _transactions.SetSupplierAsync(lineId, _db.Vendor.Id);
        var assigned = Assert.Single(await _transactions.GetAllAsync());
        Assert.Equal(_db.Vendor.Name, assigned.SupplierName);

        await _transactions.SetSupplierAsync(lineId, null);
        var cleared = Assert.Single(await _transactions.GetAllAsync());
        Assert.Null(cleared.SupplierName);
    }

    [Fact]
    public async Task SetSupplierAsync_rejects_an_unknown_vendor()
    {
        var inv = await PostInvoice();
        var lineId = inv.Lines.Single().Id;

        await Assert.ThrowsAsync<PostingException>(() => _transactions.SetSupplierAsync(lineId, 999_999));
    }

    [Fact]
    public async Task Transactions_of_another_company_are_not_visible_or_writable()
    {
        var other = _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var theirs = await _invoices.CreateAndPostAsync(other.Customer.Id, new(2026, 5, 12), other.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Service", other.Revenue.Id, null, 1, 500, 0.05m) }, Now);
        _db.SwitchTo(_db.Company.Id);

        var rows = await _transactions.GetAllAsync();
        Assert.Empty(rows);

        var theirLineId = theirs.Lines.Single().Id;
        await Assert.ThrowsAsync<PostingException>(() => _transactions.SetCompletionAsync(theirLineId, true, "tester", Now));
        await Assert.ThrowsAsync<PostingException>(() => _transactions.SetSupplierAsync(theirLineId, _db.Vendor.Id));
    }
}
