namespace SreAiAgent.SecurityTests;

/// <summary>
/// Locates the shipped configuration artifacts from a test binary.
/// </summary>
/// <remarks>
/// These suites deliberately load the real config and prompts rather than
/// fixtures. What is under test is the artifacts this platform actually
/// deploys: a fixture proving that a hand-written read-only tool is read-only
/// says nothing about the tools that ship.
/// </remarks>
internal static class RepositoryPaths
{
    private static readonly string Root = FindRoot();

    public static string Config => Path.Combine(Root, "config");

    public static string Prompts => Path.Combine(Root, "prompts");

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SreAiAgent.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test binary.");
    }
}
