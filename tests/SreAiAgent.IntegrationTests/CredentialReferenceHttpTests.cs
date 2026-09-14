using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SreAiAgent.Api.Security;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Infrastructure.Persistence;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Proves the credential reference lifecycle boundary over the real HTTP
/// surface and the real database (ADP-102): a raw secret never reaches the
/// API, the database, or a response body -- only an <c>env://</c> or
/// <c>file://</c> reference ever does.
/// </summary>
/// <remarks>
/// <c>ModelProviderControlPlane.CreateRevisionAsync</c> already rejects a
/// literal credential before resolving or persisting anything
/// (<c>ModelProviderControlPlane.cs:94-102</c>); nothing exercised that path
/// end to end before this suite. Driving it through the real endpoint and
/// reading the row back afterward proves the protection is live, not merely
/// present in source.
///
/// Each test uses its own generated tenant rather than deleting rows in
/// teardown, matching this suite's convention elsewhere
/// (<c>ModelControlPlaneTests</c>'s <c>"trigger-tenant-" + Guid.NewGuid()</c>):
/// a persisted <c>provider_connection_revisions</c> row is immutable by design
/// (an <c>UPDATE</c> or <c>DELETE</c> against it raises
/// <c>MODEL_CONFIGURATION_INVALID</c> at the database), so a fixed shared
/// tenant that ever successfully creates one could never be cleaned up again.
/// </remarks>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CredentialReferenceHttpTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string EnvironmentVariableName = "SRE_CREDENTIAL_LEAK_TEST";
    private readonly SreApiPostgresFactory _factory = new(postgres);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariableName, null);
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task ALiteralSecretInARevisionRequestIsRejectedAndNeverReachesTheDatabase()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "credential-literal-" + Guid.NewGuid().ToString("N");
        using HttpClient client = _factory.CreateClient();
        string token = TestTokens.For(tenant, SreRoles.Operator);

        Guid connectionId = await CreateConnectionAsync(client, token, "Literal Secret Connection", ct);

        // Not a raw secret's actual shape -- the point under test is the
        // scheme check, not whether this string looks convincing -- but it is
        // deliberately something a caller could plausibly paste by mistake:
        // no env:// or file:// prefix at all.
        const string LiteralSecret = "sk-not-a-reference-0123456789abcdef";
        string path = $"/v1/model-providers/{connectionId.ToString("D", CultureInfo.InvariantCulture)}/revisions";
        const string RevisionPayloadTemplate = """
            {
              "adapter_kind": "scripted",
              "endpoint": "https://test.invalid",
              "credential_ref": "REPLACED"
            }
            """;
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(
                RevisionPayloadTemplate.Replace("REPLACED", LiteralSecret, StringComparison.Ordinal),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("code").GetString().ShouldBe("MODEL_CONFIGURATION_INVALID");

        // The rejection message names the rule, never the value that broke it.
        body.ShouldNotContain(LiteralSecret);

        await using SreDbContext db = postgres.CreateContext();
        int revisionCount = await db.ProviderConnectionRevisions
            .Where(r => r.ProviderConnectionId == new ProviderConnectionId(connectionId))
            .CountAsync(ct);
        revisionCount.ShouldBe(0);
    }

    [Fact]
    public async Task AWellFormedReferencePersistsAndTheApiEchoesOnlyTheReferenceNeverTheResolvedSecret()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "credential-reference-" + Guid.NewGuid().ToString("N");
        const string ResolvedSecretValue = "super-secret-value-should-never-appear-in-a-response";
        Environment.SetEnvironmentVariable(EnvironmentVariableName, ResolvedSecretValue);

        using HttpClient client = _factory.CreateClient();
        string token = TestTokens.For(tenant, SreRoles.Operator);

        Guid connectionId = await CreateConnectionAsync(client, token, "Reference Connection", ct);

        string path = $"/v1/model-providers/{connectionId.ToString("D", CultureInfo.InvariantCulture)}/revisions";
        string revisionPayload = $$"""
            {
              "adapter_kind": "scripted",
              "endpoint": "https://test.invalid",
              "credential_ref": "env://{{EnvironmentVariableName}}"
            }
            """;
        using HttpRequestMessage request = new(HttpMethod.Post, path)
        {
            Content = new StringContent(revisionPayload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        string responseBody = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(responseBody);
        document.RootElement.GetProperty("credential_ref").GetString()
            .ShouldBe($"env://{EnvironmentVariableName}");

        // The response carries the reference. It must never carry the value
        // that reference resolves to, regardless of which field the response
        // shape happens to expose next.
        responseBody.ShouldNotContain(ResolvedSecretValue);

        await using SreDbContext db = postgres.CreateContext();
        string? persistedCredentialRef = await db.ProviderConnectionRevisions
            .Where(r => r.ProviderConnectionId == new ProviderConnectionId(connectionId))
            .Select(r => r.CredentialRef)
            .SingleAsync(ct);
        persistedCredentialRef.ShouldBe($"env://{EnvironmentVariableName}");
    }

    private static async Task<Guid> CreateConnectionAsync(
        HttpClient client,
        string token,
        string displayName,
        CancellationToken ct)
    {
        string payload = $$"""
            {
              "display_name": "{{displayName}}"
            }
            """;
        using HttpRequestMessage request = new(HttpMethod.Post, "/v1/model-providers")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using HttpResponseMessage response = await client.SendAsync(request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        string body = await response.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("connection_id").GetGuid();
    }
}
