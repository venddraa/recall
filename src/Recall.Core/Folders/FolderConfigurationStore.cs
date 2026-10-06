using System.Diagnostics;
using System.Text.Json;

namespace Recall.Core.Folders;

public sealed class FolderConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public FolderConfigurationStore(string filePath)
    {
        FilePath = FolderPath.Normalize(filePath);
    }

    public string FilePath { get; }

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 4096, FileOptions.Asynchronous);
            var settings = await JsonSerializer.DeserializeAsync<FolderSettings>(stream,
                JsonOptions, cancellationToken);

            if (settings is null || settings.Version != 1 || settings.Folders is null)
            {
                throw new InvalidDataException("The folder configuration has an invalid format or version.");
            }

            try
            {
                return Array.AsReadOnly(settings.Folders.Select(FolderPath.Normalize)
                    .Distinct(StringComparer.Ordinal).ToArray());
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The folder configuration contains an invalid path.", exception);
            }
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The folder configuration is not valid JSON.", exception);
        }
    }

    public async Task SaveAsync(IEnumerable<string> folders, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var normalizedFolders = folders.Select(FolderPath.Normalize)
            .Distinct(StringComparer.Ordinal).ToArray();
        cancellationToken.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(FilePath)!;
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = Path.Combine(directory, $".folders-{Guid.NewGuid():N}.tmp");
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
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new FolderSettings(1, normalizedFolders), JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Could not clean up temporary folder configuration: {0}", exception.Message);
            }
        }
    }

    private sealed record FolderSettings(int Version, string[]? Folders);
}
