using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Recall.Core.Folders;
using Recall.Core.Scanning;

namespace Recall.Core.Storage;

public sealed class FileMetadataStore
{
    public FileMetadataStore(string databasePath)
    {
        DatabasePath = FolderPath.Normalize(databasePath);
    }

    public string DatabasePath { get; }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateDatabaseFile();
            using var connection = OpenConnection();
            using var versionCommand = connection.CreateCommand();
            versionCommand.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (version is not (0 or 1))
            {
                throw new InvalidDataException($"Unsupported Recall database version: {version}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            using var journalCommand = connection.CreateCommand();
            journalCommand.CommandText = "PRAGMA journal_mode = WAL;";
            journalCommand.ExecuteScalar();

            if (version == 0)
            {
                using var transaction = connection.BeginTransaction();
                using var schemaCommand = connection.CreateCommand();
                schemaCommand.Transaction = transaction;
                schemaCommand.CommandText = """
                    CREATE TABLE file_metadata (
                        id INTEGER PRIMARY KEY,
                        full_path TEXT NOT NULL COLLATE BINARY UNIQUE,
                        filename TEXT NOT NULL,
                        extension TEXT NOT NULL,
                        size INTEGER NOT NULL CHECK (size >= 0),
                        modified_at_utc TEXT NOT NULL,
                        scanned_at_utc TEXT NOT NULL
                    );
                    PRAGMA user_version = 1;
                    """;
                schemaCommand.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }

            Trace.TraceInformation("Recall metadata database initialized.");
        }, cancellationToken);
    }

    public Task<long> SaveScanAsync(IEnumerable<ScannedFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            Trace.TraceInformation("Saving scanned file metadata.");
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO file_metadata
                    (full_path, filename, extension, size, modified_at_utc, scanned_at_utc)
                VALUES ($path, $filename, $extension, $size, $modified, $scanned)
                ON CONFLICT(full_path) DO UPDATE SET
                    filename = excluded.filename,
                    extension = excluded.extension,
                    size = excluded.size,
                    modified_at_utc = excluded.modified_at_utc,
                    scanned_at_utc = excluded.scanned_at_utc;
                """;
            var pathParameter = command.Parameters.Add("$path", SqliteType.Text);
            var nameParameter = command.Parameters.Add("$filename", SqliteType.Text);
            var extensionParameter = command.Parameters.Add("$extension", SqliteType.Text);
            var sizeParameter = command.Parameters.Add("$size", SqliteType.Integer);
            var modifiedParameter = command.Parameters.Add("$modified", SqliteType.Text);
            var scannedParameter = command.Parameters.Add("$scanned", SqliteType.Text);
            command.Prepare();
            scannedParameter.Value = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                pathParameter.Value = FolderPath.Normalize(file.FullPath);
                nameParameter.Value = file.Filename;
                extensionParameter.Value = file.Extension;
                sizeParameter.Value = file.Size;
                modifiedParameter.Value = file.ModifiedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                command.ExecuteNonQuery();
            }

            using var countCommand = connection.CreateCommand();
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM file_metadata;";
            var count = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            Trace.TraceInformation("Metadata batch committed; {0} files stored in total.", count);
            return count;
        }, cancellationToken);
    }

    public Task<long> GetFileCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM file_metadata;";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public Task<StoredFile?> GetFileAsync(string fullPath, CancellationToken cancellationToken = default)
    {
        var path = FolderPath.Normalize(fullPath);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, full_path, filename, extension, size, modified_at_utc, scanned_at_utc
                FROM file_metadata WHERE full_path = $path;
                """;
            command.Parameters.AddWithValue("$path", path);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new StoredFile(reader.GetInt64(0),
                new ScannedFile(reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetInt64(4), ParseUtc(reader.GetString(5))),
                ParseUtc(reader.GetString(6)));
        }, cancellationToken);
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void CreateDatabaseFile()
    {
        var directory = Path.GetDirectoryName(DatabasePath)!;
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            Directory.CreateDirectory(directory);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.ReadWrite
        };
        if (OperatingSystem.IsLinux())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(DatabasePath, options);
    }

    private static DateTime ParseUtc(string value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
