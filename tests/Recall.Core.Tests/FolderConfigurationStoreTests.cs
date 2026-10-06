using Recall.Core.Folders;

namespace Recall.Core.Tests;

public sealed class FolderConfigurationStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-tests-{Guid.NewGuid():N}");

    private FolderConfigurationStore CreateStore() => new(Path.Combine(_directory, "config", "folders.json"));

    [Fact]
    public async Task MissingConfigurationStartsEmptyWithoutCreatingFiles()
    {
        var store = CreateStore();
        Assert.Empty(await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task SavedFoldersSurviveRestartAndDeduplicateEquivalentPaths()
    {
        var store = CreateStore();
        await store.SaveAsync(["/missing/Documents/", "/missing/Documents", "/missing/documents", "/missing/My Notes"],
            TestContext.Current.CancellationToken);

        var restartedStore = CreateStore();
        Assert.Equal(new[] { "/missing/Documents", "/missing/documents", "/missing/My Notes" },
            await restartedStore.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemovingSelectionDoesNotChangeOriginalFiles()
    {
        Directory.CreateDirectory(_directory);
        var documentPath = Path.Combine(_directory, "original.txt");
        await File.WriteAllTextAsync(documentPath, "123 remains untouched", TestContext.Current.CancellationToken);
        var store = CreateStore();
        await store.SaveAsync([_directory], TestContext.Current.CancellationToken);
        await store.SaveAsync([], TestContext.Current.CancellationToken);

        Assert.Empty(await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("123 remains untouched", await File.ReadAllTextAsync(documentPath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"folders\":[]}")]
    [InlineData("{\"version\":1,\"folders\":[\"relative/path\"]}")]
    [InlineData("{\"version\":1,\"folders\":[null]}")]
    public async Task InvalidConfigurationIsReportedAndPreserved(string original)
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, original, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledOrInvalidSavePreservesPreviouslySavedFolders()
    {
        var store = CreateStore();
        await store.SaveAsync(["/original"], TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(["/replacement"], cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(["relative"], TestContext.Current.CancellationToken));
        Assert.Equal(new[] { "/original" }, await store.LoadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FailedReplacementPreservesDestinationAndCleansTemporaryFile()
    {
        var store = CreateStore();
        Directory.CreateDirectory(store.FilePath);
        var sentinel = Path.Combine(store.FilePath, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep", TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(["/selected"], TestContext.Current.CancellationToken));
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task ConfigurationHasOwnerOnlyPermissionsOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var store = CreateStore();
        await store.SaveAsync(["/private"], TestContext.Current.CancellationToken);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
