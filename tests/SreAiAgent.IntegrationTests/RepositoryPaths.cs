namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Locates the repository's real <c>config/</c> and <c>prompts/</c> directories.
/// </summary>
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
