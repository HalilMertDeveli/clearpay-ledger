using ClearPay.Application.Payments;
using Microsoft.Extensions.Configuration;

namespace ClearPay.Infrastructure.Payments.Psp;

/// <summary>Adding a provider = one more <see cref="IPaymentServiceProvider"/> registration; nothing else changes.</summary>
public sealed class PspRouter : IPspRouter
{
    private readonly Dictionary<string, IPaymentServiceProvider> _providers;
    private readonly string _default;

    public PspRouter(IEnumerable<IPaymentServiceProvider> providers, IConfiguration configuration)
    {
        _providers = providers.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        _default = configuration["Psp:Default"] ?? SimBankRestPsp.ProviderName;
    }

    public IReadOnlyCollection<string> Names => _providers.Keys;

    public IPaymentServiceProvider? Resolve(string? name) =>
        _providers.GetValueOrDefault(string.IsNullOrWhiteSpace(name) ? _default : name.Trim());
}
