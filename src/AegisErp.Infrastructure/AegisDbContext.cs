using AegisErp.Domain;
using AegisErp.Domain.Entities;
using AegisErp.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure;

public class AegisDbContext : IdentityDbContext<AppUser>
{
    /// <summary>
    /// Creates the context. When <paramref name="current"/> is supplied (the normal path, injected by
    /// the scoped factory) every query is filtered to that company; omitting it yields an unscoped
    /// context that sees all companies, used for seeding and firm-wide administration.
    /// </summary>
    /// <remarks>
    /// Deliberately a single constructor — EF's <c>IDbContextFactory</c> uses ActivatorUtilities,
    /// which throws on two constructors that both accept <c>DbContextOptions</c>.
    /// </remarks>
    public AegisDbContext(DbContextOptions<AegisDbContext> options, ICurrentCompany? current = null)
        : base(options)
        => CurrentCompanyId = current?.CompanyId;

    /// <summary>
    /// Active company for the global query filters, or null to disable filtering. Referenced by the
    /// filter expressions below, so EF re-evaluates it on every query rather than baking it into the model.
    /// </summary>
    /// <remarks>
    /// Settable within the assembly so <see cref="SeedData"/> can switch an unscoped context to a
    /// company while it builds that company's books; application code never assigns it.
    /// </remarks>
    public int? CurrentCompanyId { get; internal set; }

    public DbSet<UserCompanyAccess> UserCompanyAccess => Set<UserCompanyAccess>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();
    public DbSet<FiscalPeriod> FiscalPeriods => Set<FiscalPeriod>();
    public DbSet<JournalVoucher> JournalVouchers => Set<JournalVoucher>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<SalesInvoice> SalesInvoices => Set<SalesInvoice>();
    public DbSet<SalesInvoiceLine> SalesInvoiceLines => Set<SalesInvoiceLine>();
    public DbSet<CustomerReceipt> CustomerReceipts => Set<CustomerReceipt>();
    public DbSet<ReceiptAllocation> ReceiptAllocations => Set<ReceiptAllocation>();
    public DbSet<CreditNote> CreditNotes => Set<CreditNote>();
    public DbSet<CreditNoteLine> CreditNoteLines => Set<CreditNoteLine>();
    public DbSet<CreditNoteAllocation> CreditNoteAllocations => Set<CreditNoteAllocation>();
    public DbSet<Estimate> Estimates => Set<Estimate>();
    public DbSet<EstimateLine> EstimateLines => Set<EstimateLine>();
    public DbSet<DeliveryNote> DeliveryNotes => Set<DeliveryNote>();
    public DbSet<DeliveryNoteLine> DeliveryNoteLines => Set<DeliveryNoteLine>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();
    public DbSet<PurchaseInvoiceLine> PurchaseInvoiceLines => Set<PurchaseInvoiceLine>();
    public DbSet<VendorPayment> VendorPayments => Set<VendorPayment>();
    public DbSet<VendorPaymentAllocation> VendorPaymentAllocations => Set<VendorPaymentAllocation>();
    public DbSet<DebitNote> DebitNotes => Set<DebitNote>();
    public DbSet<DebitNoteLine> DebitNoteLines => Set<DebitNoteLine>();
    public DbSet<DirectExpense> DirectExpenses => Set<DirectExpense>();
    public DbSet<DirectExpenseLine> DirectExpenseLines => Set<DirectExpenseLine>();
    public DbSet<CompanySetup> CompanySetups => Set<CompanySetup>();
    public DbSet<CompanyBankAccount> CompanyBankAccounts => Set<CompanyBankAccount>();
    public DbSet<Salesperson> Salespersons => Set<Salesperson>();
    public DbSet<CompanySetupDocument> CompanySetupDocuments => Set<CompanySetupDocument>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<TaxCode> TaxCodes => Set<TaxCode>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<CustomerContactPerson> CustomerContactPersons => Set<CustomerContactPerson>();
    public DbSet<CustomerDocument> CustomerDocuments => Set<CustomerDocument>();
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<CustomerCustomFieldValue> CustomerCustomFieldValues => Set<CustomerCustomFieldValue>();
    public DbSet<TagGroup> TagGroups => Set<TagGroup>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<CustomerTag> CustomerTags => Set<CustomerTag>();
    public DbSet<VendorContactPerson> VendorContactPersons => Set<VendorContactPerson>();
    public DbSet<VendorDocument> VendorDocuments => Set<VendorDocument>();
    public DbSet<VendorCustomFieldValue> VendorCustomFieldValues => Set<VendorCustomFieldValue>();
    public DbSet<VendorTag> VendorTags => Set<VendorTag>();
    public DbSet<SalespersonAssignmentHistory> SalespersonAssignmentHistories => Set<SalespersonAssignmentHistory>();
    public DbSet<InvoiceReminderLog> InvoiceReminderLogs => Set<InvoiceReminderLog>();
    public DbSet<RecurringInvoiceProfile> RecurringInvoiceProfiles => Set<RecurringInvoiceProfile>();
    public DbSet<RecurringInvoiceProfileLine> RecurringInvoiceProfileLines => Set<RecurringInvoiceProfileLine>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b); // Identity tables

        b.Entity<Account>(e =>
        {
            e.HasIndex(a => new { a.CompanyId, a.Code }).IsUnique();
            e.Property(a => a.Code).HasMaxLength(20).IsRequired();
            e.Property(a => a.Name).HasMaxLength(120).IsRequired();
            e.Property(a => a.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.PnlSection).HasConversion<string>().HasMaxLength(24);
            e.Property(a => a.Category).HasMaxLength(60);
            e.Property(a => a.Currency).HasMaxLength(3).IsRequired();
            e.Property(a => a.Description).HasMaxLength(300);
            e.Property(a => a.UpdatedBy).HasMaxLength(80);
            e.Property(a => a.RowVersion).IsConcurrencyToken();
            e.Ignore(a => a.NormalBalance);
            e.HasOne(a => a.Parent).WithMany().HasForeignKey(a => a.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CostCenter>(e =>
        {
            e.HasIndex(c => new { c.CompanyId, c.Code }).IsUnique();
            e.Property(c => c.Code).HasMaxLength(20).IsRequired();
            e.Property(c => c.Name).HasMaxLength(120).IsRequired();
        });

        b.Entity<FiscalPeriod>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(40).IsRequired();
        });

        b.Entity<JournalVoucher>(e =>
        {
            e.HasIndex(v => new { v.CompanyId, v.VoucherNo }).IsUnique();
            e.Property(v => v.VoucherNo).HasMaxLength(30).IsRequired();
            e.Property(v => v.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.Narration).HasMaxLength(400);
            e.Property(v => v.Reference).HasMaxLength(60);
            e.Property(v => v.CreatedBy).HasMaxLength(80);
            e.Ignore(v => v.TotalDebit);
            e.Ignore(v => v.TotalCredit);
            e.Ignore(v => v.Difference);
            e.Ignore(v => v.IsBalanced);
            e.HasOne(v => v.FiscalPeriod).WithMany().HasForeignKey(v => v.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(v => v.Lines).WithOne(l => l.JournalVoucher).HasForeignKey(l => l.JournalVoucherId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<JournalLine>(e =>
        {
            e.Property(l => l.Debit).HasPrecision(18, 2);
            e.Property(l => l.Credit).HasPrecision(18, 2);
            e.Property(l => l.Description).HasMaxLength(300);
            e.Ignore(l => l.Signed);
            e.HasOne(l => l.Account).WithMany(a => a.Lines).HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.CostCenter).WithMany().HasForeignKey(l => l.CostCenterId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Customer>(e =>
        {
            e.HasIndex(c => new { c.CompanyId, c.Code }).IsUnique();
            e.Property(c => c.Code).HasMaxLength(20).IsRequired();
            e.Property(c => c.Name).HasMaxLength(160).IsRequired();
            e.Property(c => c.Trn).HasMaxLength(20);
            e.Property(c => c.Email).HasMaxLength(160);
            e.Property(c => c.Phone).HasMaxLength(40);
            e.Property(c => c.Address).HasMaxLength(300);
            e.Property(c => c.Group).HasMaxLength(40);
            e.Property(c => c.Currency).HasMaxLength(3).IsRequired();
            e.Property(c => c.CreditLimit).HasPrecision(18, 2);
            e.Property(c => c.Salesperson).HasMaxLength(100);

            e.Property(c => c.CustomerType).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Salutation).HasMaxLength(20);
            e.Property(c => c.FirstName).HasMaxLength(80);
            e.Property(c => c.LastName).HasMaxLength(80);
            e.Property(c => c.CompanyName).HasMaxLength(160);
            e.Property(c => c.DisplayNameArabic).HasMaxLength(160);
            e.Property(c => c.CustomerLanguage).HasMaxLength(40).IsRequired();
            e.Property(c => c.WorkPhone).HasMaxLength(40);
            e.Property(c => c.Mobile).HasMaxLength(40);

            e.Property(c => c.TaxTreatment).HasConversion<string>().HasMaxLength(40);
            e.Property(c => c.PlaceOfSupply).HasMaxLength(40);
            e.Property(c => c.OpeningBalance).HasPrecision(18, 2);
            e.Property(c => c.Remarks).HasMaxLength(1000);

            e.Property(c => c.BillingAttention).HasMaxLength(160);
            e.Property(c => c.BillingCountry).HasMaxLength(80);
            e.Property(c => c.BillingAddressLine1).HasMaxLength(200);
            e.Property(c => c.BillingAddressLine1Arabic).HasMaxLength(200);
            e.Property(c => c.BillingAddressLine2).HasMaxLength(200);
            e.Property(c => c.BillingAddressLine2Arabic).HasMaxLength(200);
            e.Property(c => c.BillingCity).HasMaxLength(80);
            e.Property(c => c.BillingEmirate).HasMaxLength(40);
            e.Property(c => c.BillingZip).HasMaxLength(20);
            e.Property(c => c.BillingPhone).HasMaxLength(40);
            e.Property(c => c.BillingFax).HasMaxLength(40);

            e.Property(c => c.ShippingAttention).HasMaxLength(160);
            e.Property(c => c.ShippingCountry).HasMaxLength(80);
            e.Property(c => c.ShippingAddressLine1).HasMaxLength(200);
            e.Property(c => c.ShippingAddressLine1Arabic).HasMaxLength(200);
            e.Property(c => c.ShippingAddressLine2).HasMaxLength(200);
            e.Property(c => c.ShippingAddressLine2Arabic).HasMaxLength(200);
            e.Property(c => c.ShippingCity).HasMaxLength(80);
            e.Property(c => c.ShippingEmirate).HasMaxLength(40);
            e.Property(c => c.ShippingZip).HasMaxLength(20);
            e.Property(c => c.ShippingPhone).HasMaxLength(40);
            e.Property(c => c.ShippingFax).HasMaxLength(40);

            e.HasMany(c => c.ContactPersons).WithOne(p => p.Customer).HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Documents).WithOne(d => d.Customer).HasForeignKey(d => d.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.CustomFieldValues).WithOne(v => v.Customer).HasForeignKey(v => v.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Tags).WithOne(t => t.Customer).HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.SalespersonHistory).WithOne(h => h.Customer).HasForeignKey(h => h.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CustomerContactPerson>(e =>
        {
            e.Property(p => p.Salutation).HasMaxLength(20);
            e.Property(p => p.FirstName).HasMaxLength(80).IsRequired();
            e.Property(p => p.LastName).HasMaxLength(80);
            e.Property(p => p.Email).HasMaxLength(160);
            e.Property(p => p.WorkPhone).HasMaxLength(40);
            e.Property(p => p.Mobile).HasMaxLength(40);
            e.Property(p => p.Designation).HasMaxLength(100);
            e.Property(p => p.Department).HasMaxLength(100);
        });

        b.Entity<CustomerDocument>(e =>
        {
            e.Property(d => d.FileName).HasMaxLength(260).IsRequired();
            e.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
        });

        b.Entity<CustomFieldDefinition>(e =>
        {
            e.Property(f => f.Module).HasMaxLength(40).IsRequired();
            e.Property(f => f.Label).HasMaxLength(80).IsRequired();
            e.Property(f => f.FieldType).HasConversion<string>().HasMaxLength(20);
            e.Property(f => f.DropdownOptionsCsv).HasMaxLength(1000);
        });

        b.Entity<CustomerCustomFieldValue>(e =>
        {
            e.Property(v => v.Value).HasMaxLength(1000);
            e.HasOne(v => v.CustomFieldDefinition).WithMany().HasForeignKey(v => v.CustomFieldDefinitionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TagGroup>(e =>
        {
            e.Property(g => g.Module).HasMaxLength(40).IsRequired();
            e.Property(g => g.Name).HasMaxLength(80).IsRequired();
            e.HasMany(g => g.Tags).WithOne(t => t.TagGroup).HasForeignKey(t => t.TagGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Tag>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(80).IsRequired();
        });

        b.Entity<CustomerTag>(e =>
        {
            e.HasOne(t => t.Tag).WithMany().HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SalespersonAssignmentHistory>(e =>
        {
            e.Property(h => h.PreviousSalesperson).HasMaxLength(100);
            e.Property(h => h.NewSalesperson).HasMaxLength(100);
            e.Property(h => h.ChangedBy).HasMaxLength(80);
        });

        b.Entity<SalesInvoice>(e =>
        {
            e.HasIndex(i => new { i.CompanyId, i.InvoiceNo }).IsUnique();
            e.Property(i => i.InvoiceNo).HasMaxLength(30).IsRequired();
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.Narration).HasMaxLength(400);
            e.Property(i => i.CreatedBy).HasMaxLength(80);
            e.Property(i => i.PostedBy).HasMaxLength(80);
            e.Property(i => i.VoidedBy).HasMaxLength(80);
            e.Property(i => i.CustomerPoNo).HasMaxLength(60);
            e.Property(i => i.DeliveryNoteRef).HasMaxLength(60);
            e.Property(i => i.SalesOrderRef).HasMaxLength(60);
            e.Property(i => i.Notes).HasMaxLength(1000);
            e.Property(i => i.Subject).HasMaxLength(200);
            e.Property(i => i.TermsAndConditions).HasMaxLength(2000);
            e.Property(i => i.Salesperson).HasMaxLength(120);
            e.Property(i => i.ApprovalStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.SubmittedForApprovalBy).HasMaxLength(80);
            e.Property(i => i.ApprovalDecisionBy).HasMaxLength(80);
            e.Property(i => i.ApprovalNote).HasMaxLength(500);
            e.Property(i => i.AttachmentFileName).HasMaxLength(260);
            e.Property(i => i.AttachmentContentType).HasMaxLength(100);
            e.Property(i => i.LockedBy).HasMaxLength(80);
            e.Property(i => i.ShareToken).HasMaxLength(40);
            e.HasIndex(i => i.ShareToken).IsUnique(); // multiple NULLs are fine — only generated tokens must be unique
            e.Ignore(i => i.TotalNet);
            e.Ignore(i => i.TotalVat);
            e.Ignore(i => i.TotalGross);
            e.HasOne(i => i.Customer).WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.FiscalPeriod).WithMany().HasForeignKey(i => i.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.JournalVoucher).WithMany().HasForeignKey(i => i.JournalVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(i => i.Lines).WithOne(l => l.SalesInvoice).HasForeignKey(l => l.SalesInvoiceId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(i => i.ReminderLogs).WithOne(r => r.SalesInvoice).HasForeignKey(r => r.SalesInvoiceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InvoiceReminderLog>(e =>
        {
            e.Property(r => r.SentBy).HasMaxLength(80);
        });

        b.Entity<RecurringInvoiceProfile>(e =>
        {
            e.Property(p => p.Frequency).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Narration).HasMaxLength(400);
            e.Property(p => p.CreatedBy).HasMaxLength(80);
            e.HasOne(p => p.Customer).WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(p => p.Lines).WithOne(l => l.RecurringInvoiceProfile).HasForeignKey(l => l.RecurringInvoiceProfileId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RecurringInvoiceProfileLine>(e =>
        {
            e.Property(l => l.Description).HasMaxLength(300).IsRequired();
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 4);
            e.HasOne(l => l.RevenueAccount).WithMany().HasForeignKey(l => l.RevenueAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.CostCenter).WithMany().HasForeignKey(l => l.CostCenterId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SalesInvoiceLine>(e =>
        {
            e.Property(l => l.Description).HasMaxLength(300).IsRequired();
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 4);
            e.Property(l => l.DiscountValue).HasPrecision(18, 2);
            e.Property(l => l.DiscountType).HasConversion<string>().HasMaxLength(20);
            e.Property(l => l.Uom).HasMaxLength(20);
            e.Property(l => l.AttachmentFileName).HasMaxLength(260);
            e.Property(l => l.AttachmentContentType).HasMaxLength(100);
            e.Property(l => l.Recognition).HasConversion<string>().HasMaxLength(20);
            e.Property(l => l.CompletedBy).HasMaxLength(80);
            e.Ignore(l => l.Net);
            e.Ignore(l => l.Vat);
            e.Ignore(l => l.Gross);
            e.HasOne(l => l.RevenueAccount).WithMany().HasForeignKey(l => l.RevenueAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.CostCenter).WithMany().HasForeignKey(l => l.CostCenterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Item).WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Supplier).WithMany().HasForeignKey(l => l.SupplierId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CustomerReceipt>(e =>
        {
            e.HasIndex(r => new { r.CompanyId, r.ReceiptNo }).IsUnique();
            e.Property(r => r.ReceiptNo).HasMaxLength(30).IsRequired();
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.PaymentMode).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Amount).HasPrecision(18, 2);
            e.Property(r => r.Narration).HasMaxLength(400);
            e.Property(r => r.CreatedBy).HasMaxLength(80);
            e.Property(r => r.ReferenceNo).HasMaxLength(60);
            e.Property(r => r.AttachmentFileName).HasMaxLength(260);
            e.Property(r => r.AttachmentContentType).HasMaxLength(100);
            e.HasOne(r => r.Customer).WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.SalesInvoice).WithMany().HasForeignKey(r => r.SalesInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.FiscalPeriod).WithMany().HasForeignKey(r => r.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.BankAccount).WithMany().HasForeignKey(r => r.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.JournalVoucher).WithMany().HasForeignKey(r => r.JournalVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(r => r.Allocations).WithOne(a => a.CustomerReceipt).HasForeignKey(a => a.CustomerReceiptId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<ReceiptAllocation>(e =>
        {
            e.Property(a => a.Amount).HasPrecision(18, 2);
            e.HasOne(a => a.SalesInvoiceLine).WithMany().HasForeignKey(a => a.SalesInvoiceLineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CreditNote>(e =>
        {
            e.HasIndex(n => new { n.CompanyId, n.CreditNoteNo }).IsUnique();
            e.Property(n => n.CreditNoteNo).HasMaxLength(30).IsRequired();
            e.Property(n => n.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(n => n.Reason).HasMaxLength(120);
            e.Property(n => n.Narration).HasMaxLength(400);
            e.Property(n => n.CreatedBy).HasMaxLength(80);
            e.Property(n => n.SettlementMethod).HasConversion<string>().HasMaxLength(20);
            e.Ignore(n => n.TotalNet);
            e.Ignore(n => n.TotalVat);
            e.Ignore(n => n.TotalGross);
            e.HasOne(n => n.Customer).WithMany().HasForeignKey(n => n.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.SalesInvoice).WithMany().HasForeignKey(n => n.SalesInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.FiscalPeriod).WithMany().HasForeignKey(n => n.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.JournalVoucher).WithMany().HasForeignKey(n => n.JournalVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.BankAccount).WithMany().HasForeignKey(n => n.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(n => n.Lines).WithOne(l => l.CreditNote).HasForeignKey(l => l.CreditNoteId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(n => n.Allocations).WithOne(a => a.CreditNote).HasForeignKey(a => a.CreditNoteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CreditNoteLine>(e =>
        {
            e.Property(l => l.Description).HasMaxLength(300).IsRequired();
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 4);
            e.Ignore(l => l.Net);
            e.Ignore(l => l.Vat);
            e.Ignore(l => l.Gross);
            e.HasOne(l => l.RevenueAccount).WithMany().HasForeignKey(l => l.RevenueAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.CostCenter).WithMany().HasForeignKey(l => l.CostCenterId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CreditNoteAllocation>(e =>
        {
            e.Property(a => a.Amount).HasPrecision(18, 2);
            e.Property(a => a.AllocatedBy).HasMaxLength(80);
            e.HasOne(a => a.SalesInvoice).WithMany().HasForeignKey(a => a.SalesInvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Estimate>(e =>
        {
            e.HasIndex(n => new { n.CompanyId, n.EstimateNo }).IsUnique();
            e.Property(n => n.EstimateNo).HasMaxLength(30).IsRequired();
            e.Property(n => n.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(n => n.Narration).HasMaxLength(400);
            e.Property(n => n.CreatedBy).HasMaxLength(80);
            e.Ignore(n => n.TotalNet);
            e.Ignore(n => n.TotalVat);
            e.Ignore(n => n.TotalGross);
            e.HasOne(n => n.Customer).WithMany().HasForeignKey(n => n.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.ConvertedInvoice).WithMany().HasForeignKey(n => n.ConvertedInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(n => n.Lines).WithOne(l => l.Estimate).HasForeignKey(l => l.EstimateId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EstimateLine>(e =>
        {
            e.Property(l => l.Description).HasMaxLength(300).IsRequired();
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 4);
            e.Ignore(l => l.Net);
            e.Ignore(l => l.Vat);
            e.Ignore(l => l.Gross);
            e.HasOne(l => l.RevenueAccount).WithMany().HasForeignKey(l => l.RevenueAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.CostCenter).WithMany().HasForeignKey(l => l.CostCenterId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DeliveryNote>(e =>
        {
            e.HasIndex(n => new { n.CompanyId, n.DeliveryNoteNo }).IsUnique();
            e.Property(n => n.DeliveryNoteNo).HasMaxLength(30).IsRequired();
            e.Property(n => n.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(n => n.DeliveryAddress).HasMaxLength(300);
            e.Property(n => n.Narration).HasMaxLength(400);
            e.Property(n => n.CreatedBy).HasMaxLength(80);
            e.Ignore(n => n.TotalQuantity);
            e.HasOne(n => n.Customer).WithMany().HasForeignKey(n => n.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.SalesInvoice).WithMany().HasForeignKey(n => n.SalesInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(n => n.Lines).WithOne(l => l.DeliveryNote).HasForeignKey(l => l.DeliveryNoteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeliveryNoteLine>(e =>
        {
            e.Property(l => l.Description).HasMaxLength(300).IsRequired();
            e.Property(l => l.Unit).HasMaxLength(20);
            e.Property(l => l.Quantity).HasPrecision(18, 3);
        });

        b.Entity<Vendor>(e =>
        {
            e.HasIndex(v => new { v.CompanyId, v.Code }).IsUnique();
            e.Property(v => v.Code).HasMaxLength(20).IsRequired();
            e.Property(v => v.Name).HasMaxLength(160).IsRequired();
            e.Property(v => v.Trn).HasMaxLength(20);
            e.Property(v => v.Email).HasMaxLength(160);
            e.Property(v => v.Phone).HasMaxLength(40);
            e.Property(v => v.Address).HasMaxLength(300);
            e.Property(v => v.Group).HasMaxLength(40);
            e.Property(v => v.Currency).HasMaxLength(3).IsRequired();
            e.Property(v => v.CreditLimit).HasPrecision(18, 2);

            e.Property(v => v.VendorType).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.Salutation).HasMaxLength(20);
            e.Property(v => v.FirstName).HasMaxLength(80);
            e.Property(v => v.LastName).HasMaxLength(80);
            e.Property(v => v.CompanyName).HasMaxLength(160);
            e.Property(v => v.DisplayNameArabic).HasMaxLength(160);
            e.Property(v => v.VendorLanguage).HasMaxLength(40).IsRequired();
            e.Property(v => v.WorkPhone).HasMaxLength(40);
            e.Property(v => v.Mobile).HasMaxLength(40);

            e.Property(v => v.TaxTreatment).HasConversion<string>().HasMaxLength(40);
            e.Property(v => v.PlaceOfSupply).HasMaxLength(40);
            e.Property(v => v.OpeningBalance).HasPrecision(18, 2);
            e.Property(v => v.Remarks).HasMaxLength(1000);

            e.Property(v => v.BillingAttention).HasMaxLength(160);
            e.Property(v => v.BillingCountry).HasMaxLength(80);
            e.Property(v => v.BillingAddressLine1).HasMaxLength(200);
            e.Property(v => v.BillingAddressLine1Arabic).HasMaxLength(200);
            e.Property(v => v.BillingAddressLine2).HasMaxLength(200);
            e.Property(v => v.BillingAddressLine2Arabic).HasMaxLength(200);
            e.Property(v => v.BillingCity).HasMaxLength(80);
            e.Property(v => v.BillingEmirate).HasMaxLength(40);
            e.Property(v => v.BillingZip).HasMaxLength(20);
            e.Property(v => v.BillingPhone).HasMaxLength(40);
            e.Property(v => v.BillingFax).HasMaxLength(40);

            e.Property(v => v.ShippingAttention).HasMaxLength(160);
            e.Property(v => v.ShippingCountry).HasMaxLength(80);
            e.Property(v => v.ShippingAddressLine1).HasMaxLength(200);
            e.Property(v => v.ShippingAddressLine1Arabic).HasMaxLength(200);
            e.Property(v => v.ShippingAddressLine2).HasMaxLength(200);
            e.Property(v => v.ShippingAddressLine2Arabic).HasMaxLength(200);
            e.Property(v => v.ShippingCity).HasMaxLength(80);
            e.Property(v => v.ShippingEmirate).HasMaxLength(40);
            e.Property(v => v.ShippingZip).HasMaxLength(20);
            e.Property(v => v.ShippingPhone).HasMaxLength(40);
            e.Property(v => v.ShippingFax).HasMaxLength(40);

            e.HasMany(v => v.ContactPersons).WithOne(p => p.Vendor).HasForeignKey(p => p.VendorId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(v => v.Documents).WithOne(d => d.Vendor).HasForeignKey(d => d.VendorId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(v => v.CustomFieldValues).WithOne(cf => cf.Vendor).HasForeignKey(cf => cf.VendorId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(v => v.Tags).WithOne(t => t.Vendor).HasForeignKey(t => t.VendorId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<VendorContactPerson>(e =>
        {
            e.Property(p => p.Salutation).HasMaxLength(20);
            e.Property(p => p.FirstName).HasMaxLength(80).IsRequired();
            e.Property(p => p.LastName).HasMaxLength(80);
            e.Property(p => p.Email).HasMaxLength(160);
            e.Property(p => p.WorkPhone).HasMaxLength(40);
            e.Property(p => p.Mobile).HasMaxLength(40);
            e.Property(p => p.Designation).HasMaxLength(100);
            e.Property(p => p.Department).HasMaxLength(100);
        });

        b.Entity<VendorDocument>(e =>
        {
            e.Property(d => d.FileName).HasMaxLength(260).IsRequired();
            e.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
        });

        b.Entity<VendorCustomFieldValue>(e =>
        {
            e.Property(v => v.Value).HasMaxLength(1000);
            e.HasOne(v => v.CustomFieldDefinition).WithMany().HasForeignKey(v => v.CustomFieldDefinitionId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<VendorTag>(e =>
        {
            e.HasOne(t => t.Tag).WithMany().HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PurchaseInvoice>(e =>
        {
            e.HasIndex(i => new { i.CompanyId, i.InvoiceNo }).IsUnique();
            e.Property(i => i.InvoiceNo).HasMaxLength(30).IsRequired();
            e.Property(i => i.VendorRef).HasMaxLength(60);
            e.Property(i => i.LpoNo).HasMaxLength(60);
            e.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.Narration).HasMaxLength(400);
            e.Property(i => i.Notes).HasMaxLength(1000);
            e.Property(i => i.AttachmentFileName).HasMaxLength(260);
            e.Property(i => i.AttachmentContentType).HasMaxLength(100);
            e.Property(i => i.CreatedBy).HasMaxLength(80);
            e.Ignore(i => i.TotalNet);
            e.Ignore(i => i.TotalVat);
            e.Ignore(i => i.TotalGross);
            e.HasOne(i => i.Vendor).WithMany().HasForeignKey(i => i.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.CostCenter).WithMany().HasForeignKey(i => i.CostCenterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.FiscalPeriod).WithMany().HasForeignKey(i => i.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.JournalVoucher).WithMany().HasForeignKey(i => i.JournalVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(i => i.Lines).WithOne(l => l.PurchaseInvoice).HasForeignKey(l => l.PurchaseInvoiceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<PurchaseInvoiceLine>(e =>
        {
            e.Property(l => l.Description).HasMaxLength(300).IsRequired();
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 4);
            e.Property(l => l.NonTaxableAmount).HasPrecision(18, 2);
            e.Ignore(l => l.Net);
            e.Ignore(l => l.Vat);
            e.Ignore(l => l.Gross);
            e.HasOne(l => l.ExpenseAccount).WithMany().HasForeignKey(l => l.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.CostCenter).WithMany().HasForeignKey(l => l.CostCenterId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<VendorPayment>(e =>
        {
            e.HasIndex(p => new { p.CompanyId, p.PaymentNo }).IsUnique();
            e.Property(p => p.PaymentNo).HasMaxLength(30).IsRequired();
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.PaymentMode).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Narration).HasMaxLength(400);
            e.Property(p => p.CreatedBy).HasMaxLength(80);
            e.Property(p => p.ReferenceNo).HasMaxLength(60);
            e.Property(p => p.AttachmentFileName).HasMaxLength(260);
            e.Property(p => p.AttachmentContentType).HasMaxLength(100);
            e.HasOne(p => p.Vendor).WithMany().HasForeignKey(p => p.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.PurchaseInvoice).WithMany().HasForeignKey(p => p.PurchaseInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.FiscalPeriod).WithMany().HasForeignKey(p => p.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.BankAccount).WithMany().HasForeignKey(p => p.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.JournalVoucher).WithMany().HasForeignKey(p => p.JournalVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(p => p.Allocations).WithOne(a => a.VendorPayment).HasForeignKey(a => a.VendorPaymentId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<VendorPaymentAllocation>(e =>
        {
            e.Property(a => a.Amount).HasPrecision(18, 2);
            e.HasOne(a => a.PurchaseInvoiceLine).WithMany().HasForeignKey(a => a.PurchaseInvoiceLineId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DebitNote>(e =>
        {
            e.HasIndex(n => new { n.CompanyId, n.DebitNoteNo }).IsUnique();
            e.Property(n => n.DebitNoteNo).HasMaxLength(30).IsRequired();
            e.Property(n => n.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(n => n.Reason).HasMaxLength(120);
            e.Property(n => n.Narration).HasMaxLength(400);
            e.Property(n => n.CreatedBy).HasMaxLength(80);
            e.Ignore(n => n.TotalNet);
            e.Ignore(n => n.TotalVat);
            e.Ignore(n => n.TotalGross);
            e.HasOne(n => n.Vendor).WithMany().HasForeignKey(n => n.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.PurchaseInvoice).WithMany().HasForeignKey(n => n.PurchaseInvoiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.FiscalPeriod).WithMany().HasForeignKey(n => n.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.JournalVoucher).WithMany().HasForeignKey(n => n.JournalVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(n => n.Lines).WithOne(l => l.DebitNote).HasForeignKey(l => l.DebitNoteId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DebitNoteLine>(e =>
        {
            e.Property(l => l.Description).HasMaxLength(300).IsRequired();
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            e.Property(l => l.UnitPrice).HasPrecision(18, 2);
            e.Property(l => l.VatRate).HasPrecision(5, 4);
            e.Ignore(l => l.Net);
            e.Ignore(l => l.Vat);
            e.Ignore(l => l.Gross);
            e.HasOne(l => l.ExpenseAccount).WithMany().HasForeignKey(l => l.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.CostCenter).WithMany().HasForeignKey(l => l.CostCenterId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<DirectExpense>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.ExpenseNo }).IsUnique();
            e.Property(x => x.ExpenseNo).HasMaxLength(30).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Reference).HasMaxLength(60);
            e.Property(x => x.Narration).HasMaxLength(500);
            e.Property(x => x.CreatedBy).HasMaxLength(80);
            e.Ignore(x => x.TotalAmount);
            e.HasOne(x => x.Vendor).WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.FiscalPeriod).WithMany().HasForeignKey(x => x.FiscalPeriodId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.BankAccount).WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.JournalVoucher).WithMany().HasForeignKey(x => x.JournalVoucherId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Lines).WithOne(l => l.DirectExpense).HasForeignKey(l => l.DirectExpenseId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DirectExpenseLine>(e =>
        {
            e.Property(l => l.Description).HasMaxLength(300);
            e.Property(l => l.Amount).HasPrecision(18, 2);
            e.HasOne(l => l.ExpenseAccount).WithMany().HasForeignKey(l => l.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.CostCenter).WithMany().HasForeignKey(l => l.CostCenterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Item).WithMany().HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<CompanySetup>(e =>
        {
            e.Property(c => c.LegalName).HasMaxLength(200).IsRequired();
            e.Property(c => c.TradeName).HasMaxLength(200);
            e.Property(c => c.CompanyCode).HasMaxLength(20);
            e.Property(c => c.LicenseNumber).HasMaxLength(60);
            e.Property(c => c.LicenseType).HasMaxLength(40);
            e.Property(c => c.Country).HasMaxLength(60);
            e.Property(c => c.Emirate).HasMaxLength(40);
            e.Property(c => c.PlaceOfIncorporation).HasMaxLength(80);
            e.Property(c => c.AccountingMethod).HasMaxLength(20);
            e.Property(c => c.FiscalYear).HasMaxLength(30);
            e.Property(c => c.BaseCurrency).HasMaxLength(3);
            e.Property(c => c.ReportingCurrency).HasMaxLength(3);
            e.Property(c => c.OrganizationLanguage).HasMaxLength(40);
            e.Property(c => c.CommunicationLanguage).HasMaxLength(120);
            e.Property(c => c.InvoiceLanguage).HasMaxLength(40);
            e.Property(c => c.TimeZone).HasMaxLength(60);
            e.Property(c => c.DateFormat).HasMaxLength(20);
            e.Property(c => c.AddressLine1).HasMaxLength(200);
            e.Property(c => c.AddressLine2).HasMaxLength(200);
            e.Property(c => c.City).HasMaxLength(80);
            e.Property(c => c.AddressEmirate).HasMaxLength(40);
            e.Property(c => c.POBox).HasMaxLength(20);
            e.Property(c => c.AddressCountry).HasMaxLength(60);
            e.Property(c => c.Phone).HasMaxLength(40);
            e.Property(c => c.Fax).HasMaxLength(40);
            e.Property(c => c.BillingAddressLine1).HasMaxLength(200);
            e.Property(c => c.BillingAddressLine2).HasMaxLength(200);
            e.Property(c => c.BillingCity).HasMaxLength(80);
            e.Property(c => c.BillingEmirate).HasMaxLength(40);
            e.Property(c => c.BillingPOBox).HasMaxLength(20);
            e.Property(c => c.BillingCountry).HasMaxLength(60);
            e.Property(c => c.TrnLabel).HasMaxLength(10);
            e.Property(c => c.TrnNumber).HasMaxLength(20);
            e.Property(c => c.VatScheme).HasMaxLength(20);
            e.Property(c => c.VatFilingFrequency).HasMaxLength(20);
            e.Property(c => c.CtTrn).HasMaxLength(20);
            e.Property(c => c.DefaultVatRate).HasPrecision(5, 2);
            e.Property(c => c.InputVatAccountCode).HasMaxLength(20);
            e.Property(c => c.OutputVatAccountCode).HasMaxLength(20);
            e.Property(c => c.UpdatedBy).HasMaxLength(80);
            e.Property(c => c.RowVersion).IsConcurrencyToken();
            e.HasMany(c => c.BankAccounts).WithOne(a => a.CompanySetup)
                .HasForeignKey(a => a.CompanySetupId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Salespersons).WithOne(s => s.CompanySetup)
                .HasForeignKey(s => s.CompanySetupId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Documents).WithOne(d => d.CompanySetup)
                .HasForeignKey(d => d.CompanySetupId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<CompanySetupDocument>(e =>
        {
            e.Property(d => d.DocType).HasMaxLength(80).IsRequired();
            e.Property(d => d.FileName).HasMaxLength(260).IsRequired();
            e.Property(d => d.ContentType).HasMaxLength(120).IsRequired();
            e.HasIndex(d => new { d.CompanySetupId, d.DocType }).IsUnique();
        });

        b.Entity<CompanyBankAccount>(e =>
        {
            e.Property(a => a.BankName).HasMaxLength(120).IsRequired();
            e.Property(a => a.AccountName).HasMaxLength(160);
            e.Property(a => a.AccountNumber).HasMaxLength(40);
            e.Property(a => a.Iban).HasMaxLength(40);
            e.Property(a => a.Swift).HasMaxLength(20);
            e.Property(a => a.Currency).HasMaxLength(3);
        });

        b.Entity<Salesperson>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(100).IsRequired();
        });

        b.Entity<Currency>(e =>
        {
            e.HasIndex(c => new { c.CompanyId, c.Code }).IsUnique();
            e.Property(c => c.Code).HasMaxLength(3).IsRequired();
            e.Property(c => c.Name).HasMaxLength(60).IsRequired();
            e.Property(c => c.RateToBase).HasPrecision(18, 5);
        });

        b.Entity<TaxCode>(e =>
        {
            e.HasIndex(t => new { t.CompanyId, t.Code }).IsUnique();
            e.Property(t => t.Code).HasMaxLength(20).IsRequired();
            e.Property(t => t.Description).HasMaxLength(120).IsRequired();
            e.Property(t => t.Rate).HasPrecision(5, 2);
            e.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
            e.HasOne(t => t.GlAccount).WithMany().HasForeignKey(t => t.GlAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Item>(e =>
        {
            e.HasIndex(i => new { i.CompanyId, i.Code }).IsUnique();
            e.Property(i => i.Code).HasMaxLength(20).IsRequired();
            e.Property(i => i.Name).HasMaxLength(160).IsRequired();
            e.Property(i => i.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(i => i.Unit).HasMaxLength(20);
            e.Property(i => i.SellingPrice).HasPrecision(18, 2);
            e.Property(i => i.CostPrice).HasPrecision(18, 2);
            e.Property(i => i.SalesDescription).HasMaxLength(300);
            e.Property(i => i.PurchaseDescription).HasMaxLength(300);
            e.HasOne(i => i.SalesAccount).WithMany().HasForeignKey(i => i.SalesAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.PurchaseAccount).WithMany().HasForeignKey(i => i.PurchaseAccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.TaxCode).WithMany().HasForeignKey(i => i.TaxCodeId).OnDelete(DeleteBehavior.Restrict);
        });

        // ── Multi-company access ──
        b.Entity<UserCompanyAccess>(e =>
        {
            e.HasIndex(a => new { a.UserId, a.CompanyId }).IsUnique();
            e.Property(a => a.UserId).HasMaxLength(450).IsRequired();
            e.Property(a => a.Role).HasMaxLength(40).IsRequired();
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Company).WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Cascade);
        });

        // ── Company (tenant) scoping ────────────────────────────────────────
        // Every company-scoped entity gets a FK to the owning company and a global query filter.
        // The filter is what makes isolation structural: a service that forgets to filter by
        // company still cannot see — or post into — another company's books.
        ConfigureCompanyScope<Account>(b);
        ConfigureCompanyScope<CostCenter>(b);
        ConfigureCompanyScope<FiscalPeriod>(b);
        ConfigureCompanyScope<JournalVoucher>(b);
        ConfigureCompanyScope<JournalLine>(b);
        ConfigureCompanyScope<Customer>(b);
        ConfigureCompanyScope<SalesInvoice>(b);
        ConfigureCompanyScope<CustomerReceipt>(b);
        ConfigureCompanyScope<CreditNote>(b);
        ConfigureCompanyScope<Estimate>(b);
        ConfigureCompanyScope<DeliveryNote>(b);
        ConfigureCompanyScope<Vendor>(b);
        ConfigureCompanyScope<PurchaseInvoice>(b);
        ConfigureCompanyScope<VendorPayment>(b);
        ConfigureCompanyScope<DebitNote>(b);
        ConfigureCompanyScope<DirectExpense>(b);
        ConfigureCompanyScope<Currency>(b);
        ConfigureCompanyScope<TaxCode>(b);
        ConfigureCompanyScope<Item>(b);
        ConfigureCompanyScope<CustomFieldDefinition>(b);
        ConfigureCompanyScope<TagGroup>(b);
        ConfigureCompanyScope<RecurringInvoiceProfile>(b);

        // Fail fast if a new company-scoped entity is added but not registered above.
        var unscoped = b.Model.GetEntityTypes()
            .Where(t => typeof(ICompanyScoped).IsAssignableFrom(t.ClrType) && t.GetQueryFilter() is null)
            .Select(t => t.ClrType.Name)
            .ToList();
        if (unscoped.Count > 0)
            throw new InvalidOperationException(
                $"These ICompanyScoped entities have no company query filter: {string.Join(", ", unscoped)}. " +
                "Add a ConfigureCompanyScope<T>(b) call in AegisDbContext.OnModelCreating.");
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyCompanyScope();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyCompanyScope();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Stamps the active company onto newly added rows, and refuses to write a row belonging to a
    /// different company. Doing this centrally means no service has to remember to set CompanyId,
    /// and a cross-company write fails loudly instead of silently corrupting another client's books.
    /// </summary>
    private void ApplyCompanyScope()
    {
        // A null CurrentCompanyId legitimately means "unscoped" for read-only, firm-wide contexts
        // AND for bulk seeding, which adds rows for several companies from one context — those
        // rows already carry an explicit, non-zero CompanyId set by the caller, so they're left
        // untouched below. What used to slip through silently was a row added with NEITHER an
        // explicit CompanyId NOR anything to stamp it with (CompanyId stays 0, which isn't any
        // real company) — that failed later with a raw, confusing "FOREIGN KEY constraint failed"
        // instead of saying what was actually wrong.
        if (CurrentCompanyId is not int companyId)
        {
            if (ChangeTracker.Entries<ICompanyScoped>().Any(e => e.State == EntityState.Added && e.Entity.CompanyId == 0))
                throw new PostingException("No company is selected. Pick a company before creating this record.");
            return;
        }

        foreach (var entry in ChangeTracker.Entries<ICompanyScoped>())
        {
            switch (entry.State)
            {
                case EntityState.Added when entry.Entity.CompanyId == 0:
                    entry.Entity.CompanyId = companyId;
                    break;
                case EntityState.Added or EntityState.Modified or EntityState.Deleted
                    when entry.Entity.CompanyId != companyId:
                    throw new PostingException(
                        $"Cross-company write blocked: a {entry.Entity.GetType().Name} belonging to company " +
                        $"{entry.Entity.CompanyId} cannot be saved while working in company {companyId}.");
            }
        }
    }

    /// <summary>
    /// Adds the company foreign key and the global query filter for one tenant-scoped entity.
    /// The filter references <see cref="CurrentCompanyId"/> on this context instance, so EF
    /// re-reads it per query; a null value disables filtering (seeding / firm-wide admin).
    /// </summary>
    private void ConfigureCompanyScope<TEntity>(ModelBuilder b) where TEntity : class, ICompanyScoped
    {
        b.Entity<TEntity>(e =>
        {
            e.HasIndex(x => x.CompanyId);
            e.HasOne<CompanySetup>().WithMany()
                .HasForeignKey(x => x.CompanyId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasQueryFilter(x => CurrentCompanyId == null || x.CompanyId == CurrentCompanyId);
        });
    }
}
