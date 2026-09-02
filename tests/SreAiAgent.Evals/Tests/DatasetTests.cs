using System.Text.Json;

namespace SreAiAgent.Evals.Harness;

public sealed class DatasetTests
{
    [Fact]
    public void Versioned_dataset_has_valid_stable_redacted_cases()
    {
        LoadedDataset dataset = EvalTestData.Dataset();

        dataset.Manifest.SchemaVersion.ShouldBe(DatasetLoader.ManifestSchemaVersion);
        dataset.Cases.Count.ShouldBe(8);
        dataset.Cases.Select(static item => item.CaseId).Distinct(StringComparer.Ordinal).Count().ShouldBe(8);
        dataset.Cases.SelectMany(static item => item.Tags).ShouldContain("app-latency");
        dataset.Cases.SelectMany(static item => item.Tags).ShouldContain("kubernetes");
        dataset.Cases.SelectMany(static item => item.Tags).ShouldContain("prompt-injection");
        dataset.Cases.SelectMany(static item => item.Tags).ShouldContain("policy-refusal");
        dataset.Cases.SelectMany(static item => item.Tags).ShouldContain("truncation");
        dataset.Cases.SelectMany(static item => item.Tags).ShouldContain("unsupported-claims");
    }

    [Fact]
    public void Canonical_fixture_lookup_ignores_object_property_order_but_not_values()
    {
        EvalCase evalCase = EvalTestData.DatabaseCase();
        ToolFixture fixture = evalCase.Fixtures.Single(static item => item.Tool == "database.health");
        using JsonDocument reordered = JsonDocument.Parse("""{"window_minutes":30,"service":"checkout"}""");
        using JsonDocument changed = JsonDocument.Parse("""{"window_minutes":31,"service":"checkout"}""");

        string expected = FixtureToolGateway.Key(fixture);
        FixtureToolGateway.Key(fixture.Tool, fixture.Version, CanonicalJson.Canonicalize(reordered.RootElement)).ShouldBe(expected);
        FixtureToolGateway.Key(fixture.Tool, fixture.Version, CanonicalJson.Canonicalize(changed.RootElement)).ShouldNotBe(expected);
    }
}
