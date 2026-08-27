using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Infrastructure;

namespace SreAiAgent.SecurityTests;

/// <summary>
/// Enforces the tool security of ARCHITECTURE.md 15.2.
/// </summary>
/// <remarks>
/// The separation the document asks for is physical, not conditional: write
/// tools are a different registry type, and V1 registers no implementation of
/// it. That means a component asking for write capability cannot be constructed
/// at all, which is a stronger guarantee than a runtime check somebody could
/// invert. These tests hold that property against the real container.
/// </remarks>
public sealed class ToolSecurityTests
{
    private static ServiceProvider BuildContainer()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Registries:ConfigurationRoot"] = RepositoryPaths.Config,
            ["Registries:PromptRoot"] = RepositoryPaths.Prompts,
            ["Registries:AgentRelease"] = "agents-2026.08.25.1",
            ["Registries:PromptRelease"] = "sre-prompts-2026.08.25.1",
            ["Registries:ModelRelease"] = "models-2026.08.25.1",
            ["Registries:ToolRelease"] = "tools-2026.08.25.1",
            ["Registries:PolicyRelease"] = "policies-2026.08.25.1",
            ["Registries:FlagRelease"] = "flags-2026.08.25.1",
            ["Llm:Provider"] = "scripted",
            ["Observability:UseFakeProviders"] = "true",
        });

        builder.Services.AddLogging();
        builder.Services
            .AddSreConfiguration(builder.Configuration)
            .AddSreRegistries()
            .AddSreMetrics()
            .AddSreObservability()
            .AddSreTools();

        return builder.Services.BuildServiceProvider();
    }

    [Fact]
    public void NoWriteToolRegistryIsRegistered()
    {
        using ServiceProvider provider = BuildContainer();

        // Unresolvable rather than empty. A component that takes a dependency on
        // write capability fails at startup, where a deployment notices, instead
        // of receiving an empty registry that someone later populates.
        provider.GetService<IWriteToolRegistry>().ShouldBeNull();
    }

    [Fact]
    public void EveryShippedToolIsReadOnly()
    {
        using ServiceProvider provider = BuildContainer();
        IReadOnlyToolRegistry registry = provider.GetRequiredService<IReadOnlyToolRegistry>();

        registry.All.ShouldNotBeEmpty();

        // The registry loader rejects a non-read-only tool at startup, so this
        // asserts the shipped release rather than the loader. V1 contains no
        // action that changes a production system (ARCHITECTURE.md 15.2).
        foreach (ToolDescriptor descriptor in registry.All)
        {
            descriptor.Risk.ShouldBe(
                Domain.Remediation.RiskLevel.ReadOnly,
                $"Tool '{descriptor.Name}' is not read-only, which V1 does not permit.");
        }
    }

    [Fact]
    public void NoShippedToolAcceptsFreeFormCommands()
    {
        using ServiceProvider provider = BuildContainer();
        IReadOnlyToolRegistry registry = provider.GetRequiredService<IReadOnlyToolRegistry>();

        // Section 15.2 rules out arbitrary shell, kubectl and SQL. A typed
        // parameter named like a command is how that rule erodes: the tool stays
        // "read-only" while accepting anything the model writes.
        string[] forbidden = ["command", "script", "shell", "query_raw", "sql", "exec"];

        foreach (ToolDescriptor descriptor in registry.All)
        {
            string schema = descriptor.Definition.ParameterSchema.GetRawText();

            foreach (string parameter in forbidden)
            {
                schema.ShouldNotContain(
                    $"\"{parameter}\"",
                    Case.Insensitive,
                    $"Tool '{descriptor.Name}' exposes a free-form '{parameter}' parameter.");
            }
        }
    }
}
