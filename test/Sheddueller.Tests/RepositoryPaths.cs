namespace Sheddueller.Tests;

internal static class RepositoryPaths
{
    public static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sheddueller.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
              ?? throw new InvalidOperationException("Could not find repository root.");
        }
    }
}
