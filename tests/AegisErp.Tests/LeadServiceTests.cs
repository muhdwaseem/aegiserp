using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;

namespace AegisErp.Tests;

public class LeadServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly CustomerService _customers;
    private readonly LeadService _leads;
    private static readonly DateTime Now = new(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

    public LeadServiceTests()
    {
        _customers = new CustomerService(_db);
        _leads = new LeadService(_db, _customers);
    }

    public void Dispose() => _db.Dispose();

    private LeadInput Input(string name = "Prospective Client") =>
        new(name, "Acme FZE", "+971500000000", "lead@example.com", "Referral", 5000m, "Ali", null);

    [Fact]
    public async Task CreateAsync_defaults_to_New_stage()
    {
        var lead = await _leads.CreateAsync(Input(), "tester", Now);

        Assert.Equal(LeadStage.New, lead.Stage);
        Assert.Null(lead.ConvertedCustomerId);
    }

    [Fact]
    public async Task CreateAsync_rejects_negative_estimated_value()
    {
        await Assert.ThrowsAsync<PostingException>(() =>
            _leads.CreateAsync(Input() with { EstimatedValue = -1 }, "tester", Now));
    }

    [Fact]
    public async Task ChangeStageAsync_moves_through_the_pipeline_but_blocks_direct_move_to_won()
    {
        var lead = await _leads.CreateAsync(Input(), "tester", Now);

        await _leads.ChangeStageAsync(lead.Id, LeadStage.Contacted);
        var reloaded = await _leads.GetByIdAsync(lead.Id);
        Assert.Equal(LeadStage.Contacted, reloaded!.Stage);

        await Assert.ThrowsAsync<PostingException>(() => _leads.ChangeStageAsync(lead.Id, LeadStage.Won));
    }

    [Fact]
    public async Task ChangeStageAsync_to_Lost_locks_the_lead_from_further_changes()
    {
        var lead = await _leads.CreateAsync(Input(), "tester", Now);

        await _leads.ChangeStageAsync(lead.Id, LeadStage.Lost);

        await Assert.ThrowsAsync<PostingException>(() => _leads.ChangeStageAsync(lead.Id, LeadStage.Contacted));
        await Assert.ThrowsAsync<PostingException>(() => _leads.UpdateAsync(lead.Id, Input("Renamed")));
    }

    [Fact]
    public async Task LogActivityAsync_appends_to_the_timeline_and_updates_last_activity()
    {
        var lead = await _leads.CreateAsync(Input(), "tester", Now);

        await _leads.LogActivityAsync(lead.Id, LeadActivityType.Call, "Initial call", new(2026, 5, 20), "tester", Now);
        var later = Now.AddDays(1);
        await _leads.LogActivityAsync(lead.Id, LeadActivityType.Email, "Sent quote", new(2026, 5, 21), "tester", later);

        var reloaded = await _leads.GetByIdAsync(lead.Id);
        Assert.Equal(2, reloaded!.Activities.Count);
        Assert.Equal(later, reloaded.LastActivityAtUtc);
    }

    [Fact]
    public async Task GetAllAsync_filters_by_stage()
    {
        var a = await _leads.CreateAsync(Input("A"), "tester", Now);
        var b = await _leads.CreateAsync(Input("B"), "tester", Now);
        await _leads.ChangeStageAsync(b.Id, LeadStage.Contacted);

        var newOnly = await _leads.GetAllAsync(LeadStage.New);
        var contactedOnly = await _leads.GetAllAsync(LeadStage.Contacted);

        Assert.Equal(a.Id, Assert.Single(newOnly).Id);
        Assert.Equal(b.Id, Assert.Single(contactedOnly).Id);
    }

    [Fact]
    public async Task ConvertToCustomerAsync_creates_a_real_customer_and_links_back()
    {
        var lead = await _leads.CreateAsync(Input(), "tester", Now);

        var customer = await _leads.ConvertToCustomerAsync(lead.Id, "tester", Now);

        Assert.Equal("Acme FZE", customer.Name); // prefers CompanyName over the contact's own Name
        Assert.Equal("lead@example.com", customer.Email);

        var reloaded = await _leads.GetByIdAsync(lead.Id);
        Assert.Equal(LeadStage.Won, reloaded!.Stage);
        Assert.Equal(customer.Id, reloaded.ConvertedCustomerId);

        var customers = await _customers.GetAllAsync();
        Assert.Contains(customers, c => c.Id == customer.Id);
    }

    [Fact]
    public async Task ConvertToCustomerAsync_falls_back_to_the_contact_name_when_no_company_name()
    {
        var lead = await _leads.CreateAsync(Input() with { CompanyName = null }, "tester", Now);

        var customer = await _leads.ConvertToCustomerAsync(lead.Id, "tester", Now);

        Assert.Equal("Prospective Client", customer.Name);
    }

    [Fact]
    public async Task ConvertToCustomerAsync_rejects_a_lead_already_closed_or_converted()
    {
        var lost = await _leads.CreateAsync(Input(), "tester", Now);
        await _leads.ChangeStageAsync(lost.Id, LeadStage.Lost);
        await Assert.ThrowsAsync<PostingException>(() => _leads.ConvertToCustomerAsync(lost.Id, "tester", Now));

        var won = await _leads.CreateAsync(Input("Second Lead"), "tester", Now);
        await _leads.ConvertToCustomerAsync(won.Id, "tester", Now);
        await Assert.ThrowsAsync<PostingException>(() => _leads.ConvertToCustomerAsync(won.Id, "tester", Now));
    }

    [Fact]
    public async Task Leads_of_another_company_are_not_visible_or_editable()
    {
        _db.SeedOtherCompany();
        _db.SwitchTo(_db.OtherCompany.Id);
        var otherLeads = new LeadService(_db, new CustomerService(_db));
        var theirs = await otherLeads.CreateAsync(Input(), "tester", Now);
        _db.SwitchTo(_db.Company.Id);

        Assert.Empty(await _leads.GetAllAsync());
        await Assert.ThrowsAsync<PostingException>(() => _leads.ChangeStageAsync(theirs.Id, LeadStage.Contacted));
    }
}
