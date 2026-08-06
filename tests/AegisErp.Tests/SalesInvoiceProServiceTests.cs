using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AegisErp.Tests;

/// <summary>PRO Service Mode fields on Sales Invoice — mirrors <see cref="EstimateServiceTests"/>.</summary>
public class SalesInvoiceProServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly SalesInvoiceService _invoices;
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    public SalesInvoiceProServiceTests() =>
        _invoices = new SalesInvoiceService(_db, new EmailService(Options.Create(new SmtpOptions())));

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Standard_shape_invoice_has_unchanged_Net_Vat_Gross()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Consulting", _db.Revenue.Id, null, 2, 500, 0.05m) }, Now);

        var line = inv.Lines.Single();
        Assert.Equal(1000m, line.Net);
        Assert.Equal(50m, line.Vat);
        Assert.Equal(1050m, line.Gross);
    }

    [Fact]
    public async Task PRO_fields_add_non_taxable_amounts_without_charging_VAT_on_them()
    {
        var inv = await _invoices.CreateAndPostAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Trade License", _db.Revenue.Id, null, 1, 1000, 0.05m,
                GovtFee: 5000, BankCharge: 50, AssignedTo: "Ali") }, Now);

        var line = inv.Lines.Single();
        Assert.Equal(50m, line.Vat);
        Assert.Equal(1000m + 5000m + 50m, line.Net);
        Assert.Equal(line.Net + line.Vat, line.Gross);
        Assert.Equal("Ali", line.AssignedTo);
    }

    [Fact]
    public async Task Header_snapshot_fields_are_persisted_through_draft_and_post()
    {
        var org = new CustomerOrganization { CustomerId = _db.Customer.Id, Name = "Acme FZE", Trn = "100111111100003" };
        using (var db = _db.CreateUnscopedDbContext())
        {
            db.CustomerOrganizations.Add(org);
            db.SaveChanges();
        }

        var draft = await _invoices.CreateDraftAsync(_db.Customer.Id, new(2026, 5, 10), _db.May.Id, null, "tester",
            new[] { new InvoiceLineInput("Trade License", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now,
            organizationId: org.Id, contactMobile: "+971500000000", contactEmail: "x@example.com",
            contactTrn: "100111111100003", contactPerson: "John", billingAddress: "Dubai");

        Assert.Equal(org.Id, draft.OrganizationId);
        Assert.Equal("+971500000000", draft.ContactMobile);
        Assert.Equal("100111111100003", draft.ContactTrn);
        Assert.Equal("John", draft.ContactPerson);
        Assert.Equal("Dubai", draft.BillingAddressSnapshot);

        var posted = await _invoices.PostDraftAsync(draft.Id, "tester", Now);
        Assert.Equal(org.Id, posted.OrganizationId);
        Assert.Equal("100111111100003", posted.ContactTrn);
    }
}
