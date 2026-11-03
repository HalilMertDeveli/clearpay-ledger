using ClearPay.Domain.Ledger;
using FluentValidation;

namespace ClearPay.Application.Transfers;

public sealed class TransferCommandValidator : AbstractValidator<TransferCommand>
{
    /// <summary>Same limit as the web form (<c>maxlength="140"</c>).</summary>
    public const int DescriptionMaxLength = 140;

    public TransferCommandValidator()
    {
        RuleFor(x => x.ActorUserId).NotEmpty();
        RuleFor(x => x.Recipient)
            .NotEmpty()
            .EmailAddress();
        RuleFor(x => x.Amount)
            .GreaterThan(0m)
            .Must(a => decimal.Round(a, 2) == a)
            .WithMessage("Amount must have at most 2 decimal places.");
        RuleFor(x => x.IdempotencyKey)
            .NotEmpty()
            .MaximumLength(LedgerSchema.IdempotencyKeyMaxLength);
        RuleFor(x => x.Description).MaximumLength(DescriptionMaxLength);
    }
}
