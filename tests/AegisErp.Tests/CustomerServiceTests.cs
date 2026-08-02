using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

public class CustomerServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly CustomerService _customers;
    private readonly CustomFieldService _customFields;
    private readonly TagService _tags;
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    public CustomerServiceTests()
    {
        _customers = new CustomerService(_db);
        _customFields = new CustomFieldService(_db);
        _tags = new TagService(_db);
    }

    public void Dispose() => _db.Dispose();

    private static NewCustomerInput MinimalInput(string name = "Acme LLC") =>
        new(name, null, "AED", 0, 30, null, null, null, null);

    private static NewCustomerInput FullInput(string name = "Acme LLC") => new(
        name, "Corporate", "AED", 5000, 30, "100123456700003", "acme@example.com", "+971500000000", null,
        Salesperson: null, CustomerType: CustomerType.Business, Salutation: "Mr.", FirstName: "John", LastName: "Doe",
        CompanyName: "Acme LLC", DisplayNameArabic: "أكمي", CustomerLanguage: "English",
        WorkPhone: "+97140000000", Mobile: "+971500000001",
        TaxTreatment: AegisErp.Domain.TaxTreatment.VatRegistered, PlaceOfSupply: "Dubai",
        OpeningBalance: 1000, EnablePortal: true, Remarks: "VIP customer",
        Billing: new CustomerAddressInput("Accounts Team", "United Arab Emirates", "Street 1", "شارع 1",
            "Street 2", null, "Dubai", "Dubai", "12345", "+97140000001", "+97140000002"),
        Shipping: new CustomerAddressInput("Warehouse", "United Arab Emirates", "Industrial Area 1", null,
            null, null, "Sharjah", "Sharjah", null, null, null));

    [Fact]
    public async Task CreateAsync_persists_every_new_field()
    {
        var c = await _customers.CreateAsync(FullInput());

        var found = await _customers.GetByIdAsync(c.Id);
        Assert.NotNull(found);
        Assert.Equal(CustomerType.Business, found!.CustomerType);
        Assert.Equal("John", found.FirstName);
        Assert.Equal("Acme LLC", found.CompanyName);
        Assert.Equal("أكمي", found.DisplayNameArabic);
        Assert.Equal(AegisErp.Domain.TaxTreatment.VatRegistered, found.TaxTreatment);
        Assert.Equal("Dubai", found.PlaceOfSupply);
        Assert.Equal(1000m, found.OpeningBalance);
        Assert.True(found.EnablePortal);
        Assert.Equal("Street 1", found.BillingAddressLine1);
        Assert.Equal("شارع 1", found.BillingAddressLine1Arabic);
        Assert.Equal("Sharjah", found.ShippingCity);
    }

    [Fact]
    public async Task Opening_balance_never_creates_a_GL_voucher()
    {
        var c = await _customers.CreateAsync(FullInput());
        await using var db = _db.CreateDbContext();
        Assert.False(await db.JournalVouchers.AnyAsync());
        Assert.True(c.Id > 0);
    }

    [Fact]
    public async Task Negative_opening_balance_is_rejected()
    {
        var input = MinimalInput() with { OpeningBalance = -1 };
        await Assert.ThrowsAsync<PostingException>(() => _customers.CreateAsync(input));
    }

    [Fact]
    public async Task UpdateAsync_changes_fields_and_replaces_child_collections()
    {
        var c = await _customers.CreateAsync(MinimalInput(),
            contactPersons: new[] { new ContactPersonInput(null, "Old", null, null, null, null, null, null, false) });

        await _customers.UpdateAsync(c.Id, MinimalInput("Acme Renamed"),
            contactPersons: new[] { new ContactPersonInput(null, "New", null, null, null, null, null, null, true) });

        var found = await _customers.GetByIdAsync(c.Id);
        Assert.Equal("Acme Renamed", found!.Name);
        Assert.Single(found.ContactPersons);
        Assert.Equal("New", found.ContactPersons.Single().FirstName);
        Assert.True(found.ContactPersons.Single().IsPrimary);
    }

    [Fact]
    public async Task Contact_person_without_a_first_name_is_rejected()
    {
        var input = new[] { new ContactPersonInput(null, "", null, null, null, null, null, null, false) };
        await Assert.ThrowsAsync<PostingException>(() => _customers.CreateAsync(MinimalInput(), contactPersons: input));
    }

    [Fact]
    public async Task Only_one_primary_contact_person_is_allowed()
    {
        var input = new[]
        {
            new ContactPersonInput(null, "A", null, null, null, null, null, null, true),
            new ContactPersonInput(null, "B", null, null, null, null, null, null, true),
        };
        await Assert.ThrowsAsync<PostingException>(() => _customers.CreateAsync(MinimalInput(), contactPersons: input));
    }

    [Fact]
    public async Task Required_custom_field_must_be_answered()
    {
        var def = await _customFields.CreateDefinitionAsync(
            new NewCustomFieldDefinitionInput(CustomerService.Module, "Industry", CustomFieldType.Text, null, IsRequired: true, SortOrder: 0));

        await Assert.ThrowsAsync<PostingException>(() => _customers.CreateAsync(MinimalInput()));

        var c = await _customers.CreateAsync(MinimalInput(),
            customFieldValues: new[] { new CustomFieldValueInput(def.Id, "Retail") });
        var found = await _customers.GetByIdAsync(c.Id);
        Assert.Equal("Retail", found!.CustomFieldValues.Single().Value);
    }

    [Fact]
    public async Task Dropdown_custom_field_rejects_a_value_outside_its_options()
    {
        var def = await _customFields.CreateDefinitionAsync(
            new NewCustomFieldDefinitionInput(CustomerService.Module, "Tier", CustomFieldType.Dropdown, "Gold, Silver, Bronze", IsRequired: false, SortOrder: 0));

        await Assert.ThrowsAsync<PostingException>(() => _customers.CreateAsync(MinimalInput(),
            customFieldValues: new[] { new CustomFieldValueInput(def.Id, "Platinum") }));

        var c = await _customers.CreateAsync(MinimalInput(),
            customFieldValues: new[] { new CustomFieldValueInput(def.Id, "Gold") });
        var found = await _customers.GetByIdAsync(c.Id);
        Assert.Equal("Gold", found!.CustomFieldValues.Single().Value);
    }

    [Fact]
    public async Task Deactivated_custom_field_is_no_longer_required()
    {
        var def = await _customFields.CreateDefinitionAsync(
            new NewCustomFieldDefinitionInput(CustomerService.Module, "Industry", CustomFieldType.Text, null, IsRequired: true, SortOrder: 0));
        await _customFields.SetActiveAsync(def.Id, false);

        var c = await _customers.CreateAsync(MinimalInput());
        Assert.NotNull(c);
    }

    [Fact]
    public async Task Deleting_a_custom_field_with_recorded_values_is_rejected()
    {
        var def = await _customFields.CreateDefinitionAsync(
            new NewCustomFieldDefinitionInput(CustomerService.Module, "Industry", CustomFieldType.Text, null, IsRequired: false, SortOrder: 0));
        await _customers.CreateAsync(MinimalInput(), customFieldValues: new[] { new CustomFieldValueInput(def.Id, "Retail") });

        await Assert.ThrowsAsync<PostingException>(() => _customFields.DeleteAsync(def.Id));
    }

    [Fact]
    public async Task Only_one_tag_per_group_is_allowed()
    {
        var group = await _tags.CreateGroupAsync(new NewTagGroupInput(CustomerService.Module, "Region", 0));
        var tagA = await _tags.AddTagAsync(group.Id, new NewTagInput("North", 0));
        var tagB = await _tags.AddTagAsync(group.Id, new NewTagInput("South", 1));

        await Assert.ThrowsAsync<PostingException>(() => _customers.CreateAsync(MinimalInput(),
            tagIds: new[] { tagA.Id, tagB.Id }));

        var c = await _customers.CreateAsync(MinimalInput(), tagIds: new[] { tagA.Id });
        var found = await _customers.GetByIdAsync(c.Id);
        Assert.Equal(tagA.Id, found!.Tags.Single().TagId);
    }

    [Fact]
    public async Task Deleting_a_tag_group_with_recorded_selections_is_rejected()
    {
        var group = await _tags.CreateGroupAsync(new NewTagGroupInput(CustomerService.Module, "Region", 0));
        var tag = await _tags.AddTagAsync(group.Id, new NewTagInput("North", 0));
        await _customers.CreateAsync(MinimalInput(), tagIds: new[] { tag.Id });

        await Assert.ThrowsAsync<PostingException>(() => _tags.DeleteGroupAsync(group.Id));
        await Assert.ThrowsAsync<PostingException>(() => _tags.DeleteTagAsync(tag.Id));
    }

    [Fact]
    public async Task Document_can_be_added_downloaded_and_removed()
    {
        var c = await _customers.CreateAsync(MinimalInput());
        var bytes = new byte[] { 1, 2, 3 };

        var doc = await _customers.AddDocumentAsync(c.Id, "license.pdf", "application/pdf", bytes, Now);
        var docs = await _customers.GetDocumentsAsync(c.Id);
        Assert.Single(docs);
        Assert.Equal("license.pdf", docs[0].FileName);
        Assert.Equal(3, docs[0].SizeBytes);

        var full = await _customers.GetDocumentAsync(c.Id, doc.Id);
        Assert.NotNull(full);
        Assert.Equal(bytes, full!.Value.Data);

        await _customers.RemoveDocumentAsync(c.Id, doc.Id);
        Assert.Empty(await _customers.GetDocumentsAsync(c.Id));
    }

    [Fact]
    public async Task Document_over_the_size_limit_is_rejected()
    {
        var c = await _customers.CreateAsync(MinimalInput());
        var tooBig = new byte[CustomerService.MaxDocumentBytes + 1];
        await Assert.ThrowsAsync<PostingException>(() => _customers.AddDocumentAsync(c.Id, "big.pdf", "application/pdf", tooBig, Now));
    }

    [Fact]
    public async Task Document_count_is_capped_per_customer()
    {
        var c = await _customers.CreateAsync(MinimalInput());
        for (var i = 0; i < CustomerService.MaxDocumentsPerCustomer; i++)
            await _customers.AddDocumentAsync(c.Id, $"doc{i}.pdf", "application/pdf", new byte[] { 1 }, Now);

        await Assert.ThrowsAsync<PostingException>(() =>
            _customers.AddDocumentAsync(c.Id, "one-too-many.pdf", "application/pdf", new byte[] { 1 }, Now));
    }

    [Fact]
    public async Task Existing_positional_call_shape_still_works_unchanged()
    {
        // The pre-parity call site (Customers.razor's original 10-arg constructor call) must still compile and behave.
        var input = new NewCustomerInput("Legacy Co", "Corporate", "AED", 100, 30, "TRN1", "a@b.com", "12345", "Some St", "Alice");
        var c = await _customers.CreateAsync(input);
        Assert.Equal("Legacy Co", c.Name);
        Assert.Equal("Alice", c.Salesperson);
        Assert.Equal(CustomerType.Business, c.CustomerType); // default preserved
    }
}
