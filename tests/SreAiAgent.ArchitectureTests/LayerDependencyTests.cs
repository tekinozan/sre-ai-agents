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

    [Fact]
    public void TheWorkflowAdapterDoesNotDependOnInfrastructure()
    {
        Assembly workflow = Assembly.Load("SreAiAgent.Workflow");

        string[] violations = workflow.GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(static name => name is "SreAiAgent.Infrastructure")
            .ToArray();

        // The workflow adapter drives activities through the application port and
        // nothing else. A reference to Infrastructure would let a workflow reach
        // a DbContext directly, and a workflow that touches the database outside
        // an activity cannot be replayed (ARCHITECTURE.md 3.2).
        violations.ShouldBeEmpty("The workflow adapter talks to the application layer only.");
    }

    [Fact]
    public void HostsDoNotReferenceEachOther()
    {
        // Section 19.1 keeps the three deployments independent: they share the
        // application and infrastructure packages and differ only in composition.
        // A reference between hosts would make one deployable only with another.
        string[] hosts = ["SreAiAgent.Api", "SreAiAgent.AlertWorker", "SreAiAgent.InvestigationWorker"];

        foreach (string host in hosts)
        {
            string[] violations = Assembly.Load(host).GetReferencedAssemblies()
                .Select(static reference => reference.Name ?? string.Empty)
                .Where(name => hosts.Contains(name, StringComparer.Ordinal) && name != host)
                .ToArray();

            violations.ShouldBeEmpty($"{host} must not reference another host.");
        }
    }

    [Fact]
    public void TheAlertWorkerDoesNotCarryAWorkflowEngine()
    {
        // Ingestion stays free of the workflow engine so a webhook storm cannot
        // compete with an LLM fan-out for the same process (ARCHITECTURE.md 19.1).
        string[] violations = Assembly.Load("SreAiAgent.AlertWorker").GetReferencedAssemblies()
            .Select(static reference => reference.Name ?? string.Empty)
            .Where(static name => name is "SreAiAgent.Workflow" or "Temporalio")
            .ToArray();

        violations.ShouldBeEmpty("The alert worker opens incidents; it does not investigate them.");
    }
}
