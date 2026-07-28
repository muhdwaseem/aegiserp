using AegisErp.Domain;

namespace AegisErp.Infrastructure.Services;

/// <summary>One row in a general-ledger account view, with the running balance after the entry.</summary>
public record LedgerRow(
    DateOnly Date,
    string VoucherNo,
    string Type,
    string Narration,
    string CostCenter,
    decimal Debit,
    decimal Credit,
    decimal RunningBalance);

public record AccountLedger(
    string AccountCode,
    string AccountName,
    string Period,
    NormalBalance NormalBalance,
    decimal Opening,
    decimal TotalDebit,
    decimal TotalCredit,
    decimal Closing,
    IReadOnlyList<LedgerRow> Rows);

/// <summary>
/// One row in the all-accounts general ledger view. Unlike <see cref="LedgerRow"/>, this can mix
/// entries from different accounts, so it carries its own account identity and running balance
/// (that account's own net position after this entry — not a combined total across accounts).
/// </summary>
public record GeneralLedgerRow(
    DateOnly Date,
    string VoucherNo,
    string Type,
    string Narration,
    string AccountCode,
    string AccountName,
    string CostCenter,
    string PostedBy,
    decimal Debit,
    decimal Credit,
    decimal RunningBalance);

public record GeneralLedgerView(
    string Period,
    decimal Opening,
    decimal TotalDebit,
    decimal TotalCredit,
    decimal Closing,
    IReadOnlyList<GeneralLedgerRow> Rows);

/// <summary>One line of a trial balance: an account with its net debit/credit position.</summary>
public record TrialBalanceRow(string Code, string Name, decimal Debit, decimal Credit);

public record TrialBalance(
    string Period,
    IReadOnlyList<TrialBalanceRow> Rows,
    decimal TotalDebit,
    decimal TotalCredit)
{
    public bool IsBalanced => TotalDebit == TotalCredit;
}

/// <summary>One revenue or expense line on the P&amp;L, with period and year-to-date figures.</summary>
public record PnlLine(string Code, string Name, decimal Period, decimal Ytd);

public record ProfitAndLoss(
    string PeriodName,
    IReadOnlyList<PnlLine> Income,
    IReadOnlyList<PnlLine> Expenses,
    decimal IncomePeriod, decimal IncomeYtd,
    decimal ExpensePeriod, decimal ExpenseYtd)
{
    public decimal NetPeriod => IncomePeriod - ExpensePeriod;
    public decimal NetYtd => IncomeYtd - ExpenseYtd;
}

/// <summary>One line on the balance sheet (account balance in its natural positive sense).</summary>
public record BsLine(string Code, string Name, decimal Amount);

public record BalanceSheet(
    string AsOf,
    IReadOnlyList<BsLine> Assets,
    IReadOnlyList<BsLine> Liabilities,
    IReadOnlyList<BsLine> Equity,
    decimal CurrentYearEarnings)
{
    public decimal TotalAssets => Assets.Sum(l => l.Amount);
    public decimal TotalLiabilities => Liabilities.Sum(l => l.Amount);
    public decimal TotalEquity => Equity.Sum(l => l.Amount) + CurrentYearEarnings;
    public decimal TotalLiabilitiesAndEquity => TotalLiabilities + TotalEquity;
    public bool IsBalanced => TotalAssets == TotalLiabilitiesAndEquity;
}

public record DashboardKpis(
    string Period,
    decimal Income,
    decimal Expense,
    decimal NetProfit,
    decimal CashAndBank,
    decimal Receivables,
    decimal Payables,
    int DraftVouchers);

/// <summary>Per-period revenue and expense series for the dashboard bar chart.</summary>
public record PeriodSeries(string[] Labels, double[] Revenue, double[] Expense);

/// <summary>A cash/bank account with its current balance, for the dashboard.</summary>
public record CashBalance(string Code, string Name, decimal Balance);

/// <summary>Input line used when creating/posting a voucher from the UI.</summary>
public record VoucherLineInput(int AccountId, int? CostCenterId, string? Description, decimal Debit, decimal Credit);
