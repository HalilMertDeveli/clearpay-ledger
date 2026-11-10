using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClearPay.Tests.Support;
using FluentAssertions;

namespace ClearPay.Tests;

/// <summary>3-D Secure -> PSP authorization + capture -> partial refund -> void, over HTTP like a client would.</summary>
public sealed class CardPaymentApiTests : IClassFixture<ClearPayWebFactory>
{
    private readonly ClearPayWebFactory _factory;

    public CardPaymentApiTests(ClearPayWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Card_payment_funds_the_wallet_and_a_partial_refund_takes_it_back()
    {
        var client = _factory.CreateClient();
        var user = await ApiSession.RegisterAsync(client, "card");
        var session = await AuthenticatedSessionAsync(client, user, 75m);

        using var created = await ApiSession.PostJsonAsync(client, user.Token, "/api/payments",
            JsonSerializer.Serialize(new { threeDsSessionId = session, provider = "simbank-soap" }), "pay-" + Guid.NewGuid());
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var paymentId = Json(await created.Content.ReadAsStringAsync()).GetProperty("paymentId").GetGuid();
        (await ApiSession.BalanceAsync(client, user)).Should().Be(75m);

        var refundKey = "refund-" + Guid.NewGuid();
        using var refund = await ApiSession.PostJsonAsync(client, user.Token, $"/api/payments/{paymentId}/refunds",
            JsonSerializer.Serialize(new { amount = 25m }), refundKey);
        refund.StatusCode.Should().Be(HttpStatusCode.OK);
        Json(await refund.Content.ReadAsStringAsync()).GetProperty("status").GetString().Should().Be("PartiallyRefunded");

        using var replay = await ApiSession.PostJsonAsync(client, user.Token, $"/api/payments/{paymentId}/refunds",
            JsonSerializer.Serialize(new { amount = 25m }), refundKey);
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);

        using var tooMuch = await ApiSession.PostJsonAsync(client, user.Token, $"/api/payments/{paymentId}/refunds",
            JsonSerializer.Serialize(new { amount = 60m }), "refund-" + Guid.NewGuid());
        tooMuch.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        (await ApiSession.BalanceAsync(client, user)).Should().Be(50m);

        var stranger = await ApiSession.RegisterAsync(client, "card-stranger");
        using var hidden = await ApiSession.GetAsync(client, stranger.Token, $"/api/payments/{paymentId}");
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Authorize_only_then_void_and_declines_are_402()
    {
        var client = _factory.CreateClient();
        var user = await ApiSession.RegisterAsync(client, "card-void");

        var session = await AuthenticatedSessionAsync(client, user, 40m);
        using var authorized = await ApiSession.PostJsonAsync(client, user.Token, "/api/payments",
            JsonSerializer.Serialize(new { threeDsSessionId = session, capture = false }), "auth-" + Guid.NewGuid());
        authorized.StatusCode.Should().Be(HttpStatusCode.Created);
        var paymentId = Json(await authorized.Content.ReadAsStringAsync()).GetProperty("paymentId").GetGuid();

        using var voided = await ApiSession.PostJsonAsync(client, user.Token, $"/api/payments/{paymentId}/void", "{}", "void-" + Guid.NewGuid());
        voided.StatusCode.Should().Be(HttpStatusCode.OK);
        Json(await voided.Content.ReadAsStringAsync()).GetProperty("status").GetString().Should().Be("Voided");
        (await ApiSession.BalanceAsync(client, user)).Should().Be(0m);

        var declinedSession = await AuthenticatedSessionAsync(client, user, 10.51m);
        using var declined = await ApiSession.PostJsonAsync(client, user.Token, "/api/payments",
            JsonSerializer.Serialize(new { threeDsSessionId = declinedSession }), "dec-" + Guid.NewGuid());
        declined.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        (await declined.Content.ReadAsStringAsync()).Should().Contain("\"declineCode\":\"51\"");
    }

    private static async Task<Guid> AuthenticatedSessionAsync(HttpClient client, TestUser user, decimal amount)
    {
        using var start = await ApiSession.PostJsonAsync(client, user.Token, "/api/3ds/sessions",
            JsonSerializer.Serialize(new { amount, cardNumber = "4000000000000010" }), "3ds-" + Guid.NewGuid());
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = Json(await start.Content.ReadAsStringAsync());
        var acsUrl = body.GetProperty("acsUrl").GetString()!;

        var page = await client.GetStringAsync(acsUrl);
        using var post = await client.PostAsync("/fake-acs", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Creq"] = Uri.UnescapeDataString(acsUrl["/fake-acs?creq=".Length..]),
            ["Otp"] = "123456",
            ["action"] = "submit",
            ["__RequestVerificationToken"] = Input(page, "__RequestVerificationToken")
        }));
        var html = await post.Content.ReadAsStringAsync();
        var cres = new[] { "sessionId", "transStatus", "eci", "authenticationValue", "issuedAt", "nonce", "signature" }
            .ToDictionary(n => n, n => WebUtility.HtmlDecode(Input(html, n)));
        using var callback = await client.PostAsync("/api/3ds/callback", new FormUrlEncodedContent(cres));
        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        return body.GetProperty("sessionId").GetGuid();
    }

    private static string Input(string html, string name) =>
        Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
