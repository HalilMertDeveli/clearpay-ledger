using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClearPay.Domain.Identity;
using ClearPay.Infrastructure.Identity;
using ClearPay.Tests.Support;
using FluentAssertions;

namespace ClearPay.Tests;

public sealed class PspSimulatorAdminApiTests : IClassFixture<ClearPayWebFactory>
{
    private readonly ClearPayWebFactory _factory;

    public PspSimulatorAdminApiTests(ClearPayWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_can_drop_settlement_files_for_a_date_customers_cannot()
    {
        var client = _factory.CreateClient();
        var customer = await ApiSession.RegisterAsync(client, "psp-customer");
        using (var denied = await ApiSession.PostJsonAsync(client, customer.Token, "/api/admin/psp/settlement-files?date=2026-11-08", "{}", null))
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var token = await client.PostAsJsonAsync("/api/token", new
        {
            email = IdentitySeeder.DevelopmentAdminEmail,
            password = ClearPayWebFactory.SeedAdminPassword,
            accountKind = AccountKinds.Bireysel
        });
        var jwt = JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString();

        using var ok = await ApiSession.PostJsonAsync(client, jwt, "/api/admin/psp/settlement-files?date=2026-11-08", "{}", null);
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ok.Content.ReadAsStringAsync()).Should()
            .Contain("simbank-rest_20261108.csv").And.Contain("simbank-soap_20261108.csv").And.Contain("simpay_20261108.csv");

        using var bad = await ApiSession.PostJsonAsync(client, jwt, "/api/admin/psp/settlement-files?date=08.11.2026", "{}", null);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
