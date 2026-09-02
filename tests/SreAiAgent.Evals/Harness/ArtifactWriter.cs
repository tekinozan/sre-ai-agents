using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace SreAiAgent.Evals.Harness;

internal static partial class ArtifactWriter
{
    public static string Write(string root, ArtifactBundle bundle, IReadOnlyList<string> secretCanaries)
    {
        string directory = Path.Combine(root, bundle.Manifest.RunId);
        Directory.CreateDirectory(directory);
        WriteImmutable(Path.Combine(directory, "manifest.json"), Serialize(bundle.Manifest, EvalJsonContext.Default.RunManifest), secretCanaries);

        StringBuilder attempts = new();
        foreach (SafeAttemptArtifact attempt in bundle.Attempts.OrderBy(static item => item.CaseId, StringComparer.Ordinal).ThenBy(static item => item.Repeat))
        {
            attempts.AppendLine(JsonSerializer.Serialize(attempt, EvalJsonContext.Default.SafeAttemptArtifact));
        }
        WriteImmutable(Path.Combine(directory, "attempts.jsonl"), attempts.ToString(), secretCanaries);
        WriteImmutable(Path.Combine(directory, "summary.json"), Serialize(bundle.Summary, EvalJsonContext.Default.SummaryArtifact), secretCanaries);
        WriteImmutable(Path.Combine(directory, "results.junit.xml"), bundle.JunitXml, secretCanaries);
        return directory;
    }

    public static string BuildJunit(string runId, IReadOnlyList<EvalAttempt> attempts, GateResult gate)
    {
        int failures = attempts.Count(static item => !item.Score.Hard.Passed) + (gate.PromotionEligible ? 0 : 1);
        StringBuilder xml = new();
        using (XmlWriter writer = XmlWriter.Create(xml, new XmlWriterSettings { OmitXmlDeclaration = false, Indent = true }))
        {
            writer.WriteStartElement("testsuite");
            writer.WriteAttributeString("name", "SreAiAgent.Evals." + runId);
            writer.WriteAttributeString("tests", (attempts.Count + 1).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("failures", failures.ToString(CultureInfo.InvariantCulture));
            foreach (EvalAttempt attempt in attempts.OrderBy(static item => item.CaseId, StringComparer.Ordinal).ThenBy(static item => item.Repeat))
            {
                writer.WriteStartElement("testcase");
                writer.WriteAttributeString("classname", "SreAiAgent.Evals");
                writer.WriteAttributeString("name", attempt.CaseId + ".repeat-" + attempt.Repeat.ToString(CultureInfo.InvariantCulture));
                if (!attempt.Score.Hard.Passed)
                {
                    writer.WriteStartElement("failure");
                    writer.WriteAttributeString("message", string.Join(',', attempt.Score.Failures));
                    writer.WriteEndElement();
                }
                writer.WriteEndElement();
            }

            writer.WriteStartElement("testcase");
            writer.WriteAttributeString("classname", "SreAiAgent.Evals");
            writer.WriteAttributeString("name", "promotion-eligibility");
            if (!gate.PromotionEligible)
            {
                writer.WriteStartElement("failure");
                writer.WriteAttributeString("message", string.Join(',', gate.Failures));
                writer.WriteEndElement();
            }
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        return xml.ToString();
    }

    internal static void EnsureSafe(string content, IReadOnlyList<string> secretCanaries)
    {
        if (ForbiddenField().IsMatch(content) || Credential().IsMatch(content))
        {
            throw new InvalidDataException("Eval artifact contains a prohibited raw field or credential-shaped value.");
        }

        foreach (string canary in secretCanaries.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            if (content.Contains(canary, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Eval artifact contains a dataset secret canary.");
            }
        }
    }

    private static string Serialize<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Serialize(value, typeInfo);

    private static void WriteImmutable(string path, string content, IReadOnlyList<string> secretCanaries)
    {
        EnsureSafe(content, secretCanaries);
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        try
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes);
        }
        catch (IOException) when (File.Exists(path))
        {
            byte[] existing = File.ReadAllBytes(path);
            if (!existing.AsSpan().SequenceEqual(bytes))
            {
                throw new IOException($"Immutable eval artifact '{path}' already exists with different content.");
            }
        }
    }

    [GeneratedRegex("(?i)\"(?:raw_prompt|system_prompt|user_prompt|raw_tool_payload|raw_provider_response|credential|api_key|authorization)\"\\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex ForbiddenField();

    [GeneratedRegex("(?i)(sk-[a-z0-9_-]{12,}|bearer\\s+[a-z0-9._-]{12,}|api[_-]?key\\s*[:=]\\s*[^\\s\\\"]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Credential();
}
