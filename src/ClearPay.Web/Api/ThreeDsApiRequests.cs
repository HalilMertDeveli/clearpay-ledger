namespace ClearPay.Web.Api;

/// <summary>Use a Luhn-valid test number (for example 4000 0000 0000 0010). The PAN is never stored or logged.</summary>
public sealed class ThreeDsInitiateApiRequest
{
    public decimal Amount { get; set; }

    public string? CardNumber { get; set; }
}

/// <summary>CRes fields as the fake ACS page posts them (form fields).</summary>
public sealed class ThreeDsCallbackForm
{
    public string? SessionId { get; set; }

    public string? TransStatus { get; set; }

    public string? Eci { get; set; }

    public string? AuthenticationValue { get; set; }

    public long IssuedAt { get; set; }

    public string? Nonce { get; set; }

    public string? Signature { get; set; }
}
