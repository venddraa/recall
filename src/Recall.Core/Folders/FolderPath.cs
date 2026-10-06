namespace Recall.Core.Folders;

public static class FolderPath
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("Folder paths must be absolute.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string GetConfigurationFilePath(string? xdgConfigHome, string userProfile)
    {
        var configHome = !string.IsNullOrWhiteSpace(xdgConfigHome)
                         && Path.IsPathFullyQualified(xdgConfigHome)
            ? Normalize(xdgConfigHome)
            : Path.Combine(Normalize(userProfile), ".config");

        return Path.Combine(configHome, "recall", "folders.json");
    }
}
