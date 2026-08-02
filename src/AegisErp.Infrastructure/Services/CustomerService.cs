using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Customer" form collects.</summary>
public record NewCustomerInput(
    string Name, string? Group, string Currency, decimal CreditLimit,
    int PaymentTermsDays, string? Trn, string? Email, string? Phone, string? Address,
    string? Salesperson = null);

public class CustomerService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public CustomerService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<Customer>> GetAllAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Customers.AsNoTracking().OrderBy(c => c.Code).AsQueryable();
        if (activeOnly) q = q.Where(c => c.IsActive);
        return await q.ToListAsync();
    }

    public async Task<Customer> CreateAsync(NewCustomerInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Customer name is required.");
        if (input.PaymentTermsDays < 0) throw new PostingException("Payment terms cannot be negative.");
        if (input.CreditLimit < 0) throw new PostingException("Credit limit cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();

        // Next code = max numeric suffix + 1 (seed uses C-0001..C-0003).
        var codes = await db.Customers.Select(c => c.Code).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("C-") && int.TryParse(code.AsSpan(2), out var n) && n > max)
                max = n;

        var customer = new Customer
        {
            Code = $"C-{max + 1:0000}",
            Name = input.Name.Trim(),
            Group = string.IsNullOrWhiteSpace(input.Group) ? null : input.Group.Trim(),
            Currency = string.IsNullOrWhiteSpace(input.Currency) ? "AED" : input.Currency.Trim(),
            CreditLimit = input.CreditLimit,
            Trn = input.Trn?.Trim(),
            Email = input.Email?.Trim(),
            Phone = input.Phone?.Trim(),
            Address = input.Address?.Trim(),
            PaymentTermsDays = input.PaymentTermsDays,
            Salesperson = string.IsNullOrWhiteSpace(input.Salesperson) ? null : input.Salesperson.Trim(),
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    }

    /// <summary>The next customer code that will be assigned (for display on the New form).</summary>
    public async Task<string> PeekNextCodeAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var codes = await db.Customers.Select(c => c.Code).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("C-") && int.TryParse(code.AsSpan(2), out var n) && n > max)
                max = n;
        return $"C-{max + 1:0000}";
    }

    /// <summary>All customers with invoiced / received / outstanding totals from posted documents.</summary>
    public async Task<List<CustomerSummary>> GetSummariesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
        var invoices = await PostedInvoicesAsync(db);
        var receipts = await PostedReceiptsAsync(db);
        var credits = await PostedCreditNotesAsync(db);

        return customers.Select(c =>
        {
            var invoiced = invoices.Where(i => i.CustomerId == c.Id).Sum(i => i.TotalGross);
            var received = receipts.Where(r => r.CustomerId == c.Id).Sum(r => r.Amount);
            var credited = credits.Where(n => n.CustomerId == c.Id).Sum(n => n.TotalGross);
            return new CustomerSummary(c.Id, c.Code, c.Name, c.Trn, c.PaymentTermsDays,
                invoiced, received, invoiced - received - credited, c.Salesperson);
        }).ToList();
    }

    /// <summary>Chronological invoice/receipt statement with running balance.</summary>
    public async Task<List<StatementRow>> GetStatementAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.CustomerId == customerId);
        var receipts = (await PostedReceiptsAsync(db)).Where(r => r.CustomerId == customerId);
        var credits = (await PostedCreditNotesAsync(db)).Where(n => n.CustomerId == customerId);

        var rows = invoices
            .Select(i => (i.Date, DocNo: i.InvoiceNo, DocType: "Invoice",
                Narration: i.Narration ?? "", Debit: i.TotalGross, Credit: 0m))
            .Concat(receipts.Select(r => (r.Date, DocNo: r.ReceiptNo, DocType: "Receipt",
                Narration: r.Narration ?? "", Debit: 0m, Credit: r.Amount)))
            .Concat(credits.Select(n => (n.Date, DocNo: n.CreditNoteNo, DocType: "Credit Note",
                Narration: n.Narration ?? n.Reason ?? "", Debit: 0m, Credit: n.TotalGross)))
            .OrderBy(r => r.Date).ThenBy(r => r.DocNo)
            .ToList();

        var result = new List<StatementRow>();
        var running = 0m;
        foreach (var r in rows)
        {
            running += r.Debit - r.Credit;
            result.Add(new StatementRow(r.Date, r.DocNo, r.DocType, r.Narration, r.Debit, r.Credit, running));
        }
        return result;
    }

    /// <summary>
    /// AR aging as of a date. Each open invoice's outstanding (gross − allocated receipts)
    /// is bucketed by days past its due date; on-account receipts appear as unallocated credits.
    /// </summary>
    public async Task<List<AgingRow>> GetAgingAsync(DateOnly asOf)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var customers = await db.Customers.AsNoTracking().OrderBy(c => c.Code).ToListAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.Date <= asOf).ToList();
        var receipts = (await PostedReceiptsAsync(db)).Where(r => r.Date <= asOf).ToList();
        var credits = (await PostedCreditNotesAsync(db)).Where(n => n.Date <= asOf).ToList();
        var (allocatedByInvoice, receiptIdsWithAllocations) = await AllocationsForAsync(db, receipts.Select(r => r.Id).ToList());

        var rows = new List<AgingRow>();
        foreach (var c in customers)
        {
            decimal current = 0, b30 = 0, b60 = 0, b90 = 0, over = 0;
            foreach (var inv in invoices.Where(i => i.CustomerId == c.Id))
            {
                var allocated = allocatedByInvoice.GetValueOrDefault(inv.Id)
                                + credits.Where(n => n.SalesInvoiceId == inv.Id).Sum(n => n.TotalGross);
                var outstanding = inv.TotalGross - allocated;
                if (outstanding <= 0) continue;

                var daysPastDue = asOf.DayNumber - inv.DueDate.DayNumber;
                if (daysPastDue <= 0) current += outstanding;
                else if (daysPastDue <= 30) b30 += outstanding;
                else if (daysPastDue <= 60) b60 += outstanding;
                else if (daysPastDue <= 90) b90 += outstanding;
                else over += outstanding;
            }

            // A receipt is either 100% on-account (no allocations at all) or 100% applied (its
            // allocations sum to exactly its Amount — no partial/overpayment state is allowed),
            // so "unallocated" is simply "has zero allocation rows", not merely SalesInvoiceId == null
            // (a receipt spanning multiple invoices also has SalesInvoiceId == null but is fully applied).
            var unallocated = receipts
                .Where(r => r.CustomerId == c.Id && !receiptIdsWithAllocations.Contains(r.Id))
                .Sum(r => r.Amount)
                + credits.Where(n => n.CustomerId == c.Id && n.SalesInvoiceId == null).Sum(n => n.TotalGross);

            if (current + b30 + b60 + b90 + over > 0 || unallocated > 0)
                rows.Add(new AgingRow(c.Code, c.Name, current, b30, b60, b90, over, unallocated));
        }
        return rows;
    }

    /// <summary>Posted invoices for a customer that still have money owing.</summary>
    public async Task<List<OpenInvoice>> GetOpenInvoicesAsync(int customerId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.CustomerId == customerId).ToList();
        var receipts = (await PostedReceiptsAsync(db)).Where(r => r.CustomerId == customerId).ToList();
        var credits = (await PostedCreditNotesAsync(db)).Where(n => n.CustomerId == customerId).ToList();
        var (allocatedByInvoice, _) = await AllocationsForAsync(db, receipts.Select(r => r.Id).ToList());

        return invoices
            .Select(i =>
            {
                var allocated = allocatedByInvoice.GetValueOrDefault(i.Id)
                                + credits.Where(n => n.SalesInvoiceId == i.Id).Sum(n => n.TotalGross);
                var billableLineCount = i.Lines.Count(l => l.Net > 0);
                return new OpenInvoice(i.Id, i.InvoiceNo, i.Date, i.DueDate, i.TotalGross, i.TotalGross - allocated, billableLineCount);
            })
            .Where(o => o.Outstanding > 0)
            .OrderBy(o => o.Date)
            .ToList();
    }

    private static Task<List<SalesInvoice>> PostedInvoicesAsync(AegisDbContext db) =>
        db.SalesInvoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Status == VoucherStatus.Posted).ToListAsync();

    private static Task<List<CustomerReceipt>> PostedReceiptsAsync(AegisDbContext db) =>
        db.CustomerReceipts.AsNoTracking()
            .Where(r => r.Status == VoucherStatus.Posted).ToListAsync();

    private static Task<List<CreditNote>> PostedCreditNotesAsync(AegisDbContext db) =>
        db.CreditNotes.AsNoTracking().Include(n => n.Lines)
            .Where(n => n.Status == VoucherStatus.Posted).ToListAsync();

    /// <summary>
    /// For a set of (already Posted-filtered) receipt ids: how much is allocated to each invoice
    /// they touch, and which of those receipt ids have any allocation at all (vs. purely on account).
    /// A receipt's <see cref="CustomerReceipt.SalesInvoiceId"/> is never used here — it can't
    /// represent a receipt spanning multiple invoices, so <see cref="ReceiptAllocation"/> (joined
    /// through the line it targets) is the only correct source of truth.
    /// </summary>
    private static async Task<(Dictionary<int, decimal> AllocatedByInvoice, HashSet<int> ReceiptIdsWithAllocations)>
        AllocationsForAsync(AegisDbContext db, List<int> receiptIds)
    {
        var allocations = await db.ReceiptAllocations.AsNoTracking()
            .Where(a => receiptIds.Contains(a.CustomerReceiptId))
            .Select(a => new { a.CustomerReceiptId, InvoiceId = a.SalesInvoiceLine.SalesInvoiceId, a.Amount })
            .ToListAsync();

        var byInvoice = allocations.GroupBy(a => a.InvoiceId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        var receiptsWithAllocations = allocations.Select(a => a.CustomerReceiptId).ToHashSet();
        return (byInvoice, receiptsWithAllocations);
    }
}
