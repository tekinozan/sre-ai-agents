using System.Net.Http.Headers;
using System.Text;
using NSubstitute;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.ApiTests;

/// <summary>
/// Proves D40's authorization matrix (F1-3) at the path a real caller
/// actually takes: a signed token, through the endpoint, to the exact
/// <see cref="TenantScope"/> <see cref="IModelProviderControlPlane"/>
/// receives.
/// </summary>
/// <remarks>
/// The acceptance for F1-3 in <c>adp-phased-execution-plan.md</c> says the
/// matrix is proven at the API host. Before this file, that was true only for
/// the illegal environment-without-product case
/// (<see cref="ModelProviderAuthorizationTests.AnEnvironmentClaimWithoutAProductClaimIsRejectedBeforeTheControlPlaneIsCalled"/>).
/// Cross-tenant, same-tenant-different-product, and a claim-less
/// <c>TenantWide</c> caller against a <c>ProductWide</c> row were proven only
/// by constructing <see cref="TenantScope"/> values directly in
/// <c>SreAiAgent.IntegrationTests.ModelControlPlaneTests</c>, which tests
/// <c>ModelProviderRepository</c>'s filtering, not the claim-to-endpoint-to-
/// control-plane path a token actually travels. A minted token with the
/// wrong claims, an endpoint reading the tenant from somewhere other than
/// the principal, or a copy-paste that forwarded the wrong scope variable
/// would all have passed every existing test.
///
/// Each test here mints two real, signed tokens differing in exactly the one
/// claim the scenario is about, drives both through the same route, and
/// asserts the exact <see cref="TenantScope"/> <see cref="SreApiFactory.ControlPlane"/>
/// received for each -- not just that the call succeeded. A mismatch between
/// a token's claims and the scope the endpoint forwards fails these tests
/// even if <c>IModelProviderControlPlane</c> itself would have tolerated it.
///
/// One representative route per verb, exercised sequentially within each
/// scenario -- create, read, update, activate -- mirroring
/// <c>ModelControlPlaneTests.AProductScopedCallerCannotCreateReadUpdateOrActivateAnotherProductsData</c>'s
/// own shape at the repository layer. Exercising all thirteen routes per
/// scenario would duplicate <c>principal.TenantScope()</c> at every call site
/// without adding coverage: every handler reads the scope through the same
/// one extension method, so a bug in the extension surfaces on any route,
/// and a bug in one handler's forwarding is caught by exercising that
/// handler's verb. This is real coverage of the claim-to-scope-forwarding
/// path without testing the same line thirteen times.
/// </remarks>
public sealed class ScopeForwardingTests(SreApiFactory factory) : IClassFixture<SreApiFactory>
{
    [Fact]
    public async Task EachVerbForwardsTheCallersOwnTenantNeverAnotherTenants()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();

        string tenantA = "scope-fwd-tenant-a-" + Guid.NewGuid().ToString("N");
        string tenantB = "scope-fwd-tenant-b-" + Guid.NewGuid().ToString("N");
        string tokenA = TestTokens.For(tenantA, SreRoles.Operator);
        string tokenB = TestTokens.For(tenantB, SreRoles.Operator);

        await AssertScopeForwardingAcrossAllVerbsAsync(
            tokenA, TenantScope.TenantWide(tenantA),
            tokenB, TenantScope.TenantWide(tenantB),
            ct);
    }

    [Fact]
    public async Task EachVerbForwardsTheCallersOwnProductNeverAnotherProductInTheSameTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();

        string tenant = "scope-fwd-product-" + Guid.NewGuid().ToString("N");
        string tokenCheckout = TestTokens.ForScope(tenant, "checkout", null, SreRoles.Operator);
        string tokenPayments = TestTokens.ForScope(tenant, "payments", null, SreRoles.Operator);

        await AssertScopeForwardingAcrossAllVerbsAsync(
            tokenCheckout, TenantScope.ProductWide(tenant, "checkout"),
            tokenPayments, TenantScope.ProductWide(tenant, "payments"),
            ct);
    }

    [Fact]
    public async Task EachVerbForwardsTenantWideForAClaimlessCallerNeverTheProductWideScopeAProductClaimingCallerGets()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();

        string tenant = "scope-fwd-claimless-" + Guid.NewGuid().ToString("N");
        string tokenClaimless = TestTokens.For(tenant, SreRoles.Operator);
        string tokenScoped = TestTokens.ForScope(tenant, "checkout", null, SreRoles.Operator);

        await AssertScopeForwardingAcrossAllVerbsAsync(
            tokenClaimless, TenantScope.TenantWide(tenant),
            tokenScoped, TenantScope.ProductWide(tenant, "checkout"),
            ct);
    }

    /// <summary>
    /// Drives create, read, update and activate with two tokens in turn and
    /// asserts <see cref="SreApiFactory.ControlPlane"/> received exactly the
    /// expected scope for each -- both directions, so neither token's claims
    /// could have leaked into the other's forwarded scope.
    /// </summary>
    private async Task AssertScopeForwardingAcrossAllVerbsAsync(
        string tokenA,
        TenantScope expectedScopeA,
        string tokenB,
        TenantScope expectedScopeB,
        CancellationToken ct)
    {
        using HttpClient client = factory.CreateClient();

        // CREATE: POST /v1/model-providers -> CreateConnectionAsync.
        factory.ControlPlane.CreateConnectionAsync(Arg.Any<TenantScope>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Result<ProviderConnection>.Success(ProviderConnection.Create("factory-return", "conn", DateTimeOffset.UtcNow)));

        await PostAsync(client, "/v1/model-providers", tokenA, """{"display_name":"A"}""", ct);
        await PostAsync(client, "/v1/model-providers", tokenB, """{"display_name":"B"}""", ct);

        await factory.ControlPlane.Received(1).CreateConnectionAsync(expectedScopeA, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await factory.ControlPlane.Received(1).CreateConnectionAsync(expectedScopeB, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // READ: GET /v1/model-providers -> ListConnectionsAsync.
        factory.ControlPlane.ListConnectionsAsync(Arg.Any<TenantScope>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProviderConnection>>([]));

        await GetAsync(client, "/v1/model-providers", tokenA, ct);
        await GetAsync(client, "/v1/model-providers", tokenB, ct);

        await factory.ControlPlane.Received(1).ListConnectionsAsync(expectedScopeA, Arg.Any<CancellationToken>());
        await factory.ControlPlane.Received(1).ListConnectionsAsync(expectedScopeB, Arg.Any<CancellationToken>());

        // UPDATE: POST /v1/model-deployments/{id}/versions -> CreateNextDeploymentVersionAsync.
        ModelDeploymentVersion deploymentReturn = ModelDeploymentVersion.Create(
            "factory-return",
            "logical",
            1,
            ProviderConnectionRevisionId.New(),
            "model",
            ModelCapability.None,
            0.5f,
            1000,
            TimeSpan.FromSeconds(30),
            0m,
            0m,
            "USD",
            new Dictionary<string, string>(StringComparer.Ordinal),
            DateTimeOffset.UtcNow,
            "test");
        factory.ControlPlane.CreateNextDeploymentVersionAsync(
                Arg.Any<TenantScope>(), Arg.Any<ModelDeploymentVersionId>(), Arg.Any<CreateModelDeploymentVersionCommand>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ModelDeploymentVersion>.Success(deploymentReturn)));

        const string DeploymentPayload = """
            {
              "logical_name": "logical",
              "provider_connection_revision_id": "11111111-1111-1111-1111-111111111111",
              "provider_model": "model",
              "temperature": 0.5,
              "max_output_tokens": 1000,
              "timeout_seconds": 30,
              "input_cost_per_million_tokens": 0,
              "output_cost_per_million_tokens": 0,
              "currency": "USD"
            }
            """;
        Guid deploymentVersionId = Guid.NewGuid();
        await PostAsync(client, $"/v1/model-deployments/{deploymentVersionId}/versions", tokenA, DeploymentPayload, ct);
        await PostAsync(client, $"/v1/model-deployments/{deploymentVersionId}/versions", tokenB, DeploymentPayload, ct);

        await factory.ControlPlane.Received(1).CreateNextDeploymentVersionAsync(
            expectedScopeA, Arg.Any<ModelDeploymentVersionId>(), Arg.Any<CreateModelDeploymentVersionCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await factory.ControlPlane.Received(1).CreateNextDeploymentVersionAsync(
            expectedScopeB, Arg.Any<ModelDeploymentVersionId>(), Arg.Any<CreateModelDeploymentVersionCommand>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // ACTIVATE: POST /v1/model-assignment-releases/{id}/activate -> ActivateAsync.
        ModelAssignmentRelease releaseReturn = ModelAssignmentRelease.Create(
            "factory-return",
            "release",
            1,
            ModelDeploymentVersionId.New(),
            ModelDeploymentVersionId.New(),
            new Dictionary<string, ModelDeploymentVersionId>(StringComparer.Ordinal) { ["test-expert"] = ModelDeploymentVersionId.New() },
            DateTimeOffset.UtcNow,
            "test");
        factory.ControlPlane.ActivateAsync(Arg.Any<TenantScope>(), Arg.Any<ModelAssignmentReleaseId>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result<ModelAssignmentRelease>.Success(releaseReturn)));

        Guid releaseId = Guid.NewGuid();
        await PostAsync(client, $"/v1/model-assignment-releases/{releaseId}/activate", tokenA, "{}", ct, hasBody: false);
        await PostAsync(client, $"/v1/model-assignment-releases/{releaseId}/activate", tokenB, "{}", ct, hasBody: false);

        await factory.ControlPlane.Received(1).ActivateAsync(expectedScopeA, Arg.Any<ModelAssignmentReleaseId>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await factory.ControlPlane.Received(1).ActivateAsync(expectedScopeB, Arg.Any<ModelAssignmentReleaseId>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task GetAsync(HttpClient client, string path, string token, CancellationToken ct)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task PostAsync(
        HttpClient client, string path, string token, string jsonBody, CancellationToken ct, bool hasBody = true)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path);
        if (hasBody)
        {
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.IsSuccessStatusCode.ShouldBeTrue(await response.Content.ReadAsStringAsync(ct));
    }
}
