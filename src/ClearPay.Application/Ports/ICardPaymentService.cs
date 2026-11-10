using ClearPay.Application.Payments;

namespace ClearPay.Application.Ports;

/// <summary>
/// Card payments that fund the wallet: authorize (needs an Authenticated 3-D Secure session),
/// capture, refund (full or partial) and void. Every call is idempotent on its key.
/// </summary>
public interface ICardPaymentService
{
    Task<PaymentResult> CreateAsync(CreateCardPaymentCommand command, CancellationToken cancellationToken = default);

    Task<PaymentResult> CaptureAsync(PaymentActionCommand command, CancellationToken cancellationToken = default);

    Task<PaymentResult> RefundAsync(PaymentActionCommand command, CancellationToken cancellationToken = default);

    Task<PaymentResult> VoidAsync(PaymentActionCommand command, CancellationToken cancellationToken = default);

    /// <summary>Owner only; null for anyone else (404).</summary>
    Task<CardPaymentView?> GetAsync(Guid paymentId, string userId, CancellationToken cancellationToken = default);
}
