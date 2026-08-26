using System.Buffers;
using System.Text.RegularExpressions;
using SreAiAgent.Application.Abstractions.Security;

namespace SreAiAgent.Infrastructure.Security;

/// <summary>
/// Removes sensitive values from tool output before it becomes evidence or
/// reaches a prompt (ARCHITECTURE.md 15.3).
/// </summary>
/// <remarks>
/// Runs on every tool result, which is the hottest data path in an
/// investigation, so the cheap check comes first: a
/// <see cref="SearchValues{T}"/> scan for the handful of characters that any
/// secret pattern must contain. Real log output is overwhelmingly ordinary text,
/// and for that text the expensive regex pass never runs at all.
///
/// Replacements name their category, for example
/// <c>[REDACTED:BEARER_TOKEN]</c>, rather than blanking the value. An agent can
/// then still reason about the shape of what was there, which occasionally
/// matters: a bearer token appearing inside a URL that is being logged is itself
/// a finding.
/// </remarks>
internal sealed partial class RegexRedactor : IRedactor
{
    /// <summary>
    /// Characters that appear in every pattern below. Absent all of them, the
    /// content cannot contain a secret in any recognised form, and the regex pass
    /// is skipped entirely.
    /// </summary>
    private static readonly SearchValues<char> Indicators =
        SearchValues.Create("=:@.-_ ");

    private static readonly (Regex Pattern, string Category)[] Detectors =
    [
        (PrivateKey(), RedactionCategories.PrivateKey),
        (ConnectionString(), RedactionCategories.ConnectionString),
        (JsonWebToken(), RedactionCategories.JsonWebToken),
        (BearerToken(), RedactionCategories.BearerToken),
        (ApiKey(), RedactionCategories.ApiKey),
        (Password(), RedactionCategories.Password),
        (Cookie(), RedactionCategories.Cookie),
        (PaymentCard(), RedactionCategories.PaymentCard),
        (NationalIdentifier(), RedactionCategories.PersonalIdentifier),
        (EmailAddress(), RedactionCategories.EmailAddress),
    ];

    /// <inheritdoc />
    public RedactionResult Redact(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0 || !ContainsSensitiveData(content))
        {
            return new RedactionResult(content, ReplacedValueCount: 0, Categories: []);
        }

        string current = content;
        int replaced = 0;
        List<string>? categories = null;

        foreach ((Regex pattern, string category) in Detectors)
        {
            int matches = pattern.Count(current);
            if (matches == 0)
            {
                continue;
            }

            current = pattern.Replace(current, $"[REDACTED:{category}]");
            replaced += matches;
            (categories ??= []).Add(category);
        }

        return new RedactionResult(current, replaced, categories ?? (IReadOnlyList<string>)[]);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only the prefilter. It answers "could this possibly contain a secret",
    /// which is the question worth asking cheaply; a false positive here costs one
    /// regex pass, whereas skipping the scan entirely would cost a leak.
    /// </remarks>
    public bool ContainsSensitiveData(ReadOnlySpan<char> content)
        => content.ContainsAny(Indicators);

    // Ordered most specific first in Detectors above: a connection string
    // containing "password=" must be replaced whole rather than leaving the
    // surrounding host and database name behind after the password alone is
    // removed.

    [GeneratedRegex(
        @"-----BEGIN[A-Z ]*PRIVATE KEY-----[\s\S]*?-----END[A-Z ]*PRIVATE KEY-----",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex PrivateKey();

    [GeneratedRegex(
        @"(?i)\b(?:Server|Host|Data Source)\s*=\s*[^;\s]+;(?:[^;\s]*;)*?\s*(?:Password|Pwd)\s*=\s*[^;\s]+;?",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex ConnectionString();

    [GeneratedRegex(
        @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex JsonWebToken();

    [GeneratedRegex(
        @"(?i)\b(?:bearer|basic)\s+[A-Za-z0-9._~+/=-]{12,}",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex BearerToken();

    [GeneratedRegex(
        @"(?i)\b(?:api[_-]?key|access[_-]?token|secret[_-]?key|client[_-]?secret)\s*[=:]\s*[""']?[A-Za-z0-9._\-]{8,}[""']?",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex ApiKey();

    [GeneratedRegex(
        @"(?i)\b(?:password|passwd|pwd)\s*[=:]\s*[""']?[^\s""',;)]{4,}[""']?",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex Password();

    [GeneratedRegex(
        @"(?i)\b(?:Cookie|Set-Cookie)\s*:\s*[^\r\n]{4,}",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex Cookie();

    // Sixteen digits with optional separators. Deliberately broad: a false
    // positive redacts a long numeric identifier that an agent probably did not
    // need, while a false negative puts a card number in an evidence row.
    [GeneratedRegex(
        @"\b(?:\d[ -]?){13,19}\b",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex PaymentCard();

    [GeneratedRegex(
        @"(?i)\b(?:tckn|ssn|national[_-]?id)\s*[=:]\s*\d{9,11}\b",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex NationalIdentifier();

    [GeneratedRegex(
        @"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b",
        RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 200)]
    private static partial Regex EmailAddress();
}
