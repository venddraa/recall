using System.Diagnostics;
using System.Text.Json;
using Recall.Core.Folders;

namespace Recall.Core.Settings;

public sealed class ApplicationSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public ApplicationSettingsStore(string filePath) => FilePath = FolderPath.Normalize(filePath);

    public string FilePath { get; }

    public static string GetFilePath(string? xdgConfigHome, string userProfile) =>
        Path.Combine(Path.GetDirectoryName(FolderPath.GetConfigurationFilePath(xdgConfigHome, userProfile))!,
            "settings.json");

    public async Task<ApplicationSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            var settings = await JsonSerializer.DeserializeAsync<SavedSettings>(stream, JsonOptions, cancellationToken);
            if (settings is null || settings.Version != 1 || settings.AutomaticUpdates is null)
                throw new InvalidDataException("The application settings have an invalid format or version.");
            return new(settings.AutomaticUpdates.Value);
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The application settings are not valid JSON.", exception);
        }
    }

    public async Task SaveAsync(ApplicationSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".settings-{Guid.NewGuid():N}.tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous
            };
            if (OperatingSystem.IsLinux())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new SavedSettings(1, settings.AutomaticUpdates), JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not clean up temporary application settings: {0}", exception.GetType().Name);
            }
        }
    }

    private sealed record SavedSettings(int Version, bool? AutomaticUpdates);
}
