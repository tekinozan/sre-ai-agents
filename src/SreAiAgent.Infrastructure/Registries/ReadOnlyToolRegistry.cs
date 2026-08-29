using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Loads read-only tool definitions (ARCHITECTURE.md 15.2).
/// </summary>
/// <remarks>
/// This registry refuses to load anything above
/// <see cref="RiskLevel.ReadOnly"/>. Section 15.2 requires read-only and write
/// registries to be physically separated, and a read-only registry that would
/// happily serve a write tool if one appeared in its directory is not separated
/// at all. Misfiling a write tool here is a startup failure, which is the point:
/// it surfaces during a deploy rather than during an incident.
/// </remarks>
internal sealed class ReadOnlyToolRegistry : IReadOnlyToolRegistry, IReadOnlyToolRegistryProbe
{
    private readonly FrozenDictionary<string, ToolDescriptor> _tools;

    public ReadOnlyToolRegistry(IOptions<RegistryOptions> options, YamlArtifactLoader loader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loader);

        string root = Path.Combine(options.Value.ConfigurationRoot, "tools");
        string releaseName = options.Value.ToolRelease;

        Dictionary<string, ToolDescriptor> loaded = new(StringComparer.Ordinal);
        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(root))
        {
            ToolDescriptor descriptor = ReadDescriptor(loader.Load<ToolDocument>(path));
            if (!loaded.TryAdd(Key(descriptor.Name, descriptor.Version), descriptor))
            {
                throw new ArtifactLoadException(path, $"Duplicate tool '{descriptor.Name}:{descriptor.Version}'.");
            }
        }

        // Only tools pinned by the configured release are exposed. A definition
        // sitting on disk but absent from the release is not available, which is
        // how a tool can be prepared and reviewed ahead of being switched on.
        Dictionary<string, ToolDescriptor> pinned = new(StringComparer.Ordinal);
        foreach (string path in YamlArtifactLoader.EnumerateArtifacts(Path.Combine(root, "releases")))
        {
            LoadedArtifact<ToolReleaseDocument> artifact = loader.Load<ToolReleaseDocument>(path);
            ToolReleaseDocument document = artifact.Value;

            if (!string.Equals(document.Name, releaseName, StringComparison.Ordinal))
            {
                continue;
            }

            if (document.Tools is null or { Count: 0 })
            {
                throw new ArtifactLoadException(path, "A tool release requires a non-empty 'tools' map.");
            }

            foreach ((string name, string version) in document.Tools)
            {
                if (!loaded.TryGetValue(Key(name, version), out ToolDescriptor? descriptor))
                {
                    throw new ArtifactLoadException(
                        path, $"Release pins tool '{name}:{version}' which was not found on disk.");
                }

                pinned[Key(name, version)] = descriptor;
            }
        }

        _tools = pinned.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ToolDescriptor> All => _tools.Values;

    /// <inheritdoc />
    public ValueTask<ToolDescriptor?> FindAsync(
        string toolName,
        string toolVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _tools.TryGetValue(Key(toolName, toolVersion), out ToolDescriptor? descriptor);
        return ValueTask.FromResult(descriptor);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Matches on name alone, ignoring version. Routing asks this whether an
    /// agent's declared tools exist in the pinned release at all, so that an
    /// agent whose tools are missing is excluded with a reason instead of being
    /// selected and failing on its first call. Which version it will get is the
    /// gateway's decision at execution time, not routing's.
    /// </remarks>
    public bool Contains(string toolName)
    {
        ArgumentNullException.ThrowIfNull(toolName);

        foreach (ToolDescriptor descriptor in _tools.Values)
        {
            if (string.Equals(descriptor.Definition.Name, toolName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string Key(string name, string version) => $"{name}:{version}";

    private static ToolDescriptor ReadDescriptor(LoadedArtifact<ToolDocument> artifact)
    {
        ToolDocument document = artifact.Value;
        string path = artifact.Path;

        if (string.IsNullOrWhiteSpace(document.Name)
            || string.IsNullOrWhiteSpace(document.Version)
            || string.IsNullOrWhiteSpace(document.Provider))
        {
            throw new ArtifactLoadException(path, "A tool requires 'name', 'version' and 'provider'.");
        }

        RiskLevel risk = ParseRisk(document.Risk, path);
        if (risk is not RiskLevel.ReadOnly)
        {
            throw new ArtifactLoadException(
                path,
                $"Tool '{document.Name}' declares risk '{document.Risk}'. The read-only registry loads only "
                + "read_only tools; a write tool belongs in the write registry, which V1 does not register.");
        }

        if (document.Parameters is null)
        {
            throw new ArtifactLoadException(
                path,
                "A tool requires a 'parameters' JSON Schema. Without one the arguments cannot be validated, "
                + "and unvalidated model output would reach an adapter.");
        }

        if (document.TimeoutSeconds <= 0 || document.MaxResultBytes <= 0)
        {
            throw new ArtifactLoadException(path, "'timeout_seconds' and 'max_result_bytes' must be positive.");
        }

        JsonElement schema = YamlToJson.Convert(document.Parameters);

        return new ToolDescriptor(
            Name: document.Name,
            Version: document.Version,
            Description: document.Description?.Trim() ?? string.Empty,
            Risk: risk,
            Provider: document.Provider,
            RequiredScopes: (document.RequiredScopes ?? []).ToFrozenSet(StringComparer.Ordinal),
            Timeout: TimeSpan.FromSeconds(document.TimeoutSeconds),
            MaxResultBytes: document.MaxResultBytes,
            Definition: new ToolDefinition(
                document.Name,
                document.Version,
                document.Description?.Trim() ?? string.Empty,
                schema),
            ProviderExpressions: (document.ProviderExpressions
                ?? new Dictionary<string, string>(StringComparer.Ordinal))
                .ToFrozenDictionary(StringComparer.Ordinal));
    }

    private static RiskLevel ParseRisk(string? value, string path) => value switch
    {
        "read_only" => RiskLevel.ReadOnly,
        "low" => RiskLevel.Low,
        "medium" => RiskLevel.Medium,
        "high" => RiskLevel.High,
        "destructive" => RiskLevel.Destructive,
        _ => throw new ArtifactLoadException(
            path,
            $"Unknown risk '{value}'. Expected read_only, low, medium, high or destructive. "
            + "An unrecognised risk is never treated as read_only."),
    };
}
