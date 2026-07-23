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
    DebitNote = 8
}

/// <summary>Lifecycle of a voucher. Only <see cref="Posted"/> entries hit the ledger.</summary>
public enum VoucherStatus
{
    Draft = 1,
    Posted = 2,
    Rejected = 3
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

public static class AccountTypeExtensions
{
    /// <summary>Assets and expenses are debit-normal; liabilities, equity and income are credit-normal.</summary>
    public static NormalBalance NormalBalance(this AccountType type) => type switch
    {
        AccountType.Asset or AccountType.Expense => Domain.NormalBalance.Debit,
        _ => Domain.NormalBalance.Credit
    };
}
