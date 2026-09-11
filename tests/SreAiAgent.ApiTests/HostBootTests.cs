using System.Net;

namespace SreAiAgent.ApiTests;

/// <summary>
/// Verifies the HTTP host starts cleanly and responds to liveness probes without
/// dependencies on external databases, message brokers, or workflow engines.
/// </summary>
public sealed class HostBootTests(SreApiFactory factory) : IClassFixture<SreApiFactory>
{
    [Fact]
    public async Task TheHostAnswersLiveWithoutAnyBackend()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        factory.Reset();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/live", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
