using Recall.Core.Folders;

namespace Recall.Core.Tests;

public sealed class FolderPathTests
{
    [Fact]
    public void NormalizeCollapsesEquivalentPathsButPreservesRoot()
    {
        Assert.Equal("/home/user/Documents", FolderPath.Normalize("/home/user/Documents/notes/../"));
        Assert.Equal("/", FolderPath.Normalize("/"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Documents")]
    [InlineData("~/Documents")]
    public void NormalizeRejectsEmptyAndRelativePaths(string path)
    {
        Assert.Throws<ArgumentException>(() => FolderPath.Normalize(path));
    }

    [Fact]
    public void NormalizePreservesSpacesAndCaseInLinuxPaths()
    {
        Assert.Equal("/home/user/ Notes ", FolderPath.Normalize("/home/user/ Notes /"));
        Assert.NotEqual(FolderPath.Normalize("/home/user/notes"), FolderPath.Normalize("/home/user/Notes"));
    }

    [Fact]
    public void ConfigurationLocationUsesAbsoluteXdgPathOrHomeFallback()
    {
        Assert.Equal("/custom/config/recall/folders.json",
            FolderPath.GetConfigurationFilePath("/custom/config", "/home/user"));
        Assert.Equal("/home/user/.config/recall/folders.json",
            FolderPath.GetConfigurationFilePath(null, "/home/user"));
        Assert.Equal("/home/user/.config/recall/folders.json",
            FolderPath.GetConfigurationFilePath("relative/config", "/home/user"));
    }
}
