using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ClearPay.Tests.Support;
using FluentAssertions;

namespace ClearPay.Tests;

/// <summary>
/// The whole browser round trip over real HTTP: merchant API -> fake ACS page (HTML + antiforgery)
/// -> auto-posted signed CRes -> gateway callback -> session status.
/// </summary>
public sealed class ThreeDsApiTests : IClassFixture<ClearPayWebFactory>
{
    private readonly ClearPayWebFactory _factory;

    public ThreeDsApiTests(ClearPayWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Merchant_to_acs_to_callback_authenticates_the_session()
    {
        var client = _factory.CreateClient();
        var user = await ApiSession.RegisterAsync(client, "threeds");

        var (sessionId, acsUrl) = await InitiateAsync(client, user, "4000 0000 0000 0010");
        var cres = await CompleteAcsAsync(client, acsUrl, "123456");
        cres["transStatus"].Should().Be("Y");

        using var callback = await client.PostAsync("/api/3ds/callback", new FormUrlEncodedContent(cres));
        callback.StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOfAsync(client, user, sessionId)).Should().Be("Authenticated");

        // Browser back button / double submit: the same signed result is accepted once.
        using var again = await client.PostAsync("/api/3ds/callback", new FormUrlEncodedContent(cres));
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await again.Content.ReadAsStringAsync()).Should().Contain("AlreadyApplied");
    }

    [Fact]
    public async Task Forged_callback_is_401_and_session_stays_in_challenge()
    {
        var client = _factory.CreateClient();
        var user = await ApiSession.RegisterAsync(client, "threeds-forge");
        var (sessionId, acsUrl) = await InitiateAsync(client, user, "4000000000000010");
        var cres = await CompleteAcsAsync(client, acsUrl, "000000");
        cres["transStatus"].Should().Be("N");

        cres["transStatus"] = "Y";
        cres["authenticationValue"] = "forged";
        using var forged = await client.PostAsync("/api/3ds/callback", new FormUrlEncodedContent(cres));

        forged.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StatusOfAsync(client, user, sessionId)).Should().Be("Challenge");
    }

    [Fact]
    public async Task Tampered_creq_is_refused_by_the_acs_page()
    {
        var client = _factory.CreateClient();
        var user = await ApiSession.RegisterAsync(client, "threeds-creq");
        var (_, acsUrl) = await InitiateAsync(client, user, "4000000000000010");

        using var response = await client.GetAsync(acsUrl[..^3] + "abc");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Initiate_requires_jwt_and_key_and_hides_sessions_from_others()
    {
        var client = _factory.CreateClient();
        var owner = await ApiSession.RegisterAsync(client, "threeds-owner");
        var stranger = await ApiSession.RegisterAsync(client, "threeds-stranger");
        var body = JsonSerializer.Serialize(new { amount = 10m, cardNumber = "4000000000000010" });

        (await ApiSession.PostJsonAsync(client, null, "/api/3ds/sessions", body, "k1")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
        (await ApiSession.PostJsonAsync(client, owner.Token, "/api/3ds/sessions", body, null)).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);

        var key = ApiSession.NewKey("3ds");
        var (sessionId, _) = await InitiateAsync(client, owner, "4000000000000010", key);
        var replay = await ApiSession.PostJsonAsync(client, owner.Token, "/api/3ds/sessions", body, key);
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await replay.Content.ReadAsStringAsync()).Should().Contain(sessionId.ToString());

        using var hidden = await ApiSession.GetAsync(client, stranger.Token, $"/api/3ds/sessions/{sessionId}");
        hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<(Guid SessionId, string AcsUrl)> InitiateAsync(
        HttpClient client, TestUser user, string card, string? key = null)
    {
        using var response = await ApiSession.PostJsonAsync(
            client,
            user.Token,
            "/api/3ds/sessions",
            JsonSerializer.Serialize(new { amount = 42.10m, cardNumber = card }),
            key ?? ApiSession.NewKey("3ds"));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("Challenge");
        return (doc.RootElement.GetProperty("sessionId").GetGuid(), doc.RootElement.GetProperty("acsUrl").GetString()!);
    }

    private static async Task<Dictionary<string, string>> CompleteAcsAsync(HttpClient client, string acsUrl, string otp)
    {
        var page = await client.GetStringAsync(acsUrl);
        page.Should().Contain("SimBank").And.Contain("42.10").And.Contain("0010");
        var antiforgery = InputValue(page, "__RequestVerificationToken");
        var creq = Uri.UnescapeDataString(acsUrl["/fake-acs?creq=".Length..]);

        using var post = await client.PostAsync("/fake-acs", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Creq"] = creq,
            ["Otp"] = otp,
            ["action"] = "submit",
            ["__RequestVerificationToken"] = antiforgery
        }));
        post.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await post.Content.ReadAsStringAsync();
        html.Should().Contain("action=\"/api/3ds/callback\"");

        return new[] { "sessionId", "transStatus", "eci", "authenticationValue", "issuedAt", "nonce", "signature" }
            .ToDictionary(n => n, n => WebUtility.HtmlDecode(InputValue(html, n)));
    }

    private static async Task<string> StatusOfAsync(HttpClient client, TestUser user, Guid sessionId)
    {
        using var response = await ApiSession.GetAsync(client, user.Token, $"/api/3ds/sessions/{sessionId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("status").GetString()!;
    }

    private static string InputValue(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"");
        match.Success.Should().BeTrue($"the page should contain an input named {name}");
        return match.Groups[1].Value;
    }
}
