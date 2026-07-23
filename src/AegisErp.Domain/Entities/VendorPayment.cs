namespace AegisErp.Domain.Entities;

/// <summary>
/// A vendor payment (money out). Posting generates a GL voucher
/// (Dr Accounts Payable / Cr bank). May be allocated to a specific
/// purchase invoice or left on account.
/// </summary>
public class VendorPayment : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "PV-2026-0045". Shared with the generated GL voucher. Unique within the company.</summary>
    public string PaymentNo { get; set; } = string.Empty;

    public int VendorId { get; set; }
    public Vendor Vendor { get; set; } = null!;

    /// <summary>Purchase invoice this payment settles (null = on-account payment).</summary>
    public int? PurchaseInvoiceId { get; set; }
    public PurchaseInvoice? PurchaseInvoice { get; set; }

    public DateOnly Date { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    /// <summary>Bank/cash account the money went out of.</summary>
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

    /// <summary>Validates the payment and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Payment is already posted.");
        if (VendorId == 0)
            throw new PostingException("Payment has no vendor.");
        if (BankAccountId == 0)
            throw new PostingException("Payment has no bank account.");
        if (Amount <= 0)
            throw new PostingException("Payment amount must be positive.");

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}
