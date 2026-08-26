using System.ComponentModel.DataAnnotations;

namespace SreAiAgent.Application.Abstractions.Workflow;

/// <summary>Workflow engine selection and deadlines.</summary>
public sealed class WorkflowOptions
{
    public const string SectionName = "Workflow";

    /// <summary>
    /// <c>temporal</c> or <c>inline</c>. The inline executor has no durability and
    /// refuses to start in Production (docs/DECISIONS.md D08).
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Engine { get; init; } = "temporal";

    public string TemporalHostPort { get; init; } = "localhost:7233";

    public string TemporalNamespace { get; init; } = "default";

    public string TaskQueue { get; init; } = "sre-investigation";

    /// <summary>
    /// Outermost bound of the timeout hierarchy. Every agent, tool and HTTP
    /// timeout must fit inside it.
    /// </summary>
    [Range(30, 3600)]
    public int DeadlineSeconds { get; init; } = 600;

    /// <summary>How many times a failed investigation may be retried.</summary>
    [Range(0, 10)]
    public int MaxAttempts { get; init; } = 3;
}
