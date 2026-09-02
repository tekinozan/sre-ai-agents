using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Llm;
using SreAiAgent.Infrastructure.Llm.Providers;

namespace SreAiAgent.Infrastructure.Tests;

public sealed class ChatClientResolverTests
{
    private readonly IProviderRevisionReader _reader = Substitute.For<IProviderRevisionReader>();
    private readonly ProviderClientCatalog _catalog;
    private readonly ChatClientResolver _resolver;

    public ChatClientResolverTests()
    {
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Development");

        IOptions<SreAiAgent.Infrastructure.Configuration.LlmOptions> options = Options.Create(
            new SreAiAgent.Infrastructure.Configuration.LlmOptions
            {
                AllowedCustomEndpointHosts = new List<string> { "test" },
            });

        _catalog = new ProviderClientCatalog(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<ICredentialResolver>(),
            new ProviderEndpointPolicy(environment, options),
            new ScriptedChatClient(TimeProvider.System));

        _resolver = new ChatClientResolver(_reader, _catalog);
    }

    private static PinnedModelCandidate Candidate(ProviderAdapterKind kind = ProviderAdapterKind.OpenAi) => new()
    {
        Tenant = "acme",
        AssignmentReleaseId = ModelAssignmentReleaseId.New(),
        AssignmentConfigurationHash = "hash1",
        Slot = "agent",
        DeploymentVersionId = ModelDeploymentVersionId.New(),
        DeploymentLogicalName = "deploy",
        DeploymentVersion = 1,
        DeploymentConfigurationHash = "hash2",
        ProviderConnectionRevisionId = ProviderConnectionRevisionId.New(),
        ProviderConnectionRevision = 1,
        ProviderConfigurationHash = "hash3",
        AdapterKind = kind,
        ProviderModel = "model",
        Capabilities = ModelCapability.None,
        Temperature = 0.5f,
        MaxOutputTokens = 100,
        Timeout = TimeSpan.FromSeconds(30),
        InputCostPerMillionTokens = 0,
        OutputCostPerMillionTokens = 0,
        Currency = "USD",
        ProviderParameters = new Dictionary<string, string>()
    };

    private static ProviderConnectionRevision Revision(PinnedModelCandidate candidate) => ProviderConnectionRevision.Restore(
        candidate.ProviderConnectionRevisionId, ProviderConnectionId.New(), candidate.Tenant, candidate.ProviderConnectionRevision,
        candidate.AdapterKind, "https://test", null, null, null, "cred", ModelCapability.None, candidate.ProviderConfigurationHash,
        DateTimeOffset.UnixEpoch, "actor");

    [Fact]
    public async Task ACandidateWhoseHashDisagreesIsRefused()
    {
        var candidate = Candidate();
        var revision = ProviderConnectionRevision.Restore(
            candidate.ProviderConnectionRevisionId, ProviderConnectionId.New(), candidate.Tenant, candidate.ProviderConnectionRevision,
            candidate.AdapterKind, "https://test", null, null, null, "cred", ModelCapability.None, "different",
            DateTimeOffset.UnixEpoch, "actor");
        _reader.FindRevisionAsync(candidate.Tenant, candidate.ProviderConnectionRevisionId, Arg.Any<CancellationToken>())
            .Returns(revision);

        var result = await _resolver.ResolveAsync(candidate, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ToString().ShouldBe(DomainErrorCodes.ModelConfigurationInvalid.ToString());
    }

    [Fact]
    public async Task ACandidateWhoseTenantDisagreesIsRefused()
    {
        var candidate = Candidate();
        var revision = ProviderConnectionRevision.Restore(
            candidate.ProviderConnectionRevisionId, ProviderConnectionId.New(), "other", candidate.ProviderConnectionRevision,
            candidate.AdapterKind, "https://test", null, null, null, "cred", ModelCapability.None, candidate.ProviderConfigurationHash,
            DateTimeOffset.UnixEpoch, "actor");
        _reader.FindRevisionAsync(candidate.Tenant, candidate.ProviderConnectionRevisionId, Arg.Any<CancellationToken>())
            .Returns(revision);

        var result = await _resolver.ResolveAsync(candidate, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ToString().ShouldBe(DomainErrorCodes.ModelConfigurationInvalid.ToString());
    }

    [Fact]
    public async Task ACandidateWhoseRevisionNumberDisagreesIsRefused()
    {
        var candidate = Candidate();
        var revision = ProviderConnectionRevision.Restore(
            candidate.ProviderConnectionRevisionId, ProviderConnectionId.New(), candidate.Tenant, 2,
            candidate.AdapterKind, "https://test", null, null, null, "cred", ModelCapability.None, candidate.ProviderConfigurationHash,
            DateTimeOffset.UnixEpoch, "actor");
        _reader.FindRevisionAsync(candidate.Tenant, candidate.ProviderConnectionRevisionId, Arg.Any<CancellationToken>())
            .Returns(revision);

        var result = await _resolver.ResolveAsync(candidate, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ToString().ShouldBe(DomainErrorCodes.ModelConfigurationInvalid.ToString());
    }

    [Fact]
    public async Task ACandidateWhoseAdapterKindDisagreesIsRefused()
    {
        var candidate = Candidate(ProviderAdapterKind.OpenAi);
        var revision = ProviderConnectionRevision.Restore(
            candidate.ProviderConnectionRevisionId, ProviderConnectionId.New(), candidate.Tenant, candidate.ProviderConnectionRevision,
            ProviderAdapterKind.Anthropic, "https://test", null, null, null, "cred", ModelCapability.None, candidate.ProviderConfigurationHash,
            DateTimeOffset.UnixEpoch, "actor");
        _reader.FindRevisionAsync(candidate.Tenant, candidate.ProviderConnectionRevisionId, Arg.Any<CancellationToken>())
            .Returns(revision);

        var result = await _resolver.ResolveAsync(candidate, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ToString().ShouldBe(DomainErrorCodes.ModelConfigurationInvalid.ToString());
    }

    [Fact]
    public async Task TwoCandidatesOnTheSameRevisionIdReuseOneClient()
    {
        var candidate1 = Candidate();
        var candidate2 = candidate1 with { Slot = "other" };
        var revision = Revision(candidate1);
        _reader.FindRevisionAsync(candidate1.Tenant, candidate1.ProviderConnectionRevisionId, Arg.Any<CancellationToken>())
            .Returns(revision);

        var result1 = await _resolver.ResolveAsync(candidate1, TestContext.Current.CancellationToken);
        var result2 = await _resolver.ResolveAsync(candidate2, TestContext.Current.CancellationToken);

        result1.IsSuccess.ShouldBeTrue();
        result2.IsSuccess.ShouldBeTrue();
        ReferenceEquals(result1.Value, result2.Value).ShouldBeTrue();
        await _reader.Received(1).FindRevisionAsync(candidate1.Tenant, candidate1.ProviderConnectionRevisionId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUnknownAdapterKindReturnsConfigInvalid()
    {
        var candidate = Candidate((ProviderAdapterKind)999);
        var revision = Revision(Candidate(ProviderAdapterKind.OpenAi));
        _reader.FindRevisionAsync(candidate.Tenant, candidate.ProviderConnectionRevisionId, Arg.Any<CancellationToken>())
            .Returns(revision);

        var result = await _resolver.ResolveAsync(candidate, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ToString().ShouldBe(DomainErrorCodes.ModelConfigurationInvalid.ToString());
        Should.Throw<InvalidOperationException>(() => ProviderClientCatalog.Descriptor((ProviderAdapterKind)999));
    }

    [Theory]
    [InlineData(ProviderAdapterKind.OpenAi)]
    [InlineData(ProviderAdapterKind.OpenAiCompatible)]
    [InlineData(ProviderAdapterKind.AzureOpenAi)]
    [InlineData(ProviderAdapterKind.Anthropic)]
    public async Task ANonScriptedCandidateNeverResolvesAScriptedClient(ProviderAdapterKind kind)
    {
        var candidate = Candidate(kind);
        var revision = Revision(candidate);
        _reader.FindRevisionAsync(candidate.Tenant, candidate.ProviderConnectionRevisionId, Arg.Any<CancellationToken>())
            .Returns(revision);

        var result = await _resolver.ResolveAsync(candidate, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeOfType<ScriptedProviderChatClient>();
    }
}
