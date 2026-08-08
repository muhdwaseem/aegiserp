using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

public class DirectExpensePostingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DirectExpenseService _expenses;
    private static readonly DateTime Now = new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    public DirectExpensePostingTests() => _expenses = new DirectExpenseService(_db);
    public void Dispose() => _db.Dispose();

    private Task<DirectExpense> PostSimpleExpense(decimal amount = 500, int? vendorId = null, int? customerId = null) =>
        _expenses.CreateAndPostAsync(vendorId, customerId, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            "REF-001", "Fuel", "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, amount) });

    [Fact]
    public async Task Posting_a_simple_expense_generates_a_balanced_Dr_expense_Cr_bank_voucher()
    {
        var e = await PostSimpleExpense(500);

        Assert.Equal(VoucherStatus.Posted, e.Status);
        Assert.Equal(500m, e.TotalAmount);
        Assert.NotNull(e.JournalVoucher);
        Assert.Equal(e.ExpenseNo, e.JournalVoucher!.VoucherNo);

        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == e.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Debit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit);
    }

    [Fact]
    public async Task Itemized_expense_across_two_accounts_stays_balanced()
    {
        await using var db = _db.CreateDbContext();
        var travel = new Account { CompanyId = _db.Company.Id, Code = "51020", Name = "Travel", Type = AccountType.Expense };
        db.Accounts.Add(travel);
        await db.SaveChangesAsync();

        var e = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            null, null, "tester", Now,
            new[]
            {
                new DirectExpenseLineInput(_db.Expense.Id, null, "Office supplies", 300),
                new DirectExpenseLineInput(travel.Id, null, "Taxi", 200),
            });

        Assert.Equal(500m, e.TotalAmount);

        await using var verifyDb = _db.CreateDbContext();
        var voucher = await verifyDb.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == e.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(300m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Debit);
        Assert.Equal(200m, voucher.Lines.Single(l => l.AccountId == travel.Id).Debit);
        Assert.Equal(500m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit);
    }

    [Fact]
    public async Task Expense_with_no_lines_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _expenses.CreateAndPostAsync(
            null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id, null, null, "tester", Now,
            Array.Empty<DirectExpenseLineInput>()));
    }

    [Fact]
    public async Task Expense_line_with_a_non_positive_amount_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _expenses.CreateAndPostAsync(
            null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id, null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, 0) }));
    }

    [Fact]
    public async Task Vendor_is_genuinely_optional()
    {
        var e = await PostSimpleExpense(vendorId: null);
        Assert.Null(e.VendorId);
    }

    [Fact]
    public async Task Customer_is_an_optional_tag_with_no_other_effect()
    {
        var customer = await new CustomerService(_db).CreateAsync(
            new NewCustomerInput("Acme Co", null, "AED", 0, 30, null, null, null, null));

        var e = await PostSimpleExpense(customerId: customer.Id);
        Assert.Equal(customer.Id, e.CustomerId);
    }

    [Fact]
    public async Task Sequential_expenses_get_distinct_expense_numbers()
    {
        var e1 = await PostSimpleExpense(100);
        var e2 = await PostSimpleExpense(200);
        Assert.NotEqual(e1.ExpenseNo, e2.ExpenseNo);
        Assert.StartsWith("EXP-", e1.ExpenseNo);
    }

    [Fact]
    public async Task Vat_exclusive_amount_adds_vat_on_top_and_posts_to_vat_input()
    {
        var e = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, 100, VatRate: 0.05m) },
            amountsIncludeVat: false);

        Assert.Equal(100m, e.TotalNet);
        Assert.Equal(5m, e.TotalVat);
        Assert.Equal(105m, e.TotalAmount);

        await using var db2 = _db.CreateDbContext();
        var voucher = await db2.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == e.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(100m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Debit);
        Assert.Equal(5m, voucher.Lines.Single(l => l.AccountId == _db.VatInput.Id).Debit);
        Assert.Equal(105m, voucher.Lines.Single(l => l.AccountId == _db.Bank.Id).Credit);
    }

    [Fact]
    public async Task Vat_inclusive_amount_backs_out_vat_from_the_entered_total()
    {
        var e = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, 105, VatRate: 0.05m) },
            amountsIncludeVat: true);

        Assert.Equal(100m, e.TotalNet);
        Assert.Equal(5m, e.TotalVat);
        Assert.Equal(105m, e.TotalAmount); // the entered total is what leaves the bank either way
    }

    [Fact]
    public async Task Zero_vat_rate_lines_never_touch_the_vat_input_account()
    {
        var e = await PostSimpleExpense(500); // default VatRate = 0

        Assert.Equal(0m, e.TotalVat);
        await using var db = _db.CreateDbContext();
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == e.JournalVoucherId);
        Assert.Equal(2, voucher.Lines.Count); // expense + bank only, no VAT line
    }

    [Fact]
    public async Task VendorInvoiceNo_is_persisted_and_distinct_from_reference()
    {
        var e = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            "REF-001", null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, 100) },
            vendorInvoiceNo: "RCPT-9981");

        Assert.Equal("REF-001", e.Reference);
        Assert.Equal("RCPT-9981", e.VendorInvoiceNo);
    }

    [Fact]
    public async Task Attachment_round_trips_through_SetAttachmentAsync_and_GetAttachmentAsync()
    {
        var e = await PostSimpleExpense(500);
        var bytes = new byte[] { 1, 2, 3, 4 };

        await _expenses.SetAttachmentAsync(e.Id, "receipt.jpg", "image/jpeg", bytes);

        var stored = await _expenses.GetAttachmentAsync(e.Id);
        Assert.NotNull(stored);
        Assert.Equal("receipt.jpg", stored!.Value.FileName);
        Assert.Equal("image/jpeg", stored.Value.ContentType);
        Assert.Equal(bytes, stored.Value.Data);
    }

    [Fact]
    public async Task Attachment_over_the_size_limit_is_rejected()
    {
        var e = await PostSimpleExpense(500);
        var tooBig = new byte[DirectExpenseService.MaxAttachmentBytes + 1];

        await Assert.ThrowsAsync<PostingException>(() => _expenses.SetAttachmentAsync(e.Id, "big.pdf", "application/pdf", tooBig));
    }

    [Fact]
    public async Task Pay_later_expense_credits_expenses_payable_instead_of_bank()
    {
        using (var seedDb = _db.CreateUnscopedDbContext())
        {
            seedDb.Accounts.Add(new Account { CompanyId = _db.Company.Id, Code = WellKnownAccounts.ExpensesPayable, Name = "Expenses Payable", Type = AccountType.Liability });
            seedDb.SaveChanges();
        }

        var e = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, null,
            null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, 100, VatRate: 0.05m) },
            amountsIncludeVat: false, payLater: true);

        Assert.True(e.IsPayLater);
        Assert.Null(e.BankAccountId);
        Assert.Equal(VoucherStatus.Posted, e.Status);

        await using var db = _db.CreateDbContext();
        var payable = await db.Accounts.SingleAsync(a => a.Code == WellKnownAccounts.ExpensesPayable);
        var voucher = await db.JournalVouchers.Include(v => v.Lines).SingleAsync(v => v.Id == e.JournalVoucherId);
        Assert.Equal(voucher.TotalDebit, voucher.TotalCredit);
        Assert.Equal(100m, voucher.Lines.Single(l => l.AccountId == _db.Expense.Id).Debit);
        Assert.Equal(5m, voucher.Lines.Single(l => l.AccountId == _db.VatInput.Id).Debit);
        Assert.Equal(105m, voucher.Lines.Single(l => l.AccountId == payable.Id).Credit);
        Assert.DoesNotContain(voucher.Lines, l => l.AccountId == _db.Bank.Id);
    }

    [Fact]
    public async Task Pay_now_expense_still_requires_a_bank_account()
    {
        await Assert.ThrowsAsync<PostingException>(() => _expenses.CreateAndPostAsync(
            null, null, new(2026, 5, 10), _db.May.Id, null, null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, null, 100) },
            payLater: false));
    }

    [Fact]
    public async Task An_itemized_line_picked_from_the_catalog_persists_its_ItemId()
    {
        var items = new ItemService(_db);
        var item = await items.CreateAsync(new NewItemInput(
            "Courier charges", ItemKind.Service, "unit", 0,
            null, null, 75, _db.Expense.Id, "Courier delivery", null));

        var e = await _expenses.CreateAndPostAsync(null, null, new(2026, 5, 10), _db.May.Id, _db.Bank.Id,
            null, null, "tester", Now,
            new[] { new DirectExpenseLineInput(_db.Expense.Id, null, "Courier delivery", 75, item.Id) });

        var found = (await _expenses.GetRecentAsync(1)).Single();
        Assert.Equal(item.Id, found.Lines.Single().ItemId);
    }
}
