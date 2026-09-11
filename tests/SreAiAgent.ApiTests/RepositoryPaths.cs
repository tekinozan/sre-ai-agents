namespace SreAiAgent.ApiTests;

/// <summary>
/// Locates the repository's real <c>config/</c> and <c>prompts/</c> directories.
/// </summary>
/// <remarks>
/// The registry tests deliberately run against the artifacts that ship, not
/// against fixtures. A fixture would prove the loader works and say nothing about
/// whether the files an operator actually deploys are valid, which is the failure
/// that matters: a typo in a released agent artifact is exactly the class of bug
/// startup validation exists to catch.
/// </remarks>
internal static class RepositoryPaths
{
    public static string Root { get; } = Locate();

    public static string Config => Path.Combine(Root, "config");

    public static string Prompts => Path.Combine(Root, "prompts");

    private static string Locate()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SreAiAgent.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root above '{AppContext.BaseDirectory}'.");
    }
}
