using System.Diagnostics;
using SreAiAgent.Application.Diagnostics;

namespace SreAiAgent.Application.Tests.Diagnostics;

/// <summary>
/// Guards the rule from ARCHITECTURE.md 16.1 that a span carries metadata only.
/// The banned-API list already makes a direct <c>Activity.SetTag</c> a compile
/// error; these tests cover what happens at the one sanctioned call site.
/// </summary>
public sealed class TraceAttributeTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ActivitySource _source = new("SreAiAgent.Tests.Trace");

    public TraceAttributeTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "SreAiAgent.Tests.Trace",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    [Fact]
    public void AnAllowedAttributeIsWritten()
    {
        using Activity? activity = _source.StartActivity("test");

        activity.SetSafeTag(TraceAttributes.AgentName, "database-expert");

        activity.ShouldNotBeNull();
        activity.GetTagItem(TraceAttributes.AgentName).ShouldBe("database-expert");
    }

    [Theory]
    [InlineData("prompt.text")]
    [InlineData("llm.response")]
    [InlineData("tool.result")]
    [InlineData("authorization")]
    [InlineData("api_key")]
    [InlineData("gen_ai.prompt")]
    public void AttributesThatCouldCarrySecretsAreDropped(string forbiddenKey)
    {
        using Activity? activity = _source.StartActivity("test");
        long before = TraceAttributes.RejectedTagCount;

        activity.SetSafeTag(forbiddenKey, "sk-live-should-never-appear");

        activity.ShouldNotBeNull();
        activity.GetTagItem(forbiddenKey).ShouldBeNull();
        TraceAttributes.RejectedTagCount.ShouldBeGreaterThan(before);
    }

    [Fact]
    public void TheAllowlistCoversEveryAttributeTheArchitectureLists()
    {
        // ARCHITECTURE.md 16.1, verbatim.
        string[] required =
        [
            TraceAttributes.IncidentId,
            TraceAttributes.AgentName,
            TraceAttributes.AgentVersion,
            TraceAttributes.PromptName,
            TraceAttributes.PromptVersion,
            TraceAttributes.ModelProvider,
            TraceAttributes.ModelName,
            TraceAttributes.ToolName,
            TraceAttributes.ToolRisk,
            TraceAttributes.ResultStatus,
        ];

        required.ShouldAllBe(static key => TraceAttributes.IsAllowed(key));
    }

    [Fact]
    public void NoAllowedAttributeNameSuggestsContentRatherThanMetadata()
    {
        string[] contentBearingWords = ["prompt.text", "response", "content", "payload", "token.value", "secret"];

        foreach (string key in TraceAttributes.AllowedKeys)
        {
            contentBearingWords.ShouldAllBe(word => !key.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void SettingATagOnAnUnsampledActivityIsSafe()
    {
        // With no listener attached StartActivity returns null. The helper must
        // tolerate that, because instrumentation runs whether or not anyone is
        // collecting.
        using ActivitySource unlistened = new("SreAiAgent.Tests.NotListened");
        using Activity? activity = unlistened.StartActivity("test");

        activity.ShouldBeNull();
        Should.NotThrow(() => activity.SetSafeTag(TraceAttributes.AgentName, "x"));
    }
}
