using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Tests;

public class VendorServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly VendorService _vendors;
    private readonly CustomFieldService _customFields;
    private readonly TagService _tags;
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    public VendorServiceTests()
    {
        _vendors = new VendorService(_db);
        _customFields = new CustomFieldService(_db);
        _tags = new TagService(_db);
    }

    public void Dispose() => _db.Dispose();

    private static NewVendorInput MinimalInput(string name = "Acme Supplies LLC") =>
        new(name, null, "AED", 0, 30, null, null, null, null);

    private static NewVendorInput FullInput(string name = "Acme Supplies LLC") => new(
        name, "Supplier", "AED", 5000, 30, "100123456700003", "acme@example.com", "+971500000000", null,
        VendorType: CustomerType.Business, Salutation: "Mr.", FirstName: "John", LastName: "Doe",
        CompanyName: "Acme Supplies LLC", DisplayNameArabic: "أكمي", VendorLanguage: "English",
        WorkPhone: "+97140000000", Mobile: "+971500000001",
        TaxTreatment: TaxTreatment.VatRegistered, PlaceOfSupply: "Dubai",
        OpeningBalance: 1000, Remarks: "Preferred supplier",
        Billing: new VendorAddressInput("Accounts Team", "United Arab Emirates", "Street 1", "شارع 1",
            "Street 2", null, "Dubai", "Dubai", "12345", "+97140000001", "+97140000002"),
        Shipping: new VendorAddressInput("Warehouse", "United Arab Emirates", "Industrial Area 1", null,
            null, null, "Sharjah", "Sharjah", null, null, null));

    [Fact]
    public async Task CreateAsync_persists_every_new_field()
    {
        var v = await _vendors.CreateAsync(FullInput());

        var found = await _vendors.GetByIdAsync(v.Id);
        Assert.NotNull(found);
        Assert.Equal(CustomerType.Business, found!.VendorType);
        Assert.Equal("John", found.FirstName);
        Assert.Equal("Acme Supplies LLC", found.CompanyName);
        Assert.Equal("أكمي", found.DisplayNameArabic);
        Assert.Equal(TaxTreatment.VatRegistered, found.TaxTreatment);
        Assert.Equal("Dubai", found.PlaceOfSupply);
        Assert.Equal(1000m, found.OpeningBalance);
        Assert.Equal(5000m, found.CreditLimit);
        Assert.Equal("Street 1", found.BillingAddressLine1);
        Assert.Equal("شارع 1", found.BillingAddressLine1Arabic);
        Assert.Equal("Sharjah", found.ShippingCity);
    }

    [Fact]
    public async Task Opening_balance_never_creates_a_GL_voucher()
    {
        var v = await _vendors.CreateAsync(FullInput());
        await using var db = _db.CreateDbContext();
        Assert.False(await db.JournalVouchers.AnyAsync());
        Assert.True(v.Id > 0);
    }

    [Fact]
    public async Task Negative_opening_balance_is_rejected()
    {
        var input = MinimalInput() with { OpeningBalance = -1 };
        await Assert.ThrowsAsync<PostingException>(() => _vendors.CreateAsync(input));
    }

    [Fact]
    public async Task Negative_credit_limit_is_rejected()
    {
        var input = MinimalInput() with { CreditLimit = -1 };
        await Assert.ThrowsAsync<PostingException>(() => _vendors.CreateAsync(input));
    }

    [Fact]
    public async Task UpdateAsync_changes_fields_and_replaces_child_collections()
    {
        var v = await _vendors.CreateAsync(MinimalInput(),
            contactPersons: new[] { new ContactPersonInput(null, "Old", null, null, null, null, null, null, false) });

        await _vendors.UpdateAsync(v.Id, MinimalInput("Acme Renamed"),
            contactPersons: new[] { new ContactPersonInput(null, "New", null, null, null, null, null, null, true) });

        var found = await _vendors.GetByIdAsync(v.Id);
        Assert.Equal("Acme Renamed", found!.Name);
        Assert.Single(found.ContactPersons);
        Assert.Equal("New", found.ContactPersons.Single().FirstName);
        Assert.True(found.ContactPersons.Single().IsPrimary);
    }

    [Fact]
    public async Task Contact_person_without_a_first_name_is_rejected()
    {
        var input = new[] { new ContactPersonInput(null, "", null, null, null, null, null, null, false) };
        await Assert.ThrowsAsync<PostingException>(() => _vendors.CreateAsync(MinimalInput(), contactPersons: input));
    }

    [Fact]
    public async Task Only_one_primary_contact_person_is_allowed()
    {
        var input = new[]
        {
            new ContactPersonInput(null, "A", null, null, null, null, null, null, true),
            new ContactPersonInput(null, "B", null, null, null, null, null, null, true),
        };
        await Assert.ThrowsAsync<PostingException>(() => _vendors.CreateAsync(MinimalInput(), contactPersons: input));
    }

    [Fact]
    public async Task Required_custom_field_must_be_answered()
    {
        var def = await _customFields.CreateDefinitionAsync(
            new NewCustomFieldDefinitionInput(VendorService.Module, "Category", CustomFieldType.Text, null, IsRequired: true, SortOrder: 0));

        await Assert.ThrowsAsync<PostingException>(() => _vendors.CreateAsync(MinimalInput()));

        var v = await _vendors.CreateAsync(MinimalInput(),
            customFieldValues: new[] { new CustomFieldValueInput(def.Id, "Raw Materials") });
        var found = await _vendors.GetByIdAsync(v.Id);
        Assert.Equal("Raw Materials", found!.CustomFieldValues.Single().Value);
    }

    [Fact]
    public async Task Dropdown_custom_field_rejects_a_value_outside_its_options()
    {
        var def = await _customFields.CreateDefinitionAsync(
            new NewCustomFieldDefinitionInput(VendorService.Module, "Tier", CustomFieldType.Dropdown, "Gold, Silver, Bronze", IsRequired: false, SortOrder: 0));

        await Assert.ThrowsAsync<PostingException>(() => _vendors.CreateAsync(MinimalInput(),
            customFieldValues: new[] { new CustomFieldValueInput(def.Id, "Platinum") }));

        var v = await _vendors.CreateAsync(MinimalInput(),
            customFieldValues: new[] { new CustomFieldValueInput(def.Id, "Gold") });
        var found = await _vendors.GetByIdAsync(v.Id);
        Assert.Equal("Gold", found!.CustomFieldValues.Single().Value);
    }

    [Fact]
    public async Task Deleting_a_custom_field_with_recorded_vendor_values_is_rejected()
    {
        var def = await _customFields.CreateDefinitionAsync(
            new NewCustomFieldDefinitionInput(VendorService.Module, "Category", CustomFieldType.Text, null, IsRequired: false, SortOrder: 0));
        await _vendors.CreateAsync(MinimalInput(), customFieldValues: new[] { new CustomFieldValueInput(def.Id, "Raw Materials") });

        await Assert.ThrowsAsync<PostingException>(() => _customFields.DeleteAsync(def.Id));
    }

    [Fact]
    public async Task Only_one_tag_per_group_is_allowed()
    {
        var group = await _tags.CreateGroupAsync(new NewTagGroupInput(VendorService.Module, "Region", 0));
        var tagA = await _tags.AddTagAsync(group.Id, new NewTagInput("North", 0));
        var tagB = await _tags.AddTagAsync(group.Id, new NewTagInput("South", 1));

        await Assert.ThrowsAsync<PostingException>(() => _vendors.CreateAsync(MinimalInput(),
            tagIds: new[] { tagA.Id, tagB.Id }));

        var v = await _vendors.CreateAsync(MinimalInput(), tagIds: new[] { tagA.Id });
        var found = await _vendors.GetByIdAsync(v.Id);
        Assert.Equal(tagA.Id, found!.Tags.Single().TagId);
    }

    [Fact]
    public async Task Deleting_a_tag_group_with_recorded_vendor_selections_is_rejected()
    {
        var group = await _tags.CreateGroupAsync(new NewTagGroupInput(VendorService.Module, "Region", 0));
        var tag = await _tags.AddTagAsync(group.Id, new NewTagInput("North", 0));
        await _vendors.CreateAsync(MinimalInput(), tagIds: new[] { tag.Id });

        await Assert.ThrowsAsync<PostingException>(() => _tags.DeleteGroupAsync(group.Id));
        await Assert.ThrowsAsync<PostingException>(() => _tags.DeleteTagAsync(tag.Id));
    }

    [Fact]
    public async Task Customer_and_vendor_custom_fields_do_not_interfere_with_each_other()
    {
        // Same label, different modules — a vendor's answer must never block deleting the customer
        // field (or vice versa), since ValidateAndBuildCustomFieldValuesAsync filters by Module.
        var customerDef = await _customFields.CreateDefinitionAsync(
            new NewCustomFieldDefinitionInput(CustomerService.Module, "Category", CustomFieldType.Text, null, false, 0));
        var vendorDef = await _customFields.CreateDefinitionAsync(
            new NewCustomFieldDefinitionInput(VendorService.Module, "Category", CustomFieldType.Text, null, false, 0));

        await _vendors.CreateAsync(MinimalInput(), customFieldValues: new[] { new CustomFieldValueInput(vendorDef.Id, "Raw Materials") });

        // The customer-module field has no recorded values anywhere — deleting it is fine.
        await _customFields.DeleteAsync(customerDef.Id);
        // The vendor-module field does — deleting it must still be rejected.
        await Assert.ThrowsAsync<PostingException>(() => _customFields.DeleteAsync(vendorDef.Id));
    }

    [Fact]
    public async Task Document_can_be_added_downloaded_and_removed()
    {
        var v = await _vendors.CreateAsync(MinimalInput());
        var bytes = new byte[] { 1, 2, 3 };

        var doc = await _vendors.AddDocumentAsync(v.Id, "license.pdf", "application/pdf", bytes, Now);
        var docs = await _vendors.GetDocumentsAsync(v.Id);
        Assert.Single(docs);
        Assert.Equal("license.pdf", docs[0].FileName);
        Assert.Equal(3, docs[0].SizeBytes);

        var full = await _vendors.GetDocumentAsync(v.Id, doc.Id);
        Assert.NotNull(full);
        Assert.Equal(bytes, full!.Value.Data);

        await _vendors.RemoveDocumentAsync(v.Id, doc.Id);
        Assert.Empty(await _vendors.GetDocumentsAsync(v.Id));
    }

    [Fact]
    public async Task Document_over_the_size_limit_is_rejected()
    {
        var v = await _vendors.CreateAsync(MinimalInput());
        var tooBig = new byte[VendorService.MaxDocumentBytes + 1];
        await Assert.ThrowsAsync<PostingException>(() => _vendors.AddDocumentAsync(v.Id, "big.pdf", "application/pdf", tooBig, Now));
    }

    [Fact]
    public async Task Document_count_is_capped_per_vendor()
    {
        var v = await _vendors.CreateAsync(MinimalInput());
        for (var i = 0; i < VendorService.MaxDocumentsPerVendor; i++)
            await _vendors.AddDocumentAsync(v.Id, $"doc{i}.pdf", "application/pdf", new byte[] { 1 }, Now);

        await Assert.ThrowsAsync<PostingException>(() =>
            _vendors.AddDocumentAsync(v.Id, "one-too-many.pdf", "application/pdf", new byte[] { 1 }, Now));
    }
}
