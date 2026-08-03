namespace AegisErp.Domain;

/// <summary>Top-level classification of a chart-of-accounts account.</summary>
public enum AccountType
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Income = 4,
    Expense = 5
}

/// <summary>The side on which an account normally carries its balance.</summary>
public enum NormalBalance
{
    Debit = 1,
    Credit = 2
}

/// <summary>Source document type for a voucher. Mirrors the modules in the prototype.</summary>
public enum VoucherType
{
    Journal = 1,
    Receipt = 2,
    Payment = 3,
    SalesInvoice = 4,
    PurchaseInvoice = 5,
    Opening = 6,
    CreditNote = 7,
    DebitNote = 8,
    Expense = 9
}

/// <summary>Lifecycle of a voucher. Only <see cref="Posted"/> entries hit the ledger.</summary>
public enum VoucherStatus
{
    Draft = 1,
    Posted = 2,
    Void = 3
}

/// <summary>
/// Zoho-style display status for a sales invoice. Draft and Void mirror the persisted
/// <see cref="VoucherStatus"/>; Pending/Overdue/Paid are derived at read time from the due date
/// and the payments/credit notes applied against the invoice, never stored directly.
/// </summary>
public enum ArStatus
{
    Draft = 1,
    Pending = 2,
    Overdue = 3,
    Paid = 4,
    Void = 5
}

/// <summary>
/// Lifecycle of a non-posting sales document (estimate / delivery note). These never touch
/// the ledger; the status just tracks where the document is in its own workflow.
/// </summary>
public enum DocumentStatus
{
    Draft = 1,
    Sent = 2,
    Accepted = 3,
    Declined = 4,
    Converted = 5,
    Delivered = 6
}

/// <summary>How a tax code applies: on sales (Output), on purchases (Input), or not at all.</summary>
public enum TaxCodeKind
{
    Output = 1,
    Input = 2,
    Exempt = 3,
    ReverseCharge = 4
}

/// <summary>Whether an item is a physical good or a billable service.</summary>
public enum ItemKind
{
    Goods = 1,
    Service = 2
}

/// <summary>How a customer receipt (or vendor payment) physically arrived — informational only.</summary>
public enum PaymentMode
{
    Cash = 1,
    BankTransfer = 2,
    Cheque = 3,
    Card = 4,
    Other = 5,
    PostDatedCheque = 6
}

/// <summary>Approval workflow state for a document, independent of its posting <see cref="VoucherStatus"/>.
/// None means the workflow was never used — the document can be posted directly.</summary>
public enum ApprovalStatus
{
    None = 1,
    PendingApproval = 2,
    Approved = 3,
    Rejected = 4
}

/// <summary>How a credit note settles: reduce a specific invoice's balance, leave a general
/// credit balance on the customer's account, or pay the customer back in cash immediately.</summary>
public enum CreditNoteSettlementMethod
{
    ApplyToInvoice = 1,
    CreditOnAccount = 2,
    CashRefund = 3
}

/// <summary>
/// Whether a sales invoice line's revenue is earned now or later. <see cref="Direct"/> credits
/// the line's own Revenue (Income) account immediately, same as every invoice line before this
/// existed — it hits the P&amp;L this period. <see cref="Deferred"/> credits the
/// <see cref="AegisErp.WellKnownAccounts.DeferredRevenue"/> control account (a Liability) instead
/// — the amount sits on the Balance Sheet as unearned revenue until a separate Journal Voucher
/// later moves it into the P&amp;L (Dr Deferred Revenue / Cr Revenue) as it's actually earned.
/// </summary>
public enum RevenueRecognition
{
    Direct = 1,
    Deferred = 2
}

/// <summary>Whether a customer represents a company or a single person — drives whether Company
/// Name or Salutation/First/Last Name is the primary identity field on the customer form.</summary>
public enum CustomerType
{
    Business = 1,
    Individual = 2
}

/// <summary>
/// UAE VAT registration status for a customer, distinct from (but usually paired with) their
/// <see cref="Entities.Customer.Trn"/>. Drives how VAT is treated on invoices raised to them.
/// </summary>
public enum TaxTreatment
{
    VatRegistered = 1,
    NonVatRegistered = 2,
    GccVatRegistered = 3,
    GccNonVatRegistered = 4,
    NonGcc = 5,
    VatRegisteredDesignatedZone = 6,
    NonVatRegisteredDesignatedZone = 7
}

/// <summary>How an admin-defined <see cref="Entities.CustomFieldDefinition"/> is rendered and
/// validated on the form it appears on.</summary>
public enum CustomFieldType
{
    Text = 1,
    Number = 2,
    Date = 3,
    Dropdown = 4,
    Checkbox = 5
}

/// <summary>How often a <see cref="Entities.RecurringInvoiceProfile"/> generates its next invoice.</summary>
public enum RecurringFrequency
{
    Weekly = 1,
    Monthly = 2,
    Quarterly = 3,
    Yearly = 4
}

public static class AccountTypeExtensions
{
    /// <summary>Assets and expenses are debit-normal; liabilities, equity and income are credit-normal.</summary>
    public static NormalBalance NormalBalance(this AccountType type) => type switch
    {
        AccountType.Asset or AccountType.Expense => Domain.NormalBalance.Debit,
        _ => Domain.NormalBalance.Credit
    };
}

public static class RecurringFrequencyExtensions
{
    /// <summary>The next generation date, <paramref name="repeatEvery"/> units of <paramref name="frequency"/> after <paramref name="from"/>.</summary>
    public static DateOnly AddFrequency(this DateOnly from, RecurringFrequency frequency, int repeatEvery) => frequency switch
    {
        RecurringFrequency.Weekly => from.AddDays(7 * repeatEvery),
        RecurringFrequency.Monthly => from.AddMonths(repeatEvery),
        RecurringFrequency.Quarterly => from.AddMonths(3 * repeatEvery),
        RecurringFrequency.Yearly => from.AddYears(repeatEvery),
        _ => from.AddMonths(repeatEvery),
    };
}
