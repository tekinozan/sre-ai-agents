using System.Text;
using System.Text.Json.Nodes;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.ModelProviders;

namespace SreAiAgent.IntegrationTests;

/// <summary>A release taken through creation and verification, with the parts a summary names.</summary>
internal sealed record VerifiedRelease(
    ModelAssignmentRelease Release,
    ModelDeploymentVersion Deployment,
    ProviderConnectionRevision Revision);

/// <summary>
/// Writes an eval harness <c>summary.json</c> (v2) for a release, the way the
/// harness would, with a hook to make it wrong in one specific way.
/// </summary>
internal static class TestEvaluationSummary
{
    public const string Dataset = "sre-incidents-redacted@v1";
    public const string Baseline = "baseline-sre-incidents-redacted-v1";

    public static byte[] For(VerifiedRelease release, bool eligible = true, Action<JsonObject>? tamper = null)
    {
        JsonObject summary = new()
        {
            ["schema_version"] = "sre-eval-summary/v2",
            ["run_id"] = "eval-test",
            ["candidate_hash"] = new string('c', 64),
            ["dataset_hash"] = new string('d', 64),
            ["assignment_release"] = Identity(release.Release.Id.Value, release.Release.Version, release.Release.ConfigurationHash),
            ["model_deployment"] = Identity(release.Deployment.Id.Value, release.Deployment.Version, release.Deployment.ConfigurationHash),
            ["provider_connection_revision"] = Identity(release.Revision.Id.Value, release.Revision.Revision, release.Revision.ConfigurationHash),
            ["dataset"] = new JsonObject { ["id"] = "sre-incidents-redacted", ["version"] = "v1", ["hash"] = new string('d', 64) },
            ["baseline"] = new JsonObject { ["id"] = Baseline, ["bootstrap"] = false },
            ["aggregate"] = new JsonObject { ["attempt_count"] = 16, ["hard_failure_count"] = eligible ? 0 : 2 },
            ["gate"] = new JsonObject
            {
                ["promotion_eligible"] = eligible,
                ["failures"] = eligible ? new JsonArray() : new JsonArray("hard_gate", "regression_quality"),
            },
        };

        tamper?.Invoke(summary);
        return Encoding.UTF8.GetBytes(summary.ToJsonString());
    }

    private static JsonObject Identity(Guid id, int version, string hash) => new()
    {
        ["id"] = id.ToString("D"),
        ["version"] = version.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["hash"] = hash,
    };
}

internal static class TestReleases
{
    /// <summary>A verified release with no evaluation yet, made through the control plane's own pipeline.</summary>
    public static async Task<VerifiedRelease> CreateVerifiedAsync(
        IModelProviderControlPlane plane, TenantScope scope, CancellationToken ct, string currency = "USD", bool separateCorrelatorDeployment = false)
    {
        var connection = await plane.CreateConnectionAsync(scope, "connection-" + Guid.NewGuid(), "actor", ct);
        connection.IsSuccess.ShouldBeTrue();

        var revision = await plane.CreateRevisionAsync(
            scope,
            connection.Value.Id,
            new CreateProviderRevisionCommand(
                ProviderAdapterKind.Scripted, "https://test", null, null, null, "env://TEST_KEY", ModelCapability.None),
            "actor",
            ct);
        revision.IsSuccess.ShouldBeTrue(revision.IsFailure ? revision.Error.Message : null);

        var verified = await plane.VerifyRevisionAsync(scope, connection.Value.Id, revision.Value.Revision, "actor", ct);
        verified.IsSuccess.ShouldBeTrue();

        async Task<ModelDeploymentVersion> DeployAsync()
        {
            var deployment = await plane.CreateDeploymentVersionAsync(
                scope,
                new CreateModelDeploymentVersionCommand(
                    "model-" + Guid.NewGuid(),
                    revision.Value.Id,
                    "model-1",
                    ModelCapability.None,
                    0f,
                    1000,
                    TimeSpan.FromSeconds(30),
                    0m,
                    0m,
                    currency,
                    new Dictionary<string, string>()),
                "actor",
                ct);
            deployment.IsSuccess.ShouldBeTrue();
            return deployment.Value;
        }

        ModelDeploymentVersion main = await DeployAsync();
        ModelDeploymentVersion correlator = separateCorrelatorDeployment ? await DeployAsync() : main;

        var release = await plane.CreateAssignmentReleaseAsync(
            scope,
            new CreateModelAssignmentReleaseCommand(
                "release-" + Guid.NewGuid(),
                main.Id,
                correlator.Id,
                new Dictionary<string, ModelDeploymentVersionId> { ["test-expert"] = main.Id }),
            "actor",
            ct);
        release.IsSuccess.ShouldBeTrue();

        return new VerifiedRelease(release.Value, main, revision.Value);
    }
}
