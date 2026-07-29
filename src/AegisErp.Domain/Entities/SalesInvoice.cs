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

    /// <summary>The GL voucher generated when this invoice was posted.</summary>
    public int? JournalVoucherId { get; set; }
    public JournalVoucher? JournalVoucher { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public List<SalesInvoiceLine> Lines { get; set; } = new();

    public decimal TotalNet => Lines.Sum(l => l.Net);
    public decimal TotalVat => Lines.Sum(l => l.Vat);
    public decimal TotalGross => Lines.Sum(l => l.Gross);

    /// <summary>Validates the invoice and transitions it to Posted. The caller generates the GL voucher.</summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Invoice is already posted.");
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
        }

        if (TotalGross <= 0)
            throw new PostingException("Invoice total must be positive.");

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
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

    public decimal Net => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    public decimal Vat => Math.Round(Net * VatRate, 2, MidpointRounding.AwayFromZero);
    public decimal Gross => Net + Vat;
}
