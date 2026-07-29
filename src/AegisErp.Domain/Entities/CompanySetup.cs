namespace AegisErp.Domain.Entities;

/// <summary>
/// Company-wide configuration (a single row). Captures the "Create Company" requirement:
/// general info, financial setup, localization, address, VAT, corporate tax, tax config,
/// system controls and bank accounts. Most fields are stored configuration; a few
/// (base currency, TRN, financial year) drive real behaviour as those features are built.
/// </summary>
public class CompanySetup
{
    public int Id { get; set; }

    // ── 1. General Information ──
    public string LegalName { get; set; } = string.Empty;      // Company Name (Legal) *
    public string? TradeName { get; set; }
    public string CompanyCode { get; set; } = string.Empty;    // auto, unique
    public string? LicenseNumber { get; set; }                 // *
    public string? LicenseType { get; set; }                   // LLC / FZE / Sole Establishment
    public DateOnly? RegistrationDate { get; set; }            // *
    public DateOnly? LicenseExpiryDate { get; set; }
    public string? Country { get; set; } = "United Arab Emirates";
    public string? Emirate { get; set; }
    public string? PlaceOfIncorporation { get; set; }
    public bool IsFreeZone { get; set; }
    public bool IsDesignatedZone { get; set; }                 // only if IsFreeZone

    // ── 2. Financial Setup ──
    public DateOnly? FinancialYearStart { get; set; }          // *
    public DateOnly? FinancialYearEnd { get; set; }            // auto (start + 1 year - 1 day)
    public DateOnly? BooksStartDate { get; set; }              // *
    public string AccountingMethod { get; set; } = "Accrual";  // Accrual / Cash
    public string? FiscalYear { get; set; }                    // e.g. "Jan–Dec"
    public string BaseCurrency { get; set; } = "AED";          // *
    public string? ReportingCurrency { get; set; }

    // ── 3. Localization & Language ──
    public string? OrganizationLanguage { get; set; }
    public string? CommunicationLanguage { get; set; }         // multi-select stored as CSV
    public string? InvoiceLanguage { get; set; }
    public string? TimeZone { get; set; } = "Asia/Dubai";
    public string? DateFormat { get; set; } = "dd/MM/yyyy";

    // ── 4. Address (Registered) ──
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? City { get; set; }
    public string? AddressEmirate { get; set; }
    public string? POBox { get; set; }
    public string? AddressCountry { get; set; } = "United Arab Emirates";
    public string? Phone { get; set; }
    public string? Fax { get; set; }

    // ── 4b. Billing Address ──
    public bool BillingSameAsRegistered { get; set; } = true;
    public string? BillingAddressLine1 { get; set; }
    public string? BillingAddressLine2 { get; set; }
    public string? BillingCity { get; set; }
    public string? BillingEmirate { get; set; }
    public string? BillingPOBox { get; set; }
    public string? BillingCountry { get; set; }

    // ── 5. VAT Setup ──
    public bool VatRegistered { get; set; }
    public string TrnLabel { get; set; } = "TRN";              // TRN / GST
    public string? TrnNumber { get; set; }                     // 15-digit
    public DateOnly? VatRegistrationDate { get; set; }
    public string? VatScheme { get; set; }                     // Standard / Cash
    public string? VatFilingFrequency { get; set; }            // Monthly / Quarterly
    public DateOnly? VatDeregistrationDate { get; set; }

    // ── 6. Corporate Tax ──
    public bool CtRegistered { get; set; }
    public string? CtTrn { get; set; }
    public DateOnly? FirstTaxPeriodStart { get; set; }
    public bool FreeZonePerson { get; set; }
    public bool QfzpStatus { get; set; }
    public bool SmallBusinessRelief { get; set; }

    // ── 7. Tax Configuration ──
    public decimal DefaultVatRate { get; set; } = 5m;          // %
    public string? InputVatAccountCode { get; set; }           // GL mapping
    public string? OutputVatAccountCode { get; set; }          // GL mapping

    // ── 8. System Controls ──
    public bool MultiCompanyEnabled { get; set; }
    public bool AuditTrailEnabled { get; set; } = true;
    public bool ApprovalWorkflowEnabled { get; set; }

    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Optimistic-concurrency token. Regenerated on every update; the save fails if the
    /// value the editor started from no longer matches what's in the database.</summary>
    public Guid RowVersion { get; set; } = Guid.NewGuid();

    public List<CompanyBankAccount> BankAccounts { get; set; } = new();
}

/// <summary>A company bank account (primary + additional). Section 9 of the requirement.</summary>
public class CompanyBankAccount
{
    public int Id { get; set; }
    public int CompanySetupId { get; set; }
    public CompanySetup CompanySetup { get; set; } = null!;

    public string BankName { get; set; } = string.Empty;
    public string? AccountName { get; set; }
    public string? AccountNumber { get; set; }
    public string? Iban { get; set; }
    public string? Swift { get; set; }
    public string Currency { get; set; } = "AED";
    public bool IsPrimary { get; set; }
}
