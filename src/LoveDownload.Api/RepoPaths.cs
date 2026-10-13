namespace LoveDownload.Api;

/// <summary>Resolves repository-relative paths regardless of launch directory.</summary>
public static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string DataDirectory => Path.Combine(Root, "data");

    public static string DownloadsDirectory => Path.Combine(Root, "downloads");

    public static string DatabasePath => Path.Combine(DataDirectory, "lovedownload.db");

    private static string FindRoot()
    {
        string[] starts = [AppContext.BaseDirectory, Directory.GetCurrentDirectory()];

        foreach (var start in starts)
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LoveDownload.sln")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}
