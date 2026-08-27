using System.Diagnostics;
using SreAiAgent.Application.Diagnostics;

namespace SreAiAgent.SecurityTests;

/// <summary>
/// Enforces "prompt, token, credential and raw tool result are never span
/// attributes" (ARCHITECTURE.md 16.1).
/// </summary>
/// <remarks>
/// The guarantee is structural rather than procedural: Activity.SetTag is a
/// banned symbol, so the only way to tag a span is SetSafeTag, and SetSafeTag
/// consults an allowlist. These tests hold both halves of that. If someone adds
/// a key to the allowlist that should not be there, the first test fails; if
/// someone finds a way around the allowlist, the second one does.
///
/// A tracing backend is a different trust domain from the platform. It is
/// usually readable by more people, retained longer and exported further, so a
/// prompt on a span is a copy of the incident's evidence in a place nobody
/// secured for it.
/// </remarks>
public sealed class SpanAttributeLeakTests
{
    /// <summary>
    /// Fragments that must never appear in an attribute key.
    /// </summary>
    /// <remarks>
    /// Matched against the allowlist itself rather than against a captured
    /// trace, so the check does not depend on a code path happening to run.
    /// </remarks>
    private static readonly string[] ForbiddenFragments =
    [
        "prompt.text",
        "prompt.body",
        "prompt.rendered",
        "completion",
        "response.body",
        "token",
        "secret",
        "password",
        "credential",
        "authorization",
        "api_key",
        "apikey",
        "content",
    ];

    [Fact]
    public void TheAllowlistContainsNothingSensitive()
    {
        string[] offending = TraceAttributes.AllowedKeys
            .Where(static key => ForbiddenFragments.Any(fragment =>
                key.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        offending.ShouldBeEmpty(
            "Section 16.1 forbids prompts, tokens, credentials and raw results as span attributes.");
    }

    [Fact]
    public void ThePromptIsIdentifiedByHashRatherThanByText()
    {
        // The hash is how a result is attributed to a prompt release without
        // putting the rendered prompt, and therefore the retrieved evidence,
        // into a tracing backend (ARCHITECTURE.md 8.1).
        TraceAttributes.AllowedKeys.ShouldContain(TraceAttributes.PromptHash);
        TraceAttributes.AllowedKeys.ShouldContain(TraceAttributes.PromptName);
        TraceAttributes.AllowedKeys.ShouldContain(TraceAttributes.PromptVersion);
    }

    [Fact]
    public void ANonAllowlistedKeyIsDroppedRatherThanRecorded()
    {
        using ActivityListener listener = Listen();
        using Activity? activity = SreActivitySources.Llm.StartActivity("leak.probe");

        activity.ShouldNotBeNull();
        activity.SetSafeTag("prompt.text", "You are a database expert. Password=hunter2");

        // Dropped silently rather than throwing: a telemetry mistake must not
        // take down an investigation, and the counter below is how it is noticed.
        activity.GetTagItem("prompt.text").ShouldBeNull();
    }

    [Fact]
    public void ADroppedKeyIsCounted()
    {
        using ActivityListener listener = Listen();
        using Activity? activity = SreActivitySources.Llm.StartActivity("leak.probe");

        long before = TraceAttributes.RejectedTagCount;
        activity.SetSafeTag("model.api_key", "sk-live-not-a-real-key");

        // A silent drop with no counter would be a guarantee nobody can observe.
        // sre_trace_attribute_rejected_total is what turns "we think nothing
        // leaks" into something an operator can watch.
        TraceAttributes.RejectedTagCount.ShouldBeGreaterThan(before);
    }

    [Fact]
    public void AnAllowlistedKeyIsRecorded()
    {
        using ActivityListener listener = Listen();
        using Activity? activity = SreActivitySources.Llm.StartActivity("leak.probe");

        activity.SetSafeTag(TraceAttributes.ToolName, "prometheus.query");

        activity.ShouldNotBeNull();
        activity.GetTagItem(TraceAttributes.ToolName).ShouldBe("prometheus.query");
    }

    private static ActivityListener Listen()
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = static source => source.Name.StartsWith("SreAiAgent.", StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
