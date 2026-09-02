using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SreAiAgent.Api.Security;
using SreAiAgent.Contracts.ModelProviders;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.SecurityTests;

/// <summary>
/// Holds the authorization contract of the tenant model-provider management
/// surface (`docs/plans/llm-providers.md` phase 3, ARCHITECTURE.md 15.1).
/// </summary>
/// <remarks>
/// The management surface is operator-only. An investigator can start and cancel
/// an investigation, but rotating a provider revision or activating a model
/// assignment release is out of scope for that role: the risk of a bad change
/// scales to every tenant workload rather than to one incident.
///
/// The tests below prove three things at once: reads and writes share a single
/// policy, an <see cref="SreRoles.Investigator"/> is refused every time, and the
/// tenant identity never comes from the request body — the contracts have no
/// tenant field for a caller to lie in.
/// </remarks>
public sealed class ModelProviderAuthorizationTests
{
    private sealed class Environment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SreAiAgent.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IAuthorizationService BuildAuthorizationService()
    {
        ServiceCollection services = [];
        services.AddLogging();
        services.AddSreSecurity(
            new SecurityOptions { AllowDevelopmentAuthentication = true },
            new Environment("Development"));

        return services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal Principal(string tenant, params string[] roles)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, "test-user"),
            new(ClaimTypes.Name, "test-user"),
            new(SreClaims.Tenant, tenant),
        ];
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    [Fact]
    public async Task AnInvestigatorIsRefusedOnEveryManagementRoute()
    {
        IAuthorizationService authorization = BuildAuthorizationService();
        ClaimsPrincipal investigator = Principal(tenant: "acme", SreRoles.Investigator);

        AuthorizationResult result = await authorization.AuthorizeAsync(
            investigator,
            resource: null,
            policyName: SrePolicies.ManageModelProviders);

        // Every management route is guarded by this single policy, so one refusal
        // covers all of them (ListConnections, CreateConnection, CreateRevision,
        // Verify, ListDeployments, Create*Deployment, ListAssignmentReleases,
        // CreateAssignmentRelease, RequestEvaluation, CompleteEvaluation,
        // Activate).
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task AViewerIsRefused()
    {
        IAuthorizationService authorization = BuildAuthorizationService();
        ClaimsPrincipal viewer = Principal(tenant: "acme", SreRoles.Viewer);

        AuthorizationResult result = await authorization.AuthorizeAsync(
            viewer,
            resource: null,
            policyName: SrePolicies.ManageModelProviders);

        // A viewer has read access to incidents, evidence and reports; the
        // management surface answers a different question and enumerates
        // credential references, so it stays out of read scope on purpose.
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task AnApproverIsRefused()
    {
        IAuthorizationService authorization = BuildAuthorizationService();
        ClaimsPrincipal approver = Principal(tenant: "acme", SreRoles.Approver);

        AuthorizationResult result = await authorization.AuthorizeAsync(
            approver,
            resource: null,
            policyName: SrePolicies.ManageModelProviders);

        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task AnOperatorIsAdmitted()
    {
        IAuthorizationService authorization = BuildAuthorizationService();
        ClaimsPrincipal operatorPrincipal = Principal(tenant: "acme", SreRoles.Operator);

        AuthorizationResult result = await authorization.AuthorizeAsync(
            operatorPrincipal,
            resource: null,
            policyName: SrePolicies.ManageModelProviders);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task APlatformAdminIsAdmitted()
    {
        IAuthorizationService authorization = BuildAuthorizationService();
        ClaimsPrincipal admin = Principal(tenant: "acme", SreRoles.PlatformAdmin);

        AuthorizationResult result = await authorization.AuthorizeAsync(
            admin,
            resource: null,
            policyName: SrePolicies.ManageModelProviders);

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task ATokenWithoutATenantClaimIsRefusedEvenAsOperator()
    {
        IAuthorizationService authorization = BuildAuthorizationService();
        ClaimsPrincipal operatorPrincipal = new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "test-user"),
                new Claim(ClaimTypes.Role, SreRoles.Operator),
            ],
            authenticationType: "Test"));

        AuthorizationResult result = await authorization.AuthorizeAsync(
            operatorPrincipal,
            resource: null,
            policyName: SrePolicies.ManageModelProviders);

        // The tenant claim is required by the policy; without it, the runtime
        // has no isolation boundary to filter reads by, and the request must be
        // refused before it reaches the endpoint. This is the pin that stops the
        // policy from being lowered to a role check.
        result.Succeeded.ShouldBeFalse();
    }

    [Fact]
    public void NoManagementRequestContractCarriesATenantField()
    {
        // A request whose body names another tenant still resolves the
        // principal's tenant — because there is nowhere in the wire contract to
        // name a tenant. Any extra property in the body is ignored by
        // System.Text.Json's snake_case source-generated resolver, and the
        // endpoint always calls `principal.Tenant()` on the authenticated
        // caller.
        Type[] managementRequests =
        [
            typeof(CreateModelProviderRequest),
            typeof(CreateProviderRevisionRequest),
            typeof(CreateModelDeploymentRequest),
            typeof(CreateModelAssignmentReleaseRequest),
            typeof(RequestEvaluationRequest),
            typeof(CompleteEvaluationRequest),
        ];

        foreach (Type request in managementRequests)
        {
            PropertyInfo[] tenantLikeProperties = request
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static property => property.Name.Contains("Tenant", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            tenantLikeProperties.ShouldBeEmpty(
                $"{request.Name} declares a tenant-shaped property; the tenant must come from the principal, not the body.");
        }
    }
}
