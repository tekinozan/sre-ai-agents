using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SreAiAgent.Api.Security;
using SreAiAgent.Infrastructure.Persistence;

namespace SreAiAgent.IntegrationTests;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ModelProviderTenantHttpTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly SreApiPostgresFactory _factory = new(postgres);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await using SreDbContext db = postgres.CreateContext();
        await db.ProviderConnections.Where(c => c.Tenant == "tenant-a").ExecuteDeleteAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AConnectionOfAnotherTenantIsNotFoundOverHttp()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using HttpClient client = _factory.CreateClient();

        // 1. Create a connection for tenant-a over HTTP
        string tenantAToken = TestTokens.For("tenant-a", SreRoles.Operator);
        const string CreateConnectionPayload = """
            {
              "display_name": "Tenant A Connection"
            }
            """;

        using HttpRequestMessage createRequest = new(HttpMethod.Post, "/v1/model-providers")
        {
            Content = new StringContent(CreateConnectionPayload, Encoding.UTF8, "application/json"),
        };
        createRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantAToken);

        using HttpResponseMessage createResponse = await client.SendAsync(createRequest, ct);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        string createBody = await createResponse.Content.ReadAsStringAsync(ct);
        using JsonDocument createDoc = JsonDocument.Parse(createBody);
        Guid connectionId = createDoc.RootElement.GetProperty("connection_id").GetGuid();

        // 2. Attempt to create a revision on tenant-a's connection using a tenant-b operator token.
        // POST /v1/model-providers/{connectionId}/revisions delegates to CreateRevisionAsync,
        // which verifies FindConnectionAsync(tenant, connectionId). Because the connection
        // belongs to tenant-a, tenant-b cannot see it and receives 404 PROVIDER_CONNECTION_NOT_FOUND.
        // (A 403 would confirm the existence of another customer's connection, violating isolation).
        string tenantBToken = TestTokens.For("tenant-b", SreRoles.Operator);
        const string RevisionPayload = """
            {
              "adapter_kind": "scripted",
              "endpoint": "https://test.invalid",
              "credential_ref": "env://SRE_TEST_CREDENTIAL"
            }
            """;

        string path = $"/v1/model-providers/{connectionId.ToString("D", CultureInfo.InvariantCulture)}/revisions";
        using HttpRequestMessage revisionRequest = new(HttpMethod.Post, path)
        {
            Content = new StringContent(RevisionPayload, Encoding.UTF8, "application/json"),
        };
        revisionRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tenantBToken);

        using HttpResponseMessage revisionResponse = await client.SendAsync(revisionRequest, ct);

        revisionResponse.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        string body = await revisionResponse.Content.ReadAsStringAsync(ct);
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("code").GetString().ShouldBe("PROVIDER_CONNECTION_NOT_FOUND");
    }
}
