using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.ModelProviders;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Covers completing an evaluation over HTTP: the body is the harness's
/// summary.json byte for byte, and only the evaluation runner may send it (D50).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EvaluationCompletionHttpTests(PostgresFixture postgres) : IAsyncLifetime
{
    // The release is built through the control plane, which resolves its
    // credential reference; nothing here calls a provider, so a stand-in
    // resolver is enough, as in ModelControlPlaneTests.
    private readonly SreApiPostgresFactory _base = new(postgres);
    private WebApplicationFactory<Program>? _derived;

    private WebApplicationFactory<Program> Factory => _derived ??= _base
        .WithWebHostBuilder(static builder => builder.ConfigureTestServices(static services =>
        {
            services.RemoveAll<ICredentialResolver>();
            services.AddSingleton(Substitute.For<ICredentialResolver>());
        }));
    private readonly string _tenant = "eval-http-" + Guid.CreateVersion7().ToString("N")[^12..];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_derived is not null)
        {
            await _derived.DisposeAsync();
        }

        await _base.DisposeAsync();
    }

    [Fact]
    public async Task TheRunnerCompletesWithTheSummaryBytesAndTheHashIsTheirs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        _ = Factory.CreateClient();

        TenantScope scope = TenantScope.TenantWide(_tenant);
        VerifiedRelease release;
        Guid evaluationId;
        await using (AsyncServiceScope services = Factory.Services.CreateAsyncScope())
        {
            IModelProviderControlPlane plane = services.ServiceProvider.GetRequiredService<IModelProviderControlPlane>();
            release = await TestReleases.CreateVerifiedAsync(plane, scope, ct);
            var requested = await plane.RequestEvaluationAsync(
                scope, release.Release.Id, TestEvaluationSummary.Dataset, TestEvaluationSummary.Baseline, "alice", ct);
            evaluationId = requested.Value.EvaluationId.Value;
        }

        // Indented, as the harness writes it: the hash is over these bytes, not
        // over a re-serialization the platform made of them.
        byte[] summary = await IndentedAsync(TestEvaluationSummary.For(release));
        string path = $"/v1/model-assignment-releases/{release.Release.Id.Value:D}/evaluations/{evaluationId:D}";

        using HttpResponseMessage asOperator = await PostAsync(path, summary, TestTokens.ForActor("bob", _tenant, SreRoles.Operator), ct);
        asOperator.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        using HttpResponseMessage asRunner = await PostAsync(path, summary, TestTokens.ForActor("ci-runner", _tenant, SreRoles.EvaluationRunner), ct);
        string body = await asRunner.Content.ReadAsStringAsync(ct);
        asRunner.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        using JsonDocument response = JsonDocument.Parse(body);
        response.RootElement.GetProperty("status").GetString().ShouldBe("passed");
        response.RootElement.GetProperty("completed_by").GetString().ShouldBe("ci-runner");
        response.RootElement.GetProperty("requested_by").GetString().ShouldBe("alice");
        response.RootElement.GetProperty("artifact_hash").GetString()
            .ShouldBe(Convert.ToHexString(SHA256.HashData(summary)).ToLowerInvariant());
    }

    private async Task<HttpResponseMessage> PostAsync(string path, byte[] summary, string token, CancellationToken ct)
    {
        using HttpClient client = Factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = new ByteArrayContent(summary) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, ct);
    }

    private static async Task<byte[]> IndentedAsync(byte[] compact)
    {
        using JsonDocument document = JsonDocument.Parse(compact);
        using MemoryStream stream = new();
        await using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            document.WriteTo(writer);
        }

        return stream.ToArray();
    }
}
