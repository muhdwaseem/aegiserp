namespace AegisErp.Domain.Entities;

/// <summary>
/// A double-entry voucher: a header plus a set of debit/credit lines that must balance
/// before it can be posted to the ledger.
/// </summary>
public class JournalVoucher : ICompanyScoped
{
    public int Id { get; set; }

    /// <summary>Owning company. Each company has its own ledger and numbering sequence.</summary>
    public int CompanyId { get; set; }

    /// <summary>Generated document number, e.g. "JV-2026-0001". Unique within the company.</summary>
    public string VoucherNo { get; set; } = string.Empty;

    public VoucherType Type { get; set; } = VoucherType.Journal;
    public VoucherStatus Status { get; set; } = VoucherStatus.Draft;

    public DateOnly Date { get; set; }
    public string? Narration { get; set; }
    public string? Reference { get; set; }

    public int FiscalPeriodId { get; set; }
    public FiscalPeriod FiscalPeriod { get; set; } = null!;

    public string CreatedBy { get; set; } = "System Admin";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    public List<JournalLine> Lines { get; set; } = new();

    public decimal TotalDebit => Lines.Sum(l => l.Debit);
    public decimal TotalCredit => Lines.Sum(l => l.Credit);
    public decimal Difference => TotalDebit - TotalCredit;
    public bool IsBalanced => Difference == 0m && TotalDebit > 0m;

    /// <summary>
    /// Validates double-entry rules and transitions the voucher to Posted.
    /// Throws <see cref="PostingException"/> if the voucher is not in a postable state.
    /// </summary>
    public void Post(DateTime nowUtc)
    {
        if (Status == VoucherStatus.Posted)
            throw new PostingException("Voucher is already posted.");
        if (Status == VoucherStatus.Void)
            throw new PostingException("A void voucher cannot be posted.");
        if (Lines.Count < 2)
            throw new PostingException("A voucher needs at least two lines.");

        foreach (var line in Lines)
        {
            if (line.AccountId == 0)
                throw new PostingException($"Line {line.LineNo} has no account selected.");
            if (line.Debit < 0 || line.Credit < 0)
                throw new PostingException($"Line {line.LineNo} has a negative amount.");
            if (line.Debit > 0 && line.Credit > 0)
                throw new PostingException($"Line {line.LineNo} cannot be both debit and credit.");
            if (line.Debit == 0 && line.Credit == 0)
                throw new PostingException($"Line {line.LineNo} has no amount.");
        }

        if (TotalDebit != TotalCredit)
            throw new PostingException(
                $"Voucher is out of balance by {Math.Abs(Difference):N2} (Dr {TotalDebit:N2} / Cr {TotalCredit:N2}).");

        Status = VoucherStatus.Posted;
        PostedAtUtc = nowUtc;
    }
}
