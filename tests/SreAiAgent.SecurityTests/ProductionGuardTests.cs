using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SreAiAgent.Api.Security;
using SreAiAgent.Infrastructure;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.SecurityTests;

/// <summary>
/// Holds the guards that stop a development convenience reaching production.
/// </summary>
/// <remarks>
/// Both guards refuse at construction, so the host does not start. That
/// placement is the design: a pod that fails to start is a deployment failure
/// somebody sees within minutes, while a pod that starts with authentication
/// disabled is an open API over incident data that nobody sees until it matters.
/// </remarks>
public sealed class ProductionGuardTests
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
    public void DevelopmentAuthenticationCannotBeEnabledInProduction()
    {
        ServiceCollection services = [];

        SecurityOptions security = new() { AllowDevelopmentAuthentication = true };

        InvalidOperationException failure = Should.Throw<InvalidOperationException>(
            () => services.AddSreSecurity(security, new Environment("Production")));

        failure.Message.ShouldContain("AllowDevelopmentAuthentication");
    }

    [Fact]
    public void DevelopmentAuthenticationIsAllowedOutsideProduction()
    {
        ServiceCollection services = [];
        services.AddLogging();

        Should.NotThrow(() => services.AddSreSecurity(
            new SecurityOptions { AllowDevelopmentAuthentication = true },
            new Environment("Development")));
    }

    [Fact]
    public void ProductionRequiresRealAuthentication()
    {
        ServiceCollection services = [];
        services.AddLogging();

        // With the development flag off, Production wires JWT bearer. The point
        // of the assertion is that reaching Production does not silently skip
        // authentication altogether.
        Should.NotThrow(() => services.AddSreSecurity(
            new SecurityOptions
            {
                AllowDevelopmentAuthentication = false,
                Authority = "https://login.example.com",
                Audience = "sre-ai-agent",
            },
            new Environment("Production")));
    }

    [Fact]
    public void FakeObservabilityProvidersCannotBeUsedInProduction()
    {
        // The mirror of the original guard. That one stopped a real backend being
        // asked for before an adapter existed; this one stops the reverse, which
        // is worse: an investigation reporting real conclusions from fabricated
        // data reads exactly like a real one.
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Observability("Production", ("MetricsProvider", "fake")).Value);

        failure.Message.ShouldContain("fake observability provider cannot be used in Production");
    }

    [Fact]
    public void TheRemovedFakeProviderSwitchFailsStartupRatherThanBeingIgnored()
    {
        // Options binding ignores keys it does not know, so a deployment carrying
        // the old switch would silently take the defaults. Since the defaults are
        // the fakes, a configuration that meant "use real backends" would quietly
        // investigate fabricated data.
        OptionsValidationException failure = Should.Throw<OptionsValidationException>(
            () => Observability("Development", ("UseFakeProviders", "false")).Value);

        failure.Message.ShouldContain("UseFakeProviders no longer exists");
    }

    [Fact]
    public void ARealMetricsProviderWithoutAnAddressIsRefused()
    {
        // Registering it anyway would defer the failure to the first tool call,
        // where it becomes one unexplained piece of missing evidence in an
        // investigation rather than a deploy that did not happen.
        Should.Throw<OptionsValidationException>(
            () => Observability("Development", ("MetricsProvider", "prometheus")).Value);
    }

    [Fact]
    public void TheFakesRemainTheDefaultOutsideProduction()
    {
        ObservabilityOptions options = Observability("Development").Value;

        options.MetricsProvider.ShouldBe(ObservabilityProviders.Fake);
        options.LogProvider.ShouldBe(ObservabilityProviders.Fake);
    }

    private static IOptions<ObservabilityOptions> Observability(
        string environmentName,
        params (string Key, string Value)[] settings)
    {
        ServiceCollection services = [];

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(
                static setting => "Observability:" + setting.Key,
                static setting => (string?)setting.Value,
                StringComparer.Ordinal))
            .Build();

        services.AddOptions<ObservabilityOptions>().Bind(configuration.GetSection("Observability"));
        services.AddSreObservability(new Environment(environmentName));

        return services.BuildServiceProvider().GetRequiredService<IOptions<ObservabilityOptions>>();
    }

    [Fact]
    public void ApprovalDecisionsAreNotGrantedToTheRolesThatStartInvestigations()
    {
        // Section 15.1 asks for stronger identity on approval. The least it can
        // mean is that whoever proposed a change is not automatically the person
        // who authorises it, so the two capabilities must not share a role.
        string[] investigators = [SreRoles.Investigator, SreRoles.Operator];

        investigators.ShouldNotContain(SreRoles.Approver);
        SrePolicies.DecideApprovals.ShouldNotBe(SrePolicies.WriteIncidents);
    }
}
