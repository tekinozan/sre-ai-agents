using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Captures the release set in force right now (ARCHITECTURE.md 8.2, 24.5).
/// </summary>
/// <remarks>
/// The releases come from configuration rather than from the registries
/// themselves, because the registries were loaded from those same values at
/// startup and reading them back would only restate the answer. What the
/// snapshot adds is a record: the identifiers are written to a row so an
/// investigation months old can still be explained in terms of the exact
/// artifacts it ran on, even after every one of those releases has been
/// superseded.
///
/// The flag states are copied in for the same reason. Flags change far more
/// often than releases do, and "the database expert was disabled at the time"
/// is otherwise unrecoverable the moment somebody switches it back on.
/// </remarks>
internal sealed class ConfigurationPinner(
    IOptions<RegistryOptions> options,
    IAgentFlagEvaluator flags) : IConfigurationPinner
{
    private readonly RegistryOptions _options = options.Value;

    /// <inheritdoc />
    public ConfigurationSnapshot PinCurrent(DateTimeOffset now)
        => new(
            ConfigurationSnapshotId.New(),
            _options.AgentRelease,
            _options.PromptRelease,
            _options.ModelRelease,
            _options.ToolRelease,
            _options.PolicyRelease,
            flags.DeclaredStates(),
            now);
}
