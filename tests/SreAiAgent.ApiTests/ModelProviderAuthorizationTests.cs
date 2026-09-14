using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using NSubstitute;
using SreAiAgent.Api.Security;
using SreAiAgent.Domain.ModelProviders;

namespace SreAiAgent.ApiTests;

/// <summary>
/// Proves authorization and tenant extraction on the model provider management surface.
/// </summary>
public sealed class ModelProviderAuthorizationTests(SreApiFactory factory) : IClassFixture<SreApiFactory>
{
    [Fact]
    public async Task TheManagementSurfaceUsesTheTenantFromTheToken()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Operator);

        factory.ControlPlane.ListConnectionsAsync(TenantScope.TenantWide("tenant-a"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProviderConnection>>([]));

        using HttpRequestMessage request = new(HttpMethod.Get, "/v1/model-providers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await factory.ControlPlane.Received(1).ListConnectionsAsync(TenantScope.TenantWide("tenant-a"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnInvestigatorMayNotManageModelProviders()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.For("tenant-a", SreRoles.Investigator);

        using HttpRequestMessage request = new(HttpMethod.Get, "/v1/model-providers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        factory.ControlPlane.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ManagementRoutesRejectAnAnonymousCaller()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();

        using HttpRequestMessage request = new(HttpMethod.Get, "/v1/model-providers");

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        factory.ControlPlane.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEnvironmentClaimWithoutAProductClaimIsRejectedBeforeTheControlPlaneIsCalled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();
        string token = TestTokens.ForScope("tenant-a", product: null, environment: "production", SreRoles.Operator);

        using HttpRequestMessage request = new(HttpMethod.Get, "/v1/model-providers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        // TenantScope.Create (D40) rejects this combination the moment
        // principal.TenantScope() is called inside the endpoint handler,
        // before IModelProviderControlPlane -- and therefore
        // ModelProviderRepository -- is ever reached. DomainExceptionHandler
        // turns that thrown DomainException into the same ProblemDetails
        // shape any other rejected request gets, rather than an opaque 500.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await AssertProblemCodeAsync(response, "TENANT_SCOPE_INVALID", ct);
        factory.ControlPlane.ReceivedCalls().ShouldBeEmpty();
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, string expectedCode, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("code", out JsonElement codeProperty).ShouldBeTrue();
        codeProperty.GetString().ShouldBe(expectedCode);
    }
}
