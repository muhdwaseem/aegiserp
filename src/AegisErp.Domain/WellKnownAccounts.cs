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
}
