namespace SreAiAgent.Domain.Remediation;

/// <summary>
/// How dangerous an action is (ARCHITECTURE.md 5.3). Every tool execution
/// context carries a risk ceiling, and the tool gateway refuses anything above
/// it regardless of what the model asked for.
/// </summary>
/// <remarks>
/// Ordered so a ceiling check is a comparison. V1 ships with the ceiling pinned
/// to <see cref="ReadOnly"/> everywhere.
/// </remarks>
public enum RiskLevel
{
    /// <summary>Observes only. Cannot change any external state.</summary>
    ReadOnly = 0,

    /// <summary>Trivially reversible write, for example annotating a dashboard.</summary>
    Low = 1,

    /// <summary>Reversible but user-visible, for example scaling a deployment.</summary>
    Medium = 2,

    /// <summary>Disruptive, for example restarting a stateful workload.</summary>
    High = 3,

    /// <summary>Irreversible or data-destroying. Never automated.</summary>
    Destructive = 4,
}
