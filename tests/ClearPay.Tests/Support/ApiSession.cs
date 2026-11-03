using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClearPay.Application.Ports;
using ClearPay.Application.Wallets;
using FluentAssertions;

namespace ClearPay.Tests.Support;

/// <summary>A customer registered through the public JSON API.</summary>
internal sealed record TestUser(string Id, string Email, string Token);

/// <summary>
/// Drives the real HTTP surface (register, JWT, top-up, transfer, wallet) so tests
/// exercise the same path as the Flutter client instead of writing ledger rows by hand.
/// </summary>
internal static class ApiSession
{
    /// <summary>Test-only password used for accounts created by tests.</summary>
    public const string Password = "Deneme123";

    public static async Task<TestUser> RegisterAsync(HttpClient client, string prefix)
    {
        var email = $"{prefix}.{Guid.NewGuid():N}@clearpay.test";
        var response = await client.PostAsJsonAsync("/api/register", new
        {
            fullName = prefix,
            email,
            password = Password,
            confirmPassword = Password
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("access_token").GetString()!;
        return new TestUser(SubjectOf(token), email, token);
    }

    public static async Task TopUpAsync(HttpClient client, TestUser user, decimal amount)
    {
        var response = await PostJsonAsync(
            client,
            user.Token,
            "/api/topup",
            JsonSerializer.Serialize(new { amount, account = "****4242" }),
            "topup-" + Guid.NewGuid().ToString("N"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    public static Task<HttpResponseMessage> TransferAsync(
        HttpClient client,
        string? token,
        string key,
        string recipient,
        decimal amount,
        string? description = "test")
    {
        return PostJsonAsync(
            client,
            token,
            "/api/transfers",
            JsonSerializer.Serialize(new { recipient, amount, description }),
            key);
    }

    public static async Task<HttpResponseMessage> PostJsonAsync(
        HttpClient client,
        string? token,
        string url,
        string json,
        string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (idempotencyKey is not null)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> GetAsync(HttpClient client, string? token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    public static async Task<decimal> BalanceAsync(HttpClient client, TestUser user)
    {
        using var response = await GetAsync(client, user.Token, "/api/wallet");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("balance").GetDecimal();
    }

    public static string NewKey(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N");

    private static string SubjectOf(string jwt)
    {
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.GetProperty("sub").GetString()!;
    }
}

internal sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);
}

internal sealed class DictionaryUserDirectory : IUserDirectory
{
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Map(string email, string userId) => _map[email] = userId;

    public Task<string?> FindUserIdByEmailAsync(string email, CancellationToken cancellationToken = default) =>
        Task.FromResult(_map.TryGetValue(email.Trim(), out var id) ? id : null);

    public Task<string?> FindEmailByUserIdAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(_map.FirstOrDefault(p => p.Value == userId).Key);
}

internal sealed class NullWalletCache : IWalletSummaryCache
{
    public Task<WalletSummary?> GetAsync(string userId, CancellationToken cancellationToken = default) =>
        Task.FromResult<WalletSummary?>(null);

    public Task SetAsync(WalletSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task InvalidateAsync(string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class NullLiveNotifier : IWalletLiveNotifier
{
    public Task NotifyAsync(WalletLiveNotice notice, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
