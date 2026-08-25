using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Findings;

/// <summary>Input for <see cref="RootCauseReport.Create"/>.</summary>
public sealed record NewRootCauseReport
{
    public required IncidentId IncidentId { get; init; }

    /// <summary>1-based version. A retry or reopen produces the next number.</summary>
    public required int Version { get; init; }

    public required ConfigurationSnapshotId ConfigurationSnapshotId { get; init; }

    public required InvestigationOutcome Outcome { get; init; }

    public required string Summary { get; init; }

    public string? Category { get; init; }

    public required Confidence Confidence { get; init; }

    public IReadOnlyList<Observation> KeyObservations { get; init; } = [];

    public IReadOnlyList<Hypothesis> RankedHypotheses { get; init; } = [];

    public IReadOnlyList<string> EvidenceGaps { get; init; } = [];

    public IReadOnlyList<EvidenceId> CitedEvidenceIds { get; init; } = [];

    public IReadOnlyList<string> ContributingAgentNames { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }
}
