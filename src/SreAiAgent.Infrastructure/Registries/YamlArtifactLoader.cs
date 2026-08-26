using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SreAiAgent.Infrastructure.Registries;

/// <summary>
/// Reads the YAML artifacts under <c>config/</c> and <c>prompts/</c> and hashes
/// each one.
/// </summary>
/// <remarks>
/// Every artifact is loaded once, at startup, and never re-read. That is not a
/// caching optimisation: an artifact that could change under a running process
/// would make an investigation non-reproducible, and section 8.2 requires the
/// pinned release set to mean something. Changing configuration means a deploy.
///
/// The content hash is computed over the raw bytes and carried on the loaded
/// object, so an audit can prove which exact file produced a given result.
/// </remarks>
internal sealed class YamlArtifactLoader
{
    private readonly IDeserializer _deserializer;

    public YamlArtifactLoader()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            // Artifacts are authored by hand and reviewed. An unrecognised key is
            // almost always a typo in a key that matters, so failing is far more
            // useful than silently ignoring it and running with a default.
            .WithEnforceRequiredMembers()
            .Build();
    }

    /// <summary>Deserializes one artifact and returns it with the hash of its bytes.</summary>
    /// <exception cref="ArtifactLoadException">The file is missing or malformed.</exception>
    public LoadedArtifact<T> Load<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            throw new ArtifactLoadException(path, "File not found.");
        }

        byte[] bytes;
        try
        {
            // Synchronous on purpose. Artifacts are read once while the host is
            // starting, before any request is served, and registries are built in
            // a DI constructor which cannot be async. Deferring this to a hosted
            // service would leave the registries mutable and half-populated for a
            // window, which is a worse trade than a few blocking reads at boot.
#pragma warning disable MA0045
            bytes = File.ReadAllBytes(path);
#pragma warning restore MA0045
        }
        catch (IOException exception)
        {
            throw new ArtifactLoadException(path, "File could not be read.", exception);
        }

        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        string text = Encoding.UTF8.GetString(bytes);

        try
        {
            T value = _deserializer.Deserialize<T>(text)
                ?? throw new ArtifactLoadException(path, "File is empty.");
            return new LoadedArtifact<T>(value, path, hash);
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            // The YAML exception carries a line and column, which is exactly what
            // whoever edited the artifact needs; the type name alone is not.
            throw new ArtifactLoadException(
                path,
                $"Malformed YAML at line {exception.Start.Line.ToString(CultureInfo.InvariantCulture)}, " +
                $"column {exception.Start.Column.ToString(CultureInfo.InvariantCulture)}: {exception.Message}",
                exception);
        }
    }

    /// <summary>Enumerates artifact files in a directory, ignoring subdirectories.</summary>
    public static IReadOnlyList<string> EnumerateArtifacts(string directory)
        => Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.yaml", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal)]
            : [];
}

/// <summary>An artifact together with the hash of the bytes it was parsed from.</summary>
/// <typeparam name="T">The artifact shape.</typeparam>
/// <param name="Value">The parsed artifact.</param>
/// <param name="Path">Where it came from, for error messages.</param>
/// <param name="ContentHash">SHA-256 of the raw bytes.</param>
internal sealed record LoadedArtifact<T>(T Value, string Path, string ContentHash)
    where T : class;

/// <summary>
/// Raised when an artifact cannot be loaded. Always fatal at startup: a platform
/// that cannot read its own configuration must not serve traffic.
/// </summary>
internal sealed class ArtifactLoadException : Exception
{
    public ArtifactLoadException(string path, string reason)
        : base($"Failed to load artifact '{path}'. {reason}")
        => Path = path;

    public ArtifactLoadException(string path, string reason, Exception innerException)
        : base($"Failed to load artifact '{path}'. {reason}", innerException)
        => Path = path;

    public string Path { get; }
}
