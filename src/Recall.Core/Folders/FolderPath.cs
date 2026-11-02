namespace Recall.Core.Folders;

public static class FolderPath
{
    public static bool HasSymbolicLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return true;
        for (var directory = new FileInfo(path).Directory; directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }

    public static bool ContainsPath(string folder, string path) => path == folder
        || path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

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
