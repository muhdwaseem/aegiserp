namespace AegisErp.Domain.Entities;

/// <summary>
/// A direct cash/bank expense (Dr Expense account(s) / Cr bank), posted in one step with no
/// vendor invoice or Accounts Payable cycle involved — e.g. fuel, a taxi, a debit-card purchase.
/// Vendor and Customer are both optional: Vendor is just a record of who was paid (no payable is
/// created), and Customer is a "billable to" tag with no automated re-billing behavior.
/// </summary>
public class DirectExpense : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "EXP-2026-0001". Shared with the generated GL voucher. Unique within the company.</summary>
    public string ExpenseNo { get; set; } = string.Empty;

    /// <summary>Who was paid, if relevant to record — optional, since this never creates a payable.</summary>
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    /// <summary>Optional "billable to" tag — no automated re-billing effect.</summary>
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public DateOnly Date { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    /// <summary>Bank/cash account the money went out of ("Paid Through").</summary>
    public int BankAccountId { get; set; }
    public Account BankAccount { get; set; } = null!;

    public string? Reference { get; set; }
    public string? Narration { get; set; }

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public List<DirectExpenseLine> Lines { get; set; } = new();

    public decimal TotalAmount => Lines.Sum(l => l.Amount);

    /// <summary>Validates the expense and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Expense is already posted.");
        if (BankAccountId == 0)
            throw new PostingException("Expense has no \"Paid Through\" account.");
        if (Lines.Count == 0)
            throw new PostingException("Expense needs at least one line.");

        foreach (var line in Lines)
        {
            if (line.ExpenseAccountId == 0)
                throw new PostingException($"Line {line.LineNo} has no expense account.");
            if (line.Amount <= 0)
                throw new PostingException($"Line {line.LineNo} amount must be positive.");
        }

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}

/// <summary>One expense line: an amount charged to a specific expense account.</summary>
public class DirectExpenseLine
{
    public int Id { get; set; }

    public int DirectExpenseId { get; set; }
    public DirectExpense DirectExpense { get; set; } = null!;

    public int LineNo { get; set; }

    /// <summary>Optional catalog item this charge was picked from — auto-fills the account/description/amount below, editable afterward.</summary>
    public int? ItemId { get; set; }
    public Item? Item { get; set; }

    public int ExpenseAccountId { get; set; }
    public Account ExpenseAccount { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public string? Description { get; set; }
    public decimal Amount { get; set; }
}
