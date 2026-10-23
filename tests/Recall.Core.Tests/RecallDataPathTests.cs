using Recall.Core.Storage;

namespace Recall.Core.Tests;

public sealed class RecallDataPathTests
{
    [Fact]
    public void AbsoluteXdgDataDirectoryIsUsed()
    {
        Assert.Equal("/custom/data/recall/recall.db", RecallDataPath.GetDatabasePath("/custom/data/", "/home/user"));
        Assert.Equal("/custom/data/recall/recall.log", RecallDataPath.GetLogFilePath("/custom/data/", "/home/user"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/data")]
    public void MissingOrRelativeXdgDirectoryFallsBackToHome(string? dataHome)
    {
        Assert.Equal("/home/user/.local/share/recall/recall.db", RecallDataPath.GetDatabasePath(dataHome, "/home/user"));
        Assert.Equal("/home/user/.local/share/recall/recall.log", RecallDataPath.GetLogFilePath(dataHome, "/home/user"));
    }
}
