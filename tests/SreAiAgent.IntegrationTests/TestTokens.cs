using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using SreAiAgent.Api.Security;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Mints signed JWTs for integration tests using the production validation path.
/// </summary>
internal static class TestTokens
{
    public const string Issuer = "https://sre-ai-agent.tests";
    public const string Audience = "sre-ai-agent-tests";

    // 32-byte key for HMAC-SHA256
    private static readonly byte[] KeyBytes = Encoding.ASCII.GetBytes("sre-ai-agent-test-key-0123456789");

    public static SymmetricSecurityKey SigningKey { get; } = new(KeyBytes);

    public static string For(string? tenant, params string[] roles) => ForActor("test-operator", tenant, roles);

    /// <summary>A token for a named person, for rules that compare who did what.</summary>
    public static string ForActor(string actor, string? tenant, params string[] roles)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, actor)];

        if (!string.IsNullOrWhiteSpace(tenant))
        {
            claims.Add(new(SreClaims.Tenant, tenant));
        }

        foreach (string role in roles)
        {
            claims.Add(new(ClaimTypes.Role, role));
        }

        JsonWebTokenHandler handler = new();
        SecurityTokenDescriptor descriptor = new()
        {
            Issuer = Issuer,
            Audience = Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(claims),
        };

        return handler.CreateToken(descriptor);
    }

    public static string WithoutTenant(params string[] roles) => For(null, roles);
}
