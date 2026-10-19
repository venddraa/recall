using Recall.Core.Settings;

namespace Recall.Core.Tests;

public sealed class ApplicationSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"recall-settings-tests-{Guid.NewGuid():N}");

    private ApplicationSettingsStore CreateStore() => new(Path.Combine(_directory, "config", "settings.json"));

    [Fact]
    public async Task FirstLaunchKeepsAutomaticUpdatesEnabledWithoutCreatingConfiguration()
    {
        var settings = await CreateStore().LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(settings.AutomaticUpdates);
        Assert.False(settings.ScanOnStartup);
        Assert.False(Directory.Exists(_directory));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task SavedChoicesSurviveRestartAndReplacement(bool automaticUpdates, bool scanOnStartup)
    {
        var store = CreateStore();
        await store.SaveAsync(new(!automaticUpdates, !scanOnStartup), TestContext.Current.CancellationToken);
        var expected = new ApplicationSettings(automaticUpdates, scanOnStartup);
        await store.SaveAsync(expected, TestContext.Current.CancellationToken);

        Assert.Equal(expected, await CreateStore().LoadAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OlderSettingsKeepStartupScanningOffWithoutRewritingTheFile(bool automaticUpdates)
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        var original = "{\"version\":1,\"automaticUpdates\":" + (automaticUpdates ? "true" : "false") + "}";
        await File.WriteAllTextAsync(store.FilePath, original, TestContext.Current.CancellationToken);

        var settings = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new ApplicationSettings(automaticUpdates, ScanOnStartup: false), settings);
        Assert.Equal(original, await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":2,\"automaticUpdates\":false}")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"automaticUpdates\":null}")]
    [InlineData("{\"version\":1,\"automaticUpdates\":\"false\"}")]
    [InlineData("{\"version\":1,\"automaticUpdates\":true,\"scanOnStartup\":null}")]
    [InlineData("{\"version\":1,\"automaticUpdates\":true,\"scanOnStartup\":\"true\"}")]
    [InlineData("{\"version\":1,\"automaticUpdates\":true,\"scanOnStartup\":1}")]
    public async Task UnreadableSettingsAreReportedWithoutOverwritingTheSavedChoice(string original)
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, original, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(original, await File.ReadAllTextAsync(store.FilePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledSavePreservesThePreviousChoice()
    {
        var store = CreateStore();
        var previous = new ApplicationSettings(AutomaticUpdates: false, ScanOnStartup: true);
        await store.SaveAsync(previous, TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(
            new(AutomaticUpdates: true, ScanOnStartup: false), cancellation.Token));
        Assert.Equal(previous, await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task FailedSaveLeavesDestinationIntactAndCleansTemporaryFiles()
    {
        var store = CreateStore();
        Directory.CreateDirectory(store.FilePath);
        var sentinel = Path.Combine(store.FilePath, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep", TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<IOException>(() => store.SaveAsync(new(false), TestContext.Current.CancellationToken));
        Assert.Equal("keep", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));
    }

    [Fact]
    public async Task SettingsDoNotChangeTheFolderSelectionOrDocuments()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        var foldersPath = Path.Combine(Path.GetDirectoryName(store.FilePath)!, "folders.json");
        const string folders = "{\"version\":1,\"folders\":[\"/selected\"]}";
        var documentPath = Path.Combine(_directory, "original.txt");
        await File.WriteAllTextAsync(foldersPath, folders, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(documentPath, "original content", TestContext.Current.CancellationToken);

        await store.SaveAsync(new(false), TestContext.Current.CancellationToken);

        Assert.Equal(folders, await File.ReadAllTextAsync(foldersPath, TestContext.Current.CancellationToken));
        Assert.Equal("original content", await File.ReadAllTextAsync(documentPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExplicitSaveCanReplaceUnreadableSettings()
    {
        var store = CreateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        await File.WriteAllTextAsync(store.FilePath, "broken JSON", TestContext.Current.CancellationToken);

        await store.SaveAsync(new(false), TestContext.Current.CancellationToken);

        Assert.False((await CreateStore().LoadAsync(TestContext.Current.CancellationToken)).AutomaticUpdates);
    }

    [Fact]
    public async Task SettingsArePrivateToTheOwnerOnLinux()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var store = CreateStore();
        await store.SaveAsync(new(false), TestContext.Current.CancellationToken);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(store.FilePath)!));
    }

    [Theory]
    [InlineData("/custom/config/", "/custom/config/recall/settings.json")]
    [InlineData(null, "/home/user/.config/recall/settings.json")]
    [InlineData("", "/home/user/.config/recall/settings.json")]
    [InlineData("relative/config", "/home/user/.config/recall/settings.json")]
    public void SettingsUseTheSameXdgDirectoryAsFolderConfiguration(string? xdgConfigHome, string expected)
    {
        Assert.Equal(expected, ApplicationSettingsStore.GetFilePath(xdgConfigHome, "/home/user"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
