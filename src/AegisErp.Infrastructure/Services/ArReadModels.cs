using AegisErp.Domain;
using AegisErp.Domain.Entities;

namespace AegisErp.Infrastructure.Services;

/// <summary>A customer with its subledger position (posted documents only).</summary>
public record CustomerSummary(
    int Id, string Code, string Name, string? Trn, int PaymentTermsDays,
    decimal Invoiced, decimal Received, decimal Outstanding);

/// <summary>One row in a customer statement: an invoice (debit) or receipt (credit).</summary>
public record StatementRow(
    DateOnly Date, string DocNo, string DocType, string Narration,
    decimal Debit, decimal Credit, decimal RunningBalance);

/// <summary>AR aging for one customer, bucketed by days past due as of a reference date.</summary>
public record AgingRow(
    string Code, string Name,
    decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90,
    decimal UnallocatedCredits)
{
    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90 - UnallocatedCredits;
}

/// <summary>A posted invoice with money still owing (for receipt allocation pickers).</summary>
public record OpenInvoice(int Id, string InvoiceNo, DateOnly Date, DateOnly DueDate, decimal Gross, decimal Outstanding);

/// <summary>A sales invoice for the list page, with its Zoho-style display status and remaining
/// balance computed from payments/credit notes applied — for the invoice list and its status filter.</summary>
public record SalesInvoiceRow(SalesInvoice Invoice, decimal Balance, ArStatus Status);
