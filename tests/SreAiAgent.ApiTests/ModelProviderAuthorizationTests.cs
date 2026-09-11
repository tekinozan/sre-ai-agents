using System.Net;
using System.Net.Http.Headers;
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

        factory.ControlPlane.ListConnectionsAsync("tenant-a", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ProviderConnection>>([]));

        using HttpRequestMessage request = new(HttpMethod.Get, "/v1/model-providers");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await factory.ControlPlane.Received(1).ListConnectionsAsync("tenant-a", Arg.Any<CancellationToken>());
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
}
