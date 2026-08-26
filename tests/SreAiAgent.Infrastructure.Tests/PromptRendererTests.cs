using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using SreAiAgent.Application.Diagnostics;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Prompting;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Covers the controlled renderer of ARCHITECTURE.md 8.1.
/// </summary>
public sealed class PromptRendererTests
{
    private readonly ControlledPromptRenderer _renderer =
        new(new SreMetrics(new DummyMeterFactory()));

    private static PromptDefinition Definition(
        string system = "System with {{incident_title}}.",
        string user = "User with {{incident_service}} and {{optional_note}}.",
        int maxCharacters = 4000)
        => new(
            Name: "test.prompt",
            Version: "v1",
            SystemTemplate: system,
            UserTemplate: user,
            RequiredVariables: new[] { "incident_title", "incident_service" }.ToFrozenSet(StringComparer.Ordinal),
            OptionalVariables: new[] { "optional_note" }.ToFrozenSet(StringComparer.Ordinal),
            MaxRenderedCharacters: maxCharacters,
            ContentHash: "hash");

    private static Dictionary<string, string> Variables() => new(StringComparer.Ordinal)
    {
        ["incident_title"] = "Checkout latency above SLO",
        ["incident_service"] = "checkout",
        ["optional_note"] = "second iteration",
    };

    [Fact]
    public void DeclaredVariablesAreSubstituted()
    {
        Result<RenderedPrompt> result = _renderer.Render(Definition(), Variables());

        result.IsSuccess.ShouldBeTrue();
        result.Value.System.ShouldBe("System with Checkout latency above SLO.");
        result.Value.User.ShouldBe("User with checkout and second iteration.");
    }

    [Fact]
    public void AMissingRequiredVariableFailsFast()
    {
        Dictionary<string, string> variables = Variables();
        variables.Remove("incident_title");

        Result<RenderedPrompt> result = _renderer.Render(Definition(), variables);

        // Rendering an empty hole would produce an agent confidently
        // investigating an incident with no title.
        result.IsFailure.ShouldBeTrue();
        result.Error.Code.Value.ShouldBe("PROMPT_RENDER_FAILED");
        result.Error.Message.ShouldContain("incident_title");
    }

    [Fact]
    public void AnEmptyRequiredVariableIsTreatedAsMissing()
    {
        Dictionary<string, string> variables = Variables();
        variables["incident_title"] = "   ";

        _renderer.Render(Definition(), variables).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void AnOmittedOptionalVariableRendersAsNothing()
    {
        Dictionary<string, string> variables = Variables();
        variables.Remove("optional_note");

        Result<RenderedPrompt> result = _renderer.Render(Definition(), variables);

        result.IsSuccess.ShouldBeTrue();
        result.Value.User.ShouldBe("User with checkout and .");
    }

    [Fact]
    public void AnUndeclaredPlaceholderIsRejected()
    {
        Result<RenderedPrompt> result = _renderer.Render(
            Definition(system: "System with {{secret_context}}."), Variables());

        // The allowlist is the point. Without it a template could pick up
        // whatever the caller happened to pass, which is how unrelated context
        // reaches a model.
        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldContain("undeclared variable 'secret_context'");
    }

    [Fact]
    public void AnUnclosedPlaceholderIsRejected()
    {
        Result<RenderedPrompt> result = _renderer.Render(
            Definition(system: "System with {{incident_title."), Variables());

        // Emitting the rest verbatim would ship "{{incident_title." to a model as
        // if it were prose.
        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldContain("unclosed");
    }

    [Fact]
    public void RenderingBeyondTheSizeBoundIsRejectedLocally()
    {
        Dictionary<string, string> variables = Variables();
        variables["incident_title"] = new string('x', 5000);

        Result<RenderedPrompt> result = _renderer.Render(Definition(maxCharacters: 500), variables);

        // Caught here rather than at the provider. Discovering the overflow after
        // a paid round trip returns an error that names neither the prompt nor
        // the evidence that caused it.
        result.IsFailure.ShouldBeTrue();
        result.Error.Message.ShouldContain("over its limit");
    }

    [Fact]
    public void TheHashIdentifiesTheRenderedContentWithoutRevealingIt()
    {
        Result<RenderedPrompt> first = _renderer.Render(Definition(), Variables());

        Dictionary<string, string> different = Variables();
        different["incident_service"] = "payments";
        Result<RenderedPrompt> second = _renderer.Render(Definition(), different);

        first.Value.Hash.Length.ShouldBe(64);
        first.Value.Hash.ShouldNotBe(second.Value.Hash);
        first.Value.Hash.ShouldNotContain("checkout");
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        Result<RenderedPrompt> first = _renderer.Render(Definition(), Variables());
        Result<RenderedPrompt> second = _renderer.Render(Definition(), Variables());

        second.Value.Hash.ShouldBe(first.Value.Hash);
        second.Value.System.ShouldBe(first.Value.System);
    }

    [Fact]
    public void PooledBuffersDoNotLeakBetweenRenders()
    {
        // The renderer returns pooled StringBuilders and a pooled byte buffer. A
        // buffer handed back with the previous prompt still in it would leak one
        // incident's evidence into the next one's hash computation.
        Result<RenderedPrompt> first = _renderer.Render(Definition(), Variables());

        Dictionary<string, string> shorter = Variables();
        shorter["incident_title"] = "a";
        Result<RenderedPrompt> second = _renderer.Render(Definition(), shorter);

        second.Value.System.ShouldBe("System with a.");
        second.Value.System.ShouldNotContain("Checkout latency");
        second.Value.CharacterCount.ShouldBeLessThan(first.Value.CharacterCount);
    }

    /// <summary>Meter factory that keeps instruments out of the global registry.</summary>
    private sealed class DummyMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            Meter meter = new(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (Meter meter in _meters)
            {
                meter.Dispose();
            }

            _meters.Clear();
        }
    }
}
