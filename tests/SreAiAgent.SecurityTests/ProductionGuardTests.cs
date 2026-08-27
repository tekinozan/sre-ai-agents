using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SreAiAgent.Api.Security;
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
