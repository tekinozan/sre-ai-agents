using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace SreAiAgent.Api.Security;

/// <summary>
/// Authenticates every request as a local operator, for development only.
/// </summary>
/// <remarks>
/// This exists so the platform can be run end to end without standing up an
/// identity provider first, and for no other reason. It is registered only when
/// Security:AllowDevelopmentAuthentication is set, and Program.cs refuses to
/// start a Production host with that flag on; the check is there rather than
/// here because a host that fails to start is noticed during a deploy, while an
/// open API is noticed by whoever finds it.
///
/// ARCHITECTURE.md 15.1 rules out writing local password authentication. This is
/// not that: it validates nothing and grants a fixed identity, so there is no
/// bespoke credential handling to get wrong.
/// </remarks>
public sealed class DevelopmentAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The scheme name.</summary>
    public const string SchemeName = "Development";

    /// <summary>The tenant every development principal belongs to.</summary>
    public const string DevelopmentTenant = "local";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        ClaimsIdentity identity = new(
            [
                new Claim(ClaimTypes.NameIdentifier, "local-operator"),
                new Claim(ClaimTypes.Name, "local-operator"),
                new Claim(SreClaims.Tenant, DevelopmentTenant),
                new Claim(ClaimTypes.Role, SreRoles.Viewer),
                new Claim(ClaimTypes.Role, SreRoles.Investigator),
                new Claim(ClaimTypes.Role, SreRoles.Operator),
                new Claim(ClaimTypes.Role, SreRoles.Approver),
            ],
            SchemeName);

        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Claim types the platform reads off a principal.</summary>
public static class SreClaims
{
    /// <summary>
    /// The tenant every query is filtered by.
    /// </summary>
    /// <remarks>
    /// Taken from the token, never from a header or a route value. A tenant a
    /// caller can choose is not an isolation boundary, and every read query in
    /// the platform trusts this value to keep one customer's evidence away from
    /// another's.
    /// </remarks>
    public const string Tenant = "tenant";
}
