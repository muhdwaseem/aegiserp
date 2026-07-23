namespace AegisErp.Domain;

/// <summary>Raised when a voucher violates double-entry rules during posting.</summary>
public class PostingException : Exception
{
    public PostingException(string message) : base(message) { }
}
