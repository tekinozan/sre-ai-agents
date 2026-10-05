using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SreAiAgent.Infrastructure.Configuration;
using ModelProviderTenantScope = SreAiAgent.Domain.ModelProviders.TenantScope;

namespace SreAiAgent.Api.Security;

/// <summary>Authentication and authorization wiring (ARCHITECTURE.md 15.1).</summary>
public static class SecurityDependencyInjection
{
    /// <summary>Registers JWT bearer authentication and the capability policies.</summary>
    /// <remarks>
    /// Standard OIDC middleware, never a hand-written token check. Token
    /// validation is the part of an authentication system that is easy to get
    /// subtly and catastrophically wrong.
    /// </remarks>
    public static IServiceCollection AddSreSecurity(
        this IServiceCollection services,
        SecurityOptions security,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(security);
        ArgumentNullException.ThrowIfNull(environment);

        if (security.AllowDevelopmentAuthentication && environment.IsProduction())
        {
            // The host does not start. A development convenience left on in
            // production is an unauthenticated API over incident data, and it is
            // the kind of flag that gets copied between environment files.
            throw new InvalidOperationException(
                "Security:AllowDevelopmentAuthentication cannot be enabled in Production.");
        }

        if (security.AllowDevelopmentAuthentication)
        {
            services
                .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
                .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions,
                    DevelopmentAuthenticationHandler>(DevelopmentAuthenticationHandler.SchemeName, null);
        }
        else
        {
            services
                .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(options =>
                {
                    options.Authority = security.Authority;
                    options.Audience = security.Audience;
                    options.MapInboundClaims = false;

                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidateAudience = true,
                        ValidateLifetime = true,
                        ValidateIssuerSigningKey = true,
                        RoleClaimType = ClaimTypes.Role,
                        NameClaimType = ClaimTypes.NameIdentifier,

                        // Tokens are minted by an identity provider whose clock is
                        // synchronized. The default five minutes is a five-minute
                        // window in which a revoked token still works.
                        ClockSkew = TimeSpan.FromSeconds(30),
                    };
                });
        }

        services
            .AddAuthorizationBuilder()
            .AddPolicy(SrePolicies.ReadIncidents, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(SreClaims.Tenant)
                .RequireRole(
                    SreRoles.Viewer,
                    SreRoles.Investigator,
                    SreRoles.Approver,
                    SreRoles.Operator,
                    SreRoles.PlatformAdmin))
            .AddPolicy(SrePolicies.WriteIncidents, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(SreClaims.Tenant)
                .RequireRole(SreRoles.Investigator, SreRoles.Operator, SreRoles.PlatformAdmin))
            .AddPolicy(SrePolicies.DecideApprovals, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(SreClaims.Tenant)
                .RequireRole(SreRoles.Approver, SreRoles.PlatformAdmin))
            .AddPolicy(SrePolicies.ManageModelProviders, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(SreClaims.Tenant)
                .RequireRole(SreRoles.Operator, SreRoles.PlatformAdmin))
            .AddPolicy(SrePolicies.ManageNotifications, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(SreClaims.Tenant)
                .RequireRole(SreRoles.Operator, SreRoles.PlatformAdmin));

        return services;
    }
}

/// <summary>Reads identity facts off the current principal.</summary>
public static class PrincipalExtensions
{
    /// <summary>
    /// The caller's tenant, taken from the validated token.
    /// </summary>
    /// <remarks>
    /// Every policy requires this claim, so an authorized request always has
    /// one. The empty fallback exists only so a bug here degrades into a query
    /// that matches nothing rather than one that matches everything.
    /// </remarks>
    public static string Tenant(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.FindFirstValue(SreClaims.Tenant) ?? string.Empty;
    }

    /// <summary>
    /// The caller's <see cref="ModelProviderTenantScope"/> (D40, F1-3),
    /// derived only from validated claims.
    /// </summary>
    /// <remarks>
    /// ADP-203: scope never comes from a request body, a query string or a
    /// header, so this is the only place a <see cref="ModelProviderTenantScope"/>
    /// is built for a caller. A missing <see cref="SreClaims.Product"/> or
    /// <see cref="SreClaims.Environment"/> claim is never a wildcard —
    /// <see cref="ModelProviderTenantScope.Create"/> turns its absence into
    /// <c>TenantWide</c>, a specific and narrow scope that matches only other
    /// <c>TenantWide</c> rows, never "every row in this tenant."
    /// </remarks>
    /// <exception cref="SreAiAgent.Domain.Shared.DomainException">
    /// The caller holds an <see cref="SreClaims.Environment"/> claim without a
    /// <see cref="SreClaims.Product"/> claim. D40 has no scope kind for that
    /// combination, so it is rejected here, at construction, before it can
    /// reach anything that matches a scope against a row.
    /// </exception>
    public static ModelProviderTenantScope TenantScope(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return ModelProviderTenantScope.Create(
            principal.Tenant(),
            principal.FindFirstValue(SreClaims.Product),
            principal.FindFirstValue(SreClaims.Environment));
    }

    /// <summary>The caller's identifier, for the audit trail.</summary>
    public static string Actor(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);

        return principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
    }
}
