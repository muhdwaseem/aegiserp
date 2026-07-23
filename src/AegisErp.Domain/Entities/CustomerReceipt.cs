namespace AegisErp.Domain.Entities;

/// <summary>
/// A customer receipt (money in). Posting generates a GL voucher
/// (Dr bank / Cr Accounts Receivable). May be allocated to a specific
/// invoice or left on account.
/// </summary>
public class CustomerReceipt : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "RV-2026-0028". Shared with the generated GL voucher. Unique within the company.</summary>
    public string ReceiptNo { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    /// <summary>Invoice this receipt settles (null = on-account receipt).</summary>
    public int? SalesInvoiceId { get; set; }
    public SalesInvoice? SalesInvoice { get; set; }

    public DateOnly Date { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    /// <summary>Bank/cash account the money landed in.</summary>
    public int BankAccountId { get; set; }
    public Account BankAccount { get; set; } = null!;

    public decimal Amount { get; set; }

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;
    public string? Narration { get; set; }

    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    /// <summary>Validates the receipt and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Receipt is already posted.");
        if (CustomerId == 0)
            throw new PostingException("Receipt has no customer.");
        if (BankAccountId == 0)
            throw new PostingException("Receipt has no bank account.");
        if (Amount <= 0)
            throw new PostingException("Receipt amount must be positive.");

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}
