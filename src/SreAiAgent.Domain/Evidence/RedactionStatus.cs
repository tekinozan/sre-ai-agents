namespace SreAiAgent.Domain.Evidence;

/// <summary>
/// Outcome of the redaction pass that every tool result goes through before it
/// can reach a prompt (ARCHITECTURE.md 15.3). Recorded on the evidence so an
/// auditor can tell "nothing sensitive was present" apart from "we never looked".
/// </summary>
public enum RedactionStatus
{
    /// <summary>Redaction has not run. Such evidence must never enter a prompt.</summary>
    NotApplied = 0,

    /// <summary>Redaction ran and found nothing to remove.</summary>
    Clean = 1,

    /// <summary>Redaction ran and replaced one or more sensitive values.</summary>
    Redacted = 2,

    /// <summary>Redaction failed. The content is quarantined and unusable.</summary>
    Failed = 3,
}
