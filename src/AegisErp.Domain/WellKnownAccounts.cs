namespace AegisErp.Domain;

/// <summary>
/// Control accounts the posting engine must be able to resolve by code.
/// Subledger documents (invoices, receipts) post against these.
/// </summary>
public static class WellKnownAccounts
{
    public const string AccountsReceivable = "12010";
    public const string VatPayable = "22010";

    /// <summary>AP control account — purchase invoices credit it; payments and debit notes debit it.</summary>
    public const string AccountsPayable = "21010";

    /// <summary>Recoverable input VAT on purchases (sits in the prepaid/VAT-input asset account).</summary>
    public const string VatInput = "13010";

    /// <summary>Unearned revenue (Liability) — where a sales invoice line's credit lands when
    /// its <see cref="Domain.RevenueRecognition"/> is Deferred, instead of a P&amp;L revenue account.</summary>
    public const string DeferredRevenue = "23010";

    /// <summary>Contra-asset (credit-normal, nets against fixed assets on the Balance Sheet) —
    /// fixed-asset depreciation runs credit this control account instead of crediting each asset
    /// directly.</summary>
    public const string AccumulatedDepreciation = "15010";

    /// <summary>Liability — payroll runs credit this for each employee's net pay; paying salaries
    /// out later debits it against Bank.</summary>
    public const string SalariesPayable = "21020";
}
