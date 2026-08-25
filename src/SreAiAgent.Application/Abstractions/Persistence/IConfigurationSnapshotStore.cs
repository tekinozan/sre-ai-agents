using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Persistence;

/// <summary>
/// The immutable release set pinned when an investigation starts
/// (ARCHITECTURE.md 8.2 and 24.5).
/// </summary>
/// <remarks>
/// This is what makes an investigation reproducible and a regression
/// attributable. It also means a flag flipped mid-incident does not change the
/// behaviour of work already in flight, so replay and audit stay deterministic.
/// The documented exception is a kill switch, which is evaluated live precisely
/// because it exists for emergencies.
/// </remarks>
/// <param name="Id">Snapshot identity, referenced by the incident.</param>
/// <param name="AgentRelease">Pinned agent definition release.</param>
/// <param name="PromptRelease">Pinned prompt release. Never <c>latest</c> in production.</param>
/// <param name="ModelRelease">Pinned model profile release.</param>
/// <param name="ToolRelease">Pinned tool release.</param>
/// <param name="PolicyRelease">Pinned policy release.</param>
/// <param name="FlagSnapshot">Agent name to evaluated availability state at pin time.</param>
/// <param name="CreatedAt">When the snapshot was taken, UTC.</param>
public sealed record ConfigurationSnapshot(
    ConfigurationSnapshotId Id,
    string AgentRelease,
    string PromptRelease,
    string ModelRelease,
    string ToolRelease,
    string PolicyRelease,
    IReadOnlyDictionary<string, string> FlagSnapshot,
    DateTimeOffset CreatedAt);

/// <summary>Persists and resolves configuration snapshots.</summary>
public interface IConfigurationSnapshotStore
{
    /// <summary>Stages a new snapshot for the current unit of work.</summary>
    void Add(ConfigurationSnapshot snapshot);

    /// <summary>Loads a snapshot so a resumed workflow sees what it started with.</summary>
    Task<ConfigurationSnapshot?> GetAsync(
        ConfigurationSnapshotId snapshotId,
        CancellationToken cancellationToken);
}
