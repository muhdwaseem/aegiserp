using AegisErp.Domain;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AegisErp.Tests;

public class RecurringInvoiceServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SalesInvoiceService _invoices;
    private readonly RecurringInvoiceService _recurring;
    private static readonly DateTime Now = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    public RecurringInvoiceServiceTests()
    {
        _invoices = new SalesInvoiceService(_db, new EmailService(Options.Create(new SmtpOptions())));
        _recurring = new RecurringInvoiceService(_db, _invoices);
    }

    public void Dispose() => _db.Dispose();

    private Task<AegisErp.Domain.Entities.SalesInvoice> PostSourceInvoice() =>
        _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 1), _db.May.Id, "Monthly retainer", "tester",
            new[] { new InvoiceLineInput("Retainer", _db.Revenue.Id, _db.CostCenter.Id, 1, 1000, 0.05m) }, Now);

    [Fact]
    public async Task CreateFromInvoiceAsync_copies_customer_and_lines()
    {
        var source = await PostSourceInvoice();

        var profile = await _recurring.CreateFromInvoiceAsync(
            source.Id, RecurringFrequency.Monthly, 1, new(2026, 6, 1), null, "tester", Now);

        Assert.Equal(_db.Customer.Id, profile.CustomerId);
        Assert.Equal(new DateOnly(2026, 6, 1), profile.NextGenerationDate);
        Assert.Single(profile.Lines);
        Assert.Equal("Retainer", profile.Lines[0].Description);
        Assert.Equal(1000m, profile.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task Repeat_interval_must_be_positive()
    {
        var source = await PostSourceInvoice();
        await Assert.ThrowsAsync<PostingException>(() =>
            _recurring.CreateFromInvoiceAsync(source.Id, RecurringFrequency.Monthly, 0, new(2026, 6, 1), null, "tester", Now));
    }

    [Fact]
    public async Task End_date_before_start_date_is_rejected()
    {
        var source = await PostSourceInvoice();
        await Assert.ThrowsAsync<PostingException>(() => _recurring.CreateFromInvoiceAsync(
            source.Id, RecurringFrequency.Monthly, 1, new(2026, 6, 1), new(2026, 5, 1), "tester", Now));
    }

    [Theory]
    [InlineData(RecurringFrequency.Weekly, 1, "2026-06-08")]
    [InlineData(RecurringFrequency.Monthly, 1, "2026-07-01")]
    [InlineData(RecurringFrequency.Quarterly, 1, "2026-09-01")]
    [InlineData(RecurringFrequency.Yearly, 1, "2027-06-01")]
    [InlineData(RecurringFrequency.Monthly, 2, "2026-08-01")]
    public async Task GenerateDueInvoicesAsync_creates_a_draft_and_advances_by_frequency(
        RecurringFrequency frequency, int repeatEvery, string expectedNext)
    {
        var source = await PostSourceInvoice();
        var profile = await _recurring.CreateFromInvoiceAsync(
            source.Id, frequency, repeatEvery, new(2026, 6, 1), null, "tester", Now);

        var generated = await _recurring.GenerateDueInvoicesAsync(new(2026, 6, 1), "system", Now);

        Assert.Equal(1, generated);
        var updated = await _recurring.GetByIdAsync(profile.Id);
        Assert.Equal(DateOnly.Parse(expectedNext), updated!.NextGenerationDate);

        await using var db = _db.CreateDbContext();
        var newInvoice = await db.SalesInvoices.Include(i => i.Lines)
            .Where(i => i.CustomerId == _db.Customer.Id && i.Date == new DateOnly(2026, 6, 1))
            .SingleAsync();
        Assert.Equal(VoucherStatus.Draft, newInvoice.Status); // never auto-posted
        Assert.Single(newInvoice.Lines);
        Assert.Equal(1000m, newInvoice.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task A_profile_not_yet_due_generates_nothing()
    {
        var source = await PostSourceInvoice();
        await _recurring.CreateFromInvoiceAsync(source.Id, RecurringFrequency.Monthly, 1, new(2026, 7, 1), null, "tester", Now);

        var generated = await _recurring.GenerateDueInvoicesAsync(new(2026, 6, 1), "system", Now);
        Assert.Equal(0, generated);
    }

    [Fact]
    public async Task A_profile_deactivates_once_its_next_run_would_be_past_the_end_date()
    {
        var source = await PostSourceInvoice();
        var profile = await _recurring.CreateFromInvoiceAsync(
            source.Id, RecurringFrequency.Monthly, 1, new(2026, 6, 1), new(2026, 6, 15), "tester", Now);

        await _recurring.GenerateDueInvoicesAsync(new(2026, 6, 1), "system", Now);

        var updated = await _recurring.GetByIdAsync(profile.Id);
        Assert.False(updated!.IsActive); // next run (2026-07-01) is past the 2026-06-15 end date
    }

    [Fact]
    public async Task Paused_profiles_are_skipped()
    {
        var source = await PostSourceInvoice();
        var profile = await _recurring.CreateFromInvoiceAsync(
            source.Id, RecurringFrequency.Monthly, 1, new(2026, 6, 1), null, "tester", Now);
        await _recurring.PauseAsync(profile.Id);

        var generated = await _recurring.GenerateDueInvoicesAsync(new(2026, 6, 1), "system", Now);
        Assert.Equal(0, generated);
    }

    [Fact]
    public async Task Resuming_a_paused_profile_makes_it_due_again()
    {
        var source = await PostSourceInvoice();
        var profile = await _recurring.CreateFromInvoiceAsync(
            source.Id, RecurringFrequency.Monthly, 1, new(2026, 6, 1), null, "tester", Now);
        await _recurring.PauseAsync(profile.Id);
        await _recurring.ResumeAsync(profile.Id);

        var generated = await _recurring.GenerateDueInvoicesAsync(new(2026, 6, 1), "system", Now);
        Assert.Equal(1, generated);
    }

    [Fact]
    public async Task DeleteAsync_removes_the_profile()
    {
        var source = await PostSourceInvoice();
        var profile = await _recurring.CreateFromInvoiceAsync(
            source.Id, RecurringFrequency.Monthly, 1, new(2026, 6, 1), null, "tester", Now);

        await _recurring.DeleteAsync(profile.Id);

        Assert.Null(await _recurring.GetByIdAsync(profile.Id));
    }
}
