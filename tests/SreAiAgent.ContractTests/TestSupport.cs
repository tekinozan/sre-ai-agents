namespace SreAiAgent.ContractTests;

/// <summary>Hands out clients pointed at one container.</summary>
/// <remarks>
/// The adapters resolve a named client through <see cref="IHttpClientFactory"/>
/// because <c>new HttpClient()</c> is a banned symbol, and the name selects a
/// resilience pipeline. Here there is one backend per suite and no pipeline
/// worth exercising, so the name is ignored.
/// </remarks>
internal sealed class FixedAddressHttpClientFactory : IHttpClientFactory
{
    private readonly Uri _baseAddress;

    public FixedAddressHttpClientFactory(Uri baseAddress) => _baseAddress = baseAddress;

    public HttpClient CreateClient(string name) => new() { BaseAddress = _baseAddress };
}

/// <summary>
/// Locates the repository's real <c>config/</c> and <c>prompts/</c> directories.
/// </summary>
/// <remarks>
/// The Prometheus contract runs against the shipped tool release, so the
/// expressions it exercises are the ones production sends rather than something
/// written to make a test pass.
/// </remarks>
internal static class RepositoryPaths
{
    private static readonly string Root = Locate();

    public static string Config => Path.Combine(Root, "config");

    public static string Prompts => Path.Combine(Root, "prompts");

    private static string Locate()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SreAiAgent.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException(
                $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
