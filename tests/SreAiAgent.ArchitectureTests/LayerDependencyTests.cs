using System.Reflection;

namespace SreAiAgent.ArchitectureTests;

/// <summary>
/// Enforces the layer rule of ARCHITECTURE.md section 1.2 against the compiled
/// assemblies. Directory.Build.targets already blocks a direct
/// <c>PackageReference</c>; this suite additionally catches a violation that
/// arrives through a transitive project reference.
/// </summary>
public sealed class LayerDependencyTests
{
    /// <summary>
    /// Assembly name prefixes that must never appear in the closure of a pure
    /// layer. Taken verbatim from the forbidden list in ARCHITECTURE.md 1.2.
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "Confluent.Kafka",
        "Temporalio",
        "OpenTelemetry",
        "Serilog",
        "k8s",
        "KubernetesClient",
        "YamlDotNet",
        "Json.Schema",
        "OpenAI",
        "Azure.AI",
        "Anthropic",
    ];

    public static TheoryData<string> PureLayerAssemblies() =>
    [
        "SreAiAgent.Domain",
        "SreAiAgent.Contracts",
        "SreAiAgent.Application",
    ];

    [Theory]
    [MemberData(nameof(PureLayerAssemblies))]
    public void PureLayerDoesNotReferenceInfrastructure(string assemblyName)
    {
        Assembly assembly = Assembly.Load(assemblyName);

        string[] violations = assembly.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(static name => ForbiddenPrefixes.Any(prefix =>
                name.StartsWith(prefix, StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        violations.ShouldBeEmpty(
            $"{assemblyName} must not depend on infrastructure (ARCHITECTURE.md 1.2).");
    }

    [Fact]
    public void DomainDoesNotDependOnAnyOtherPlatformAssembly()
    {
        Assembly domain = Assembly.Load("SreAiAgent.Domain");

        string[] violations = domain.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(static name => name.StartsWith("SreAiAgent.", StringComparison.Ordinal))
            .ToArray();

        violations.ShouldBeEmpty("The domain sits at the bottom of the dependency graph.");
    }

    [Fact]
    public void ApplicationDoesNotDependOnInfrastructureOrHosts()
    {
        Assembly application = Assembly.Load("SreAiAgent.Application");

        string[] violations = application.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(static name =>
                name is "SreAiAgent.Infrastructure"
                    or "SreAiAgent.Workflow"
                    or "SreAiAgent.Api"
                    or "SreAiAgent.AlertWorker"
                    or "SreAiAgent.InvestigationWorker")
            .ToArray();

        violations.ShouldBeEmpty(
            "Application depends only on Domain and Contracts (ARCHITECTURE.md 1.2).");
    }
}
