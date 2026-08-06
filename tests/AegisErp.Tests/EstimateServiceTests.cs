using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Services;
using Microsoft.Extensions.Options;

namespace AegisErp.Tests;

public class EstimateServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly EstimateService _estimates;
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    public EstimateServiceTests()
    {
        var invoices = new SalesInvoiceService(_db, new EmailService(Options.Create(new SmtpOptions())));
        _estimates = new EstimateService(_db, invoices);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Standard_shape_estimate_has_unchanged_Net_Vat_Gross()
    {
        var est = await _estimates.CreateAsync(_db.Customer.Id, new(2026, 5, 1), new(2026, 5, 31), null,
            "tester", new[] { new EstimateLineInput("Consulting", _db.Revenue.Id, null, 2, 500, 0.05m) }, Now);

        var line = est.Lines.Single();
        Assert.Equal(1000m, line.Net);
        Assert.Equal(50m, line.Vat);
        Assert.Equal(1050m, line.Gross);
    }

    [Fact]
    public async Task PRO_fields_add_non_taxable_amounts_without_charging_VAT_on_them()
    {
        var est = await _estimates.CreateAsync(_db.Customer.Id, new(2026, 5, 1), new(2026, 5, 31), null,
            "tester",
            new[] { new EstimateLineInput("Trade License", _db.Revenue.Id, null, 1, 1000, 0.05m, GovtFee: 5000, BankCharge: 50, AssignedTo: "Ali") },
            Now);

        var line = est.Lines.Single();
        // Taxable base is Center Fee (UnitPrice) only — VAT must not apply to Govt Fee/Bank Charge.
        Assert.Equal(50m, line.Vat);
        Assert.Equal(1000m + 5000m + 50m, line.Net);
        Assert.Equal(line.Net + line.Vat, line.Gross);
        Assert.Equal("Ali", line.AssignedTo);
    }

    [Fact]
    public async Task Header_snapshot_fields_are_persisted()
    {
        var org = new CustomerOrganization { CustomerId = _db.Customer.Id, Name = "Acme FZE", Trn = "100111111100003" };
        using (var db = _db.CreateUnscopedDbContext())
        {
            db.CustomerOrganizations.Add(org);
            db.SaveChanges();
        }

        var est = await _estimates.CreateAsync(_db.Customer.Id, new(2026, 5, 1), new(2026, 5, 31), null,
            "tester", new[] { new EstimateLineInput("Trade License", _db.Revenue.Id, null, 1, 1000, 0.05m) }, Now,
            organizationId: org.Id, contactMobile: "+971500000000", contactEmail: "x@example.com",
            contactTrn: "100111111100003", contactPerson: "John", billingAddress: "Dubai",
            jobRequest: "Visa Renewal — Ahmed");

        var found = await _estimates.GetByIdAsync(est.Id);
        Assert.Equal(org.Id, found!.OrganizationId);
        Assert.Equal("+971500000000", found.ContactMobile);
        Assert.Equal("100111111100003", found.ContactTrn);
        Assert.Equal("John", found.ContactPerson);
        Assert.Equal("Dubai", found.BillingAddressSnapshot);
        Assert.Equal("Visa Renewal — Ahmed", found.JobRequest);
    }

    [Fact]
    public async Task ConvertToInvoiceAsync_carries_PRO_fields_through_without_overcharging_VAT()
    {
        var org = new CustomerOrganization { CustomerId = _db.Customer.Id, Name = "Acme FZE", Trn = "100111111100003" };
        using (var db = _db.CreateUnscopedDbContext())
        {
            db.CustomerOrganizations.Add(org);
            db.SaveChanges();
        }

        var est = await _estimates.CreateAsync(_db.Customer.Id, new(2026, 5, 1), new(2026, 5, 31), null,
            "tester",
            new[] { new EstimateLineInput("Trade License", _db.Revenue.Id, null, 1, 1000, 0.05m, GovtFee: 5000, BankCharge: 50, AssignedTo: "Ali") },
            Now, organizationId: org.Id, contactMobile: "+971500000000", contactEmail: "x@example.com",
            contactTrn: "100111111100003", contactPerson: "John", billingAddress: "Dubai",
            jobRequest: "Visa Renewal — Ahmed");

        var invoice = await _estimates.ConvertToInvoiceAsync(est.Id, _db.May.Id, "tester", new DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc));

        var line = invoice.Lines.Single();
        Assert.Equal(5000m, line.GovtFee);
        Assert.Equal(50m, line.BankCharge);
        Assert.Equal("Ali", line.AssignedTo);
        // VAT must still apply only to the Center Fee (UnitPrice), not the Govt Fee/Bank Charge disbursements.
        Assert.Equal(50m, line.Vat);
        Assert.Equal(1000m + 5000m + 50m, line.Net);

        Assert.Equal(org.Id, invoice.OrganizationId);
        Assert.Equal("100111111100003", invoice.ContactTrn);
        Assert.Equal("John", invoice.ContactPerson);
        Assert.Equal("Dubai", invoice.BillingAddressSnapshot);
        Assert.Equal("Visa Renewal — Ahmed", invoice.JobRequest);
    }
}
