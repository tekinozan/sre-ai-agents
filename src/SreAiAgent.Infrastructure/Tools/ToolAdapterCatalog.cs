using System.Collections.Frozen;

namespace SreAiAgent.Infrastructure.Tools;

/// <summary>
/// The tool adapters, indexed by tool name and built once.
/// </summary>
/// <remarks>
/// Separated from the gateway because their lifetimes differ. Adapters are
/// stateless and shared for the life of the process, while the gateway is scoped:
/// it reads the incident's pinned configuration and stages evidence through the
/// unit of work of whatever activity is calling it. Without this split the
/// gateway would either have to be a singleton holding a scoped dependency, which
/// the container rejects, or rebuild this dictionary on every tool call.
/// </remarks>
internal sealed class ToolAdapterCatalog
{
    private readonly FrozenDictionary<string, IToolAdapter> _adapters;

    public ToolAdapterCatalog(IEnumerable<IToolAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _adapters = adapters.ToFrozenDictionary(
            static adapter => adapter.ToolName, StringComparer.Ordinal);
    }

    /// <summary>Finds the adapter for a tool, if the platform ships one.</summary>
    public bool TryGet(string toolName, out IToolAdapter adapter)
        => _adapters.TryGetValue(toolName, out adapter!);
}
