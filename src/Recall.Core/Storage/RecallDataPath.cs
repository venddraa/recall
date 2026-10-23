using Recall.Core.Folders;

namespace Recall.Core.Storage;

public static class RecallDataPath
{
    public static string GetLogFilePath(string? xdgDataHome, string userProfile) =>
        Path.Combine(Path.GetDirectoryName(GetDatabasePath(xdgDataHome, userProfile))!, "recall.log");

    public static string GetDatabasePath(string? xdgDataHome, string userProfile)
    {
        var dataHome = !string.IsNullOrWhiteSpace(xdgDataHome)
                       && Path.IsPathFullyQualified(xdgDataHome)
            ? FolderPath.Normalize(xdgDataHome)
            : Path.Combine(FolderPath.Normalize(userProfile), ".local", "share");

        return Path.Combine(dataHome, "recall", "recall.db");
    }
}
