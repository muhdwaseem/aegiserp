using AegisErp.Domain;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

public class JournalPostingTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly JournalService _journal;
    private static readonly DateTime Now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public JournalPostingTests() => _journal = new JournalService(_db);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Balanced_voucher_posts_and_hits_the_ledger()
    {
        var v = await _journal.CreateAndPostAsync(
            VoucherType.Journal, new(2026, 5, 10), _db.May.Id, "Test", null, "tester",
            new[]
            {
                new VoucherLineInput(_db.Expense.Id, null, "expense", 100, 0),
                new VoucherLineInput(_db.Bank.Id, null, "bank", 0, 100),
            }, Now);

        Assert.Equal(VoucherStatus.Posted, v.Status);
        Assert.StartsWith("JV-2026-", v.VoucherNo);

        await using var db = _db.CreateDbContext();
        var saved = await db.JournalVouchers.Include(x => x.Lines).SingleAsync(x => x.Id == v.Id);
        Assert.Equal(2, saved.Lines.Count);
        Assert.Equal(saved.TotalDebit, saved.TotalCredit);
    }

    [Fact]
    public async Task Unbalanced_voucher_is_rejected_and_nothing_is_saved()
    {
        await Assert.ThrowsAsync<PostingException>(() => _journal.CreateAndPostAsync(
            VoucherType.Journal, new(2026, 5, 10), _db.May.Id, "Bad", null, "tester",
            new[]
            {
                new VoucherLineInput(_db.Expense.Id, null, null, 100, 0),
                new VoucherLineInput(_db.Bank.Id, null, null, 0, 99),
            }, Now));

        await using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.JournalVouchers.CountAsync());
    }

    [Fact]
    public async Task Single_line_voucher_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _journal.CreateAndPostAsync(
            VoucherType.Journal, new(2026, 5, 10), _db.May.Id, null, null, "tester",
            new[] { new VoucherLineInput(_db.Expense.Id, null, null, 100, 0) }, Now));
    }

    [Fact]
    public async Task Line_with_both_debit_and_credit_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _journal.CreateAndPostAsync(
            VoucherType.Journal, new(2026, 5, 10), _db.May.Id, null, null, "tester",
            new[]
            {
                new VoucherLineInput(_db.Expense.Id, null, null, 100, 50),
                new VoucherLineInput(_db.Bank.Id, null, null, 0, 50),
            }, Now));
    }

    [Fact]
    public async Task Date_outside_the_fiscal_period_is_rejected()
    {
        await Assert.ThrowsAsync<PostingException>(() => _journal.CreateAndPostAsync(
            VoucherType.Journal, new(2026, 6, 10), _db.May.Id, null, null, "tester",
            new[]
            {
                new VoucherLineInput(_db.Expense.Id, null, null, 100, 0),
                new VoucherLineInput(_db.Bank.Id, null, null, 0, 100),
            }, Now));
    }

    [Fact]
    public async Task Voucher_numbers_continue_from_the_highest_existing_suffix()
    {
        // Simulate a seeded voucher with an explicit high number.
        await using (var db = _db.CreateDbContext())
        {
            var v = new AegisErp.Domain.Entities.JournalVoucher
            {
                VoucherNo = "JV-2026-0141",
                Type = VoucherType.Journal,
                Date = new(2026, 5, 2),
                FiscalPeriodId = _db.May.Id,
                CreatedAtUtc = Now,
            };
            v.Lines.Add(new() { LineNo = 1, AccountId = _db.Expense.Id, Debit = 10 });
            v.Lines.Add(new() { LineNo = 2, AccountId = _db.Bank.Id, Credit = 10 });
            v.Post(Now);
            db.JournalVouchers.Add(v);
            await db.SaveChangesAsync();
        }

        var next = await _journal.PeekNextVoucherNoAsync(VoucherType.Journal, 2026);
        Assert.Equal("JV-2026-0142", next);
    }
}
