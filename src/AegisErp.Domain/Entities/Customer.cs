namespace AegisErp.Domain.Entities;

/// <summary>An AR customer. Invoices and receipts reference the customer to form its subledger.</summary>
public class Customer : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company.</summary>
    public int CompanyId { get; set; }

    /// <summary>Human-facing customer number, e.g. "C-0001". Unique within the company.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>UAE Tax Registration Number (optional).</summary>
    public string? Trn { get; set; }

    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }

    /// <summary>Customer group / segment, e.g. Corporate, Individual, Government.</summary>
    public string? Group { get; set; }

    /// <summary>Account currency. AED-only for now (stored, no FX logic).</summary>
    public string Currency { get; set; } = "AED";

    /// <summary>Credit limit in the account currency (0 = no limit set).</summary>
    public decimal CreditLimit { get; set; }

    /// <summary>Days until an invoice falls due (drives the invoice DueDate and aging).</summary>
    public int PaymentTermsDays { get; set; } = 30;

    /// <summary>Staff member who owns this customer relationship. Only collected when the owning
    /// company has <see cref="CompanySetup.SalespersonEnabled"/> turned on — some companies
    /// (e.g. pro-services firms) track this, most don't.</summary>
    public string? Salesperson { get; set; }

    public bool IsActive { get; set; } = true;
}
