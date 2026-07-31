namespace AegisErp.Domain.Entities;

/// <summary>
/// An AR sales invoice. Posting it generates a balanced GL voucher
/// (Dr Accounts Receivable / Cr revenue per line / Cr VAT payable).
/// </summary>
public class SalesInvoice : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "INV-2026-0141". Shared with the generated GL voucher. Unique within the company.</summary>
    public string InvoiceNo { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public DateOnly Date { get; set; }
    public DateOnly DueDate { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    public string? Narration { get; set; }

    /// <summary>Customer's own purchase order reference, shown on the printed invoice.</summary>
    public string? CustomerPoNo { get; set; }

    /// <summary>Reference to the delivery note this invoice bills against, if any.</summary>
    public string? DeliveryNoteRef { get; set; }

    /// <summary>Reference to the sales order this invoice was raised from, if any.</summary>
    public string? SalesOrderRef { get; set; }

    /// <summary>Customer-facing note printed on the invoice (payment terms, delivery notes, etc.) —
    /// distinct from <see cref="Narration"/>, which is the internal GL voucher description.</summary>
    public string? Notes { get; set; }

    /// <summary>Approval workflow state — independent of <see cref="Status"/>. An invoice can be
    /// submitted for approval while still a Draft; approval does not itself post it.</summary>
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.None;
    public DateTime? SubmittedForApprovalAtUtc { get; set; }
    public string? SubmittedForApprovalBy { get; set; }
    public DateTime? ApprovalDecisionAtUtc { get; set; }
    public string? ApprovalDecisionBy { get; set; }
    /// <summary>Rejection reason, if the last decision was a rejection.</summary>
    public string? ApprovalNote { get; set; }

    /// <summary>One supporting document attached to the invoice (e.g. signed PO, delivery proof).</summary>
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public byte[]? AttachmentData { get; set; }

    /// <summary>The GL voucher generated when this invoice was posted.</summary>
    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public string? VoidedBy { get; set; }
    public DateTime? VoidedAtUtc { get; set; }

    public List<SalesInvoiceLine> Lines { get; set; } = new();

    public decimal TotalNet => Lines.Sum(l => l.Net);
    public decimal TotalVat => Lines.Sum(l => l.Vat);
    public decimal TotalGross => Lines.Sum(l => l.Gross);

    /// <summary>Validates the invoice and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(string postedBy, DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Invoice is already posted.");
        if (ApprovalStatus == ApprovalStatus.PendingApproval)
            throw new PostingException("Invoice is pending approval — approve or reject it before posting.");
        if (CustomerId == 0)
            throw new PostingException("Invoice has no customer.");
        if (Lines.Count == 0)
            throw new PostingException("Invoice needs at least one line.");
        if (DueDate < Date)
            throw new PostingException("Due date cannot be before the invoice date.");

        foreach (var line in Lines)
        {
            if (line.RevenueAccountId == 0)
                throw new PostingException($"Line {line.LineNo} has no revenue account.");
            if (line.Quantity <= 0)
                throw new PostingException($"Line {line.LineNo} quantity must be positive.");
            if (line.UnitPrice < 0)
                throw new PostingException($"Line {line.LineNo} unit price cannot be negative.");
            if (line.VatRate is < 0 or > 1)
                throw new PostingException($"Line {line.LineNo} VAT rate is invalid.");
            if (line.DiscountPercent is < 0 or > 100)
                throw new PostingException($"Line {line.LineNo} discount % is invalid.");
        }

        if (TotalGross <= 0)
            throw new PostingException("Invoice total must be positive.");

        Status = VoucherStatus.Posted;
        PostedBy = postedBy;
        PostedAtUtc = nowUtc;
    }

    /// <summary>Submits a draft invoice into the approval queue. Posting is blocked while pending.</summary>
    public void SubmitForApproval(string submittedBy, DateTime nowUtc)
    {
        if (Status != VoucherStatus.Draft)
            throw new PostingException("Only draft invoices can be submitted for approval.");
        if (ApprovalStatus == ApprovalStatus.PendingApproval)
            throw new PostingException("Invoice is already pending approval.");

        ApprovalStatus = ApprovalStatus.PendingApproval;
        SubmittedForApprovalAtUtc = nowUtc;
        SubmittedForApprovalBy = submittedBy;
        ApprovalDecisionAtUtc = null;
        ApprovalDecisionBy = null;
        ApprovalNote = null;
    }

    /// <summary>Approves a pending invoice, clearing the block on posting.</summary>
    public void Approve(string approvedBy, DateTime nowUtc)
    {
        if (ApprovalStatus != ApprovalStatus.PendingApproval)
            throw new PostingException("Invoice is not pending approval.");

        ApprovalStatus = ApprovalStatus.Approved;
        ApprovalDecisionAtUtc = nowUtc;
        ApprovalDecisionBy = approvedBy;
    }

    /// <summary>Rejects a pending invoice. It stays a Draft, editable and re-submittable.</summary>
    public void Reject(string rejectedBy, DateTime nowUtc, string? note)
    {
        if (ApprovalStatus != ApprovalStatus.PendingApproval)
            throw new PostingException("Invoice is not pending approval.");

        ApprovalStatus = ApprovalStatus.Rejected;
        ApprovalDecisionAtUtc = nowUtc;
        ApprovalDecisionBy = rejectedBy;
        ApprovalNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    /// <summary>Voids a draft invoice. Posted invoices already hit the ledger — reverse those with a Credit Note instead.</summary>
    public void Void(string voidedBy, DateTime nowUtc)
    {
        if (Status != VoucherStatus.Draft)
            throw new PostingException("Only draft invoices can be voided — a posted invoice already hit the ledger; reverse it with a Credit Note instead.");

        Status = VoucherStatus.Void;
        VoidedBy = voidedBy;
        VoidedAtUtc = nowUtc;
    }
}

/// <summary>A sales invoice line: quantity × unit price with a VAT rate, posted to a revenue account.</summary>
public class SalesInvoiceLine
{
    public int Id { get; set; }

    public int SalesInvoiceId { get; set; }
    public SalesInvoice SalesInvoice { get; set; } = null!;

    public int LineNo { get; set; }
    public string Description { get; set; } = string.Empty;

    /// <summary>The catalog item this line was billed from, if any — used to remember which
    /// revenue account was last used for this item so it can be pre-filled next time.</summary>
    public int? ItemId { get; set; }
    public Item? Item { get; set; }

    public int RevenueAccountId { get; set; }
    public Account RevenueAccount { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }

    /// <summary>Fractional VAT rate, e.g. 0.05 for UAE 5%.</summary>
    public decimal VatRate { get; set; } = 0.05m;

    /// <summary>Line discount, 0-100.</summary>
    public decimal DiscountPercent { get; set; }

    /// <summary>Unit of measure, e.g. "pcs", "hrs" — free text, defaults from the item's own Unit.</summary>
    public string? Uom { get; set; }

    /// <summary>Supporting document for this specific line (e.g. a delivery note or spec sheet for that item).</summary>
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public byte[]? AttachmentData { get; set; }

    public decimal Net => Math.Round(Quantity * UnitPrice * (1 - DiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);
    public decimal Vat => Math.Round(Net * VatRate, 2, MidpointRounding.AwayFromZero);
    public decimal Gross => Net + Vat;
}
