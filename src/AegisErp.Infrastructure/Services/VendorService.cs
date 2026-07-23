using AegisErp.Domain;
using AegisErp.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace AegisErp.Infrastructure.Services;

/// <summary>Everything the "New Vendor" form collects.</summary>
public record NewVendorInput(
    string Name, string? Group, string Currency,
    int PaymentTermsDays, string? Trn, string? Email, string? Phone, string? Address);

public class VendorService
{
    private readonly IDbContextFactory<AegisDbContext> _dbf;
    public VendorService(IDbContextFactory<AegisDbContext> dbf) => _dbf = dbf;

    public async Task<List<Vendor>> GetAllAsync(bool activeOnly = false)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var q = db.Vendors.AsNoTracking().OrderBy(v => v.Code).AsQueryable();
        if (activeOnly) q = q.Where(v => v.IsActive);
        return await q.ToListAsync();
    }

    public async Task<Vendor> CreateAsync(NewVendorInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name)) throw new PostingException("Vendor name is required.");
        if (input.PaymentTermsDays < 0) throw new PostingException("Payment terms cannot be negative.");

        await using var db = await _dbf.CreateDbContextAsync();

        var vendor = new Vendor
        {
            Code = await NextCodeAsync(db),
            Name = input.Name.Trim(),
            Group = string.IsNullOrWhiteSpace(input.Group) ? null : input.Group.Trim(),
            Currency = string.IsNullOrWhiteSpace(input.Currency) ? "AED" : input.Currency.Trim(),
            Trn = input.Trn?.Trim(),
            Email = input.Email?.Trim(),
            Phone = input.Phone?.Trim(),
            Address = input.Address?.Trim(),
            PaymentTermsDays = input.PaymentTermsDays,
        };
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        return vendor;
    }

    /// <summary>The next vendor code that will be assigned (for display on the New form).</summary>
    public async Task<string> PeekNextCodeAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        return await NextCodeAsync(db);
    }

    private static async Task<string> NextCodeAsync(AegisDbContext db)
    {
        var codes = await db.Vendors.Select(v => v.Code).ToListAsync();
        var max = 0;
        foreach (var code in codes)
            if (code.StartsWith("V-") && int.TryParse(code.AsSpan(2), out var n) && n > max)
                max = n;
        return $"V-{max + 1:0000}";
    }

    /// <summary>All vendors with billed / paid / outstanding totals from posted documents.</summary>
    public async Task<List<VendorSummary>> GetSummariesAsync()
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var vendors = await db.Vendors.AsNoTracking().OrderBy(v => v.Code).ToListAsync();
        var invoices = await PostedInvoicesAsync(db);
        var payments = await PostedPaymentsAsync(db);
        var debits = await PostedDebitNotesAsync(db);

        return vendors.Select(v =>
        {
            var billed = invoices.Where(i => i.VendorId == v.Id).Sum(i => i.TotalGross);
            var paid = payments.Where(p => p.VendorId == v.Id).Sum(p => p.Amount)
                       + debits.Where(d => d.VendorId == v.Id).Sum(d => d.TotalGross);
            return new VendorSummary(v.Id, v.Code, v.Name, v.Trn, v.PaymentTermsDays,
                billed, paid, billed - paid);
        }).ToList();
    }

    /// <summary>Chronological purchase-invoice / payment / debit-note statement with running balance (what we owe).</summary>
    public async Task<List<StatementRow>> GetStatementAsync(int vendorId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.VendorId == vendorId);
        var payments = (await PostedPaymentsAsync(db)).Where(p => p.VendorId == vendorId);
        var debits = (await PostedDebitNotesAsync(db)).Where(d => d.VendorId == vendorId);

        // Credit = we owe more (invoice); Debit = we owe less (payment / debit note).
        var rows = invoices
            .Select(i => (i.Date, DocNo: i.InvoiceNo, DocType: "Purchase Invoice",
                Narration: i.Narration ?? "", Debit: 0m, Credit: i.TotalGross))
            .Concat(payments.Select(p => (p.Date, DocNo: p.PaymentNo, DocType: "Payment",
                Narration: p.Narration ?? "", Debit: p.Amount, Credit: 0m)))
            .Concat(debits.Select(d => (d.Date, DocNo: d.DebitNoteNo, DocType: "Debit Note",
                Narration: d.Narration ?? d.Reason ?? "", Debit: d.TotalGross, Credit: 0m)))
            .OrderBy(r => r.Date).ThenBy(r => r.DocNo)
            .ToList();

        var result = new List<StatementRow>();
        var running = 0m;
        foreach (var r in rows)
        {
            running += r.Credit - r.Debit; // payable grows on credit
            result.Add(new StatementRow(r.Date, r.DocNo, r.DocType, r.Narration, r.Debit, r.Credit, running));
        }
        return result;
    }

    /// <summary>
    /// AP aging as of a date. Each open purchase invoice's outstanding (gross − allocated payments −
    /// allocated debit notes) is bucketed by days past its due date; unallocated payments/debit notes
    /// appear as debits.
    /// </summary>
    public async Task<List<ApAgingRow>> GetAgingAsync(DateOnly asOf)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var vendors = await db.Vendors.AsNoTracking().OrderBy(v => v.Code).ToListAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.Date <= asOf).ToList();
        var payments = (await PostedPaymentsAsync(db)).Where(p => p.Date <= asOf).ToList();
        var debits = (await PostedDebitNotesAsync(db)).Where(d => d.Date <= asOf).ToList();

        var rows = new List<ApAgingRow>();
        foreach (var v in vendors)
        {
            decimal current = 0, b30 = 0, b60 = 0, b90 = 0, over = 0;
            foreach (var inv in invoices.Where(i => i.VendorId == v.Id))
            {
                var allocated = payments.Where(p => p.PurchaseInvoiceId == inv.Id).Sum(p => p.Amount)
                                + debits.Where(d => d.PurchaseInvoiceId == inv.Id).Sum(d => d.TotalGross);
                var outstanding = inv.TotalGross - allocated;
                if (outstanding <= 0) continue;

                var daysPastDue = asOf.DayNumber - inv.DueDate.DayNumber;
                if (daysPastDue <= 0) current += outstanding;
                else if (daysPastDue <= 30) b30 += outstanding;
                else if (daysPastDue <= 60) b60 += outstanding;
                else if (daysPastDue <= 90) b90 += outstanding;
                else over += outstanding;
            }

            var unallocated = payments.Where(p => p.VendorId == v.Id && p.PurchaseInvoiceId == null).Sum(p => p.Amount)
                              + debits.Where(d => d.VendorId == v.Id && d.PurchaseInvoiceId == null).Sum(d => d.TotalGross);

            if (current + b30 + b60 + b90 + over > 0 || unallocated > 0)
                rows.Add(new ApAgingRow(v.Code, v.Name, current, b30, b60, b90, over, unallocated));
        }
        return rows;
    }

    /// <summary>Posted purchase invoices for a vendor that still have money owing.</summary>
    public async Task<List<OpenPurchaseInvoice>> GetOpenInvoicesAsync(int vendorId)
    {
        await using var db = await _dbf.CreateDbContextAsync();
        var invoices = (await PostedInvoicesAsync(db)).Where(i => i.VendorId == vendorId).ToList();
        var payments = (await PostedPaymentsAsync(db)).Where(p => p.VendorId == vendorId).ToList();
        var debits = (await PostedDebitNotesAsync(db)).Where(d => d.VendorId == vendorId).ToList();

        return invoices
            .Select(i =>
            {
                var allocated = payments.Where(p => p.PurchaseInvoiceId == i.Id).Sum(p => p.Amount)
                                + debits.Where(d => d.PurchaseInvoiceId == i.Id).Sum(d => d.TotalGross);
                return new OpenPurchaseInvoice(i.Id, i.InvoiceNo, i.Date, i.DueDate, i.TotalGross, i.TotalGross - allocated);
            })
            .Where(o => o.Outstanding > 0)
            .OrderBy(o => o.Date)
            .ToList();
    }

    private static Task<List<PurchaseInvoice>> PostedInvoicesAsync(AegisDbContext db) =>
        db.PurchaseInvoices.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.Status == VoucherStatus.Posted).ToListAsync();

    private static Task<List<VendorPayment>> PostedPaymentsAsync(AegisDbContext db) =>
        db.VendorPayments.AsNoTracking()
            .Where(p => p.Status == VoucherStatus.Posted).ToListAsync();

    private static Task<List<DebitNote>> PostedDebitNotesAsync(AegisDbContext db) =>
        db.DebitNotes.AsNoTracking().Include(d => d.Lines)
            .Where(d => d.Status == VoucherStatus.Posted).ToListAsync();
}
