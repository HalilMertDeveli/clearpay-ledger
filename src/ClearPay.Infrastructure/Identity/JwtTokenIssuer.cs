using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ClearPay.Application.Ports;
using ClearPay.Domain.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace ClearPay.Infrastructure.Identity;

public sealed class JwtTokenIssuer : IJwtTokenIssuer
{
    /// <summary>Development-only fallback. Public placeholder, not a secret; never used outside Development.</summary>
    public const string DevelopmentSigningKey = "dev-only-placeholder-jwt-key-NOT-A-SECRET-0000";

    private readonly SymmetricSecurityKey _signingKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly IClock _clock;

    public JwtTokenIssuer(IConfiguration configuration, IHostEnvironment environment, IClock clock)
    {
        _clock = clock;
        var parameters = CreateValidationParameters(configuration, environment);
        _issuer = parameters.ValidIssuer!;
        _audience = parameters.ValidAudience!;
        _signingKey = (SymmetricSecurityKey)parameters.IssuerSigningKey!;
    }

    public static TokenValidationParameters CreateValidationParameters(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var issuer = configuration["Jwt:Issuer"] ?? "ClearPay";
        var audience = configuration["Jwt:Audience"] ?? "ClearPay";
        var raw = configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Jwt:SigningKey is required outside Development (environment variable Jwt__SigningKey or a secret store). Do not commit a real key.");
            }

            raw = DevelopmentSigningKey;
        }

        var bytes = Encoding.UTF8.GetBytes(raw);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes.");
        }

        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(bytes),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    }

    public string Issue(string userId, string email, IReadOnlyList<string> roles, string? accountKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(roles);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Email, email),
            new(JwtRegisteredClaimNames.Email, email),
            new(ClaimTypes.Name, email),
            new(AccountKinds.JwtClaim, AccountKinds.Normalize(accountKind))
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            notBefore: _clock.UtcNow.UtcDateTime,
            expires: _clock.UtcNow.UtcDateTime.AddHours(8),
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
