using System.Text.Json;
using System.Text.RegularExpressions;

namespace SreAiAgent.Evals.Harness;

internal static partial class DatasetLoader
{
    public const string SchemaVersion = "sre-eval-case/v1";
    public const string ManifestSchemaVersion = "sre-eval-dataset/v1";

    public static LoadedDataset Load(string directory)
    {
        string manifestPath = Path.Combine(directory, "dataset.json");
        EvalDatasetManifest manifest = Deserialize(File.ReadAllText(manifestPath), EvalJsonContext.Default.EvalDatasetManifest, manifestPath);
        if (!string.Equals(manifest.SchemaVersion, ManifestSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported dataset schema '{manifest.SchemaVersion}'.");
        }

        if (manifest.Cases.Count == 0 || manifest.Cases.Count != manifest.Cases.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Dataset case paths must be non-empty and unique.");
        }

        List<EvalCase> cases = new(manifest.Cases.Count);
        List<string> paths = [manifestPath];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (string relativePath in manifest.Cases)
        {
            if (Path.IsPathRooted(relativePath) || relativePath.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Dataset case path '{relativePath}' is not a safe relative path.");
            }

            string path = Path.Combine(directory, relativePath);
            EvalCase evalCase = Deserialize(File.ReadAllText(path), EvalJsonContext.Default.EvalCase, path);
            Validate(evalCase);
            if (!ids.Add(evalCase.CaseId))
            {
                throw new InvalidDataException($"Duplicate stable case id '{evalCase.CaseId}'.");
            }

            cases.Add(evalCase);
            paths.Add(path);
        }

        return new LoadedDataset(manifest, cases, CanonicalJson.HashFiles(paths), directory);
    }

    private static void Validate(EvalCase evalCase)
    {
        if (!string.Equals(evalCase.SchemaVersion, SchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Case '{evalCase.CaseId}' has unsupported schema '{evalCase.SchemaVersion}'.");
        }

        if (!StableCaseId().IsMatch(evalCase.CaseId))
        {
            throw new InvalidDataException($"Case id '{evalCase.CaseId}' is not stable kebab-case with a version suffix.");
        }

        if (evalCase.FixedUtc.Offset != TimeSpan.Zero || evalCase.Fixtures.Count == 0)
        {
            throw new InvalidDataException($"Case '{evalCase.CaseId}' needs a UTC fixed clock and at least one fixture.");
        }

        if (evalCase.ExpectedRoutingFamilies.Count == 0 || evalCase.ExpectedRootCauseFamilies.Count == 0)
        {
            throw new InvalidDataException($"Case '{evalCase.CaseId}' needs routing and root-cause expectations.");
        }

        if (evalCase.Fixtures.Select(static item => item.EvidenceId).Distinct().Count() != evalCase.Fixtures.Count)
        {
            throw new InvalidDataException($"Case '{evalCase.CaseId}' has duplicate evidence ids.");
        }

        HashSet<Guid> available = evalCase.Fixtures.Select(static item => item.EvidenceId).ToHashSet();
        if (evalCase.RequiredEvidenceIds.Any(id => !available.Contains(id)))
        {
            throw new InvalidDataException($"Case '{evalCase.CaseId}' requires evidence absent from its fixtures.");
        }

        foreach (ToolFixture fixture in evalCase.Fixtures)
        {
            string canonical = CanonicalJson.Canonicalize(fixture.Arguments);
            if (string.IsNullOrWhiteSpace(fixture.Tool) || fixture.Version != "v1" || canonical.Length == 0)
            {
                throw new InvalidDataException($"Case '{evalCase.CaseId}' contains an invalid tool fixture.");
            }

            string fixtureText = fixture.RedactedResult.GetRawText();
            if (CredentialPattern().IsMatch(fixtureText))
            {
                throw new InvalidDataException($"Case '{evalCase.CaseId}' contains credential-shaped fixture data.");
            }
        }
    }

    private static T Deserialize<T>(string json, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string path)
        where T : class
        => JsonSerializer.Deserialize(json, typeInfo)
            ?? throw new InvalidDataException($"'{path}' deserialized to null.");

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*-v[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StableCaseId();

    [GeneratedRegex("(?i)(sk-[a-z0-9_-]{12,}|bearer\\s+[a-z0-9._-]{12,}|api[_-]?key\\s*[:=]\\s*[^\\s\\\"]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();
}
