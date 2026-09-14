using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm;

namespace SreAiAgent.SecurityTests;

/// <summary>
/// Holds the credential reference lifecycle boundary of ADP-102: a raw secret
/// never reaches the API, the database, a snapshot, a log line or the audit
/// trail, only an <c>env://</c> or <c>file://</c> reference does.
/// </summary>
/// <remarks>
/// <see cref="ModelProviderControlPlane.CreateRevisionAsync"/> already refused
/// a literal credential for an operator's own request
/// (<c>ModelProviderControlPlane.cs:94-102</c>). <see cref="ModelAssignmentBootstrapper"/>
/// reaches the same <c>ProviderConnectionRevision.Create</c> constructor
/// through configuration instead, entirely bypassing the control plane and,
/// with it, that check -- so a misconfigured <c>Llm:Bootstrap:CredentialRef</c>
/// could write a literal secret straight into <c>provider_connection_revisions</c>
/// with nothing to stop it. The guard added here closes that, refusing before
/// the hosted service ever opens a database scope, matching
/// <see cref="ProductionGuardTests"/>'s own placement: a pod that fails to
/// start is a deployment failure somebody sees within minutes.
/// </remarks>
public sealed class CredentialReferenceTests
{
    private sealed class Environment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "SreAiAgent.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [Fact]
    public async Task ALiteralCredentialCannotBootstrapAndTheHostFailsBeforeOpeningAScope()
    {
        const string LiteralSecret = "sk-not-a-reference-0123456789abcdef";
        LlmOptions options = new()
        {
            Bootstrap = new LlmOptions.BootstrapOptions
            {
                Tenant = "tenant-a",
                ConnectionDisplayName = "bootstrap",
                AdapterKind = ProviderAdapterKind.Scripted,
                Endpoint = "https://scripted.invalid/",
                CredentialRef = LiteralSecret,
                AssignmentName = "bootstrap",
                ClassifierProfile = "fast",
                CorrelatorProfile = "analytical",
            },
        };

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        ModelAssignmentBootstrapper bootstrapper = new(
            scopeFactory,
            Options.Create(options),
            Options.Create(new RegistryOptions()),
            Substitute.For<IModelProfileRegistry>(),
            Substitute.For<IAgentRegistry>(),
            new Environment("Development"),
            TimeProvider.System,
            NullLogger<ModelAssignmentBootstrapper>.Instance);

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(
            () => bootstrapper.StartAsync(TestContext.Current.CancellationToken));

        // The failure names the rule, never the value that broke it -- an
        // operator's paste-in-the-wrong-field mistake must not become a log
        // line quoting their own secret back at them.
        failure.Message.ShouldNotContain(LiteralSecret);
        failure.Message.ShouldContain("env://");
        failure.Message.ShouldContain("file://");

        // The guard runs before the hosted service opens a database scope at
        // all: nothing about this rejection ever reaches a connection, a
        // transaction, or a row.
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Theory]
    [InlineData("env://SRE_SCRIPTED_CREDENTIAL")]
    [InlineData("file://credentials/openai.key")]
    public void AWellFormedReferenceIsNotRejectedByTheFormatGuard(string reference)
    {
        // The guard's other half: it must not also refuse the two forms the
        // platform actually issues, or every real deployment would fail to
        // boot alongside the malformed one this suite exists to catch.
        ExternalReferenceCredentialResolver.IsWellFormedReference(reference).ShouldBeTrue();
    }

    [Theory]
    [InlineData("sk-live-abcdef0123456789")]
    [InlineData("")]
    [InlineData("https://not-a-credential-scheme.example/key")]
    public void ALiteralOrForeignSchemeIsRejectedByTheFormatGuard(string reference)
    {
        ExternalReferenceCredentialResolver.IsWellFormedReference(reference).ShouldBeFalse();
    }
}
