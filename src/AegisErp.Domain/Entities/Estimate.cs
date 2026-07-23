namespace AegisErp.Domain.Entities;

/// <summary>
/// A sales estimate / quotation. A non-posting document: it never touches the ledger. When a
/// customer accepts it, it can be converted into a sales invoice (which is what posts to the GL).
/// </summary>
public class Estimate : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Document number, e.g. "EST-2026-0004". Unique within the company.</summary>
    public string EstimateNo { get; set; } = string.Empty;

    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;

    public DateOnly Date { get; set; }

    /// <summary>Date the quotation is valid until.</summary>
    public DateOnly ValidUntil { get; set; }

    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;

    public string? Narration { get; set; }

    /// <summary>Sales invoice this estimate was converted into (set once accepted &amp; converted).</summary>
    public int? ConvertedInvoiceId { get; set; }
    public SalesInvoice? ConvertedInvoice { get; set; }

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }

    public List<EstimateLine> Lines { get; set; } = new();

    public decimal TotalNet => Lines.Sum(l => l.Net);
    public decimal TotalVat => Lines.Sum(l => l.Vat);
    public decimal TotalGross => Lines.Sum(l => l.Gross);
}

/// <summary>A quotation line: quantity × unit price with a VAT rate, mapped to a revenue account for conversion.</summary>
public class EstimateLine
{
    public int Id { get; set; }

    public int EstimateId { get; set; }
    public Estimate Estimate { get; set; } = null!;

    public int LineNo { get; set; }
    public string Description { get; set; } = string.Empty;

    public int RevenueAccountId { get; set; }
    public Account RevenueAccount { get; set; } = null!;

    public int? CostCenterId { get; set; }
    public CostCenter? CostCenter { get; set; }

    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    public decimal VatRate { get; set; } = 0.05m;

    public decimal Net => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
    public decimal Vat => Math.Round(Net * VatRate, 2, MidpointRounding.AwayFromZero);
    public decimal Gross => Net + Vat;
}
