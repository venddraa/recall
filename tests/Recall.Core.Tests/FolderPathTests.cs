using Recall.Core.Folders;

namespace Recall.Core.Tests;

public sealed class FolderPathTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-folder-paths-{Guid.NewGuid():N}");

    [Fact]
    public void RegularFoldersAndFilesHaveNoLinkedComponents()
    {
        var folder = Path.Combine(_directory, "nested");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "notes.txt");
        File.WriteAllText(file, "original content");

        Assert.False(FolderPath.HasSymbolicLink(folder));
        Assert.False(FolderPath.HasSymbolicLink(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DetectsLinkedFolderAndLinkedAncestor(bool nested)
    {
        var original = Path.Combine(_directory, "original");
        Directory.CreateDirectory(Path.Combine(original, "nested"));
        var link = Path.Combine(_directory, "linked");
        Directory.CreateSymbolicLink(link, original);

        Assert.True(FolderPath.HasSymbolicLink(nested ? Path.Combine(link, "nested") : link));
        Assert.False(FolderPath.HasSymbolicLink(Path.Combine(original, "nested")));
    }

    [Fact]
    public void DetectsLinkedFileWithoutChangingItsTarget()
    {
        Directory.CreateDirectory(_directory);
        var original = Path.Combine(_directory, "original.txt");
        byte[] bytes = [65, 13, 10, 66];
        File.WriteAllBytes(original, bytes);
        var modified = File.GetLastWriteTimeUtc(original);
        var link = Path.Combine(_directory, "linked.txt");
        File.CreateSymbolicLink(link, original);

        Assert.True(FolderPath.HasSymbolicLink(link));
        Assert.Equal(bytes, File.ReadAllBytes(original));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(original));
    }

    [Fact]
    public void RecognizesBrokenLinks()
    {
        Directory.CreateDirectory(_directory);
        var link = Path.Combine(_directory, "broken");
        Directory.CreateSymbolicLink(link, Path.Combine(_directory, "missing-target"));

        Assert.True(FolderPath.HasSymbolicLink(link));
    }

    [Fact]
    public void MissingPathIsReportedRatherThanTreatedAsRegular()
    {
        Assert.ThrowsAny<IOException>(() => FolderPath.HasSymbolicLink(Path.Combine(_directory, "missing")));
    }

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

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
