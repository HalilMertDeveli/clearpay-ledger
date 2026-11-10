namespace ClearPay.Web.Api;

public sealed class CreatePaymentApiRequest
{
    /// <summary>An Authenticated session from POST /api/3ds/sessions.</summary>
    public Guid ThreeDsSessionId { get; set; }

    /// <summary>simbank-rest (default), simbank-soap or simpay.</summary>
    public string? Provider { get; set; }

    /// <summary>false = authorize only; capture or void later.</summary>
    public bool Capture { get; set; } = true;
}

public sealed class RefundApiRequest
{
    public decimal Amount { get; set; }
}
