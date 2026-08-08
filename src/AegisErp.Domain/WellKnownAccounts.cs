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

    /// <summary>Liability — gratuity (End of Service Benefits) postings credit this; paying it out
    /// later debits it against Bank. See <c>GratuityService</c>.</summary>
    public const string GratuityPayable = "21040";

    /// <summary>Liability — a "Pay Later" Direct Expense credits this instead of Bank at posting;
    /// the later payoff debits it against Bank. See <c>DirectExpensePaymentService</c>.
    /// v1 limitation: input VAT on a Pay-Later expense is recovered in full at posting time and is
    /// never automatically reversed under the UAE VAT Executive Regulations' Article 55 six-month
    /// non-payment rule (input tax must be repaid if unpaid &gt;6 months past due, then re-claimed
    /// once paid). Flag long-outstanding balances on this account for manual review.</summary>
    public const string ExpensesPayable = "21050";
}
