namespace SreAiAgent.Application.Abstractions.Security;

/// <summary>
/// Removes sensitive values from content before it can be stored as evidence or
/// placed in a prompt (ARCHITECTURE.md 15.3).
/// </summary>
/// <remarks>
/// Applied at the tool gateway, on the way out of an adapter and before anything
/// else sees the content. Redacting later would mean the raw value had already
/// been written to an evidence row, a log or a span, and a value that reached
/// storage has effectively leaked regardless of what happens next.
/// </remarks>
public interface IRedactor
{
    /// <summary>
    /// Redacts <paramref name="content"/>, reporting how many values were
    /// replaced so the count can be recorded on the evidence.
    /// </summary>
    RedactionResult Redact(string content);

    /// <summary>
    /// Redacts without allocating a result object, for hot paths that only need
    /// to know whether the content is clean.
    /// </summary>
    bool ContainsSensitiveData(ReadOnlySpan<char> content);
}

/// <summary>The outcome of a redaction pass.</summary>
/// <param name="Content">
/// The content with sensitive values replaced by category placeholders. The
/// placeholder names the category, for example <c>[REDACTED:BEARER_TOKEN]</c>, so
/// an agent can still reason about the shape of what was there.
/// </param>
/// <param name="ReplacedValueCount">How many values were replaced.</param>
/// <param name="Categories">Which categories were found.</param>
public sealed record RedactionResult(
    string Content,
    int ReplacedValueCount,
    IReadOnlyList<string> Categories)
{
    /// <summary>True when nothing needed replacing.</summary>
    public bool IsClean => ReplacedValueCount == 0;
}
