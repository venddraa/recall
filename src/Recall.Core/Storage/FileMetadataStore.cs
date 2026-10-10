using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Recall.Core.Folders;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Search;

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
            if (version is not (0 or 1 or 2 or 3))
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

            if (version < 2)
            {
                using var transaction = connection.BeginTransaction();
                using var migrationCommand = connection.CreateCommand();
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = """
                    CREATE TABLE file_text (
                        file_id INTEGER PRIMARY KEY REFERENCES file_metadata(id) ON DELETE CASCADE,
                        status TEXT NOT NULL,
                        content TEXT,
                        processed_at_utc TEXT NOT NULL,
                        CHECK ((status = 'Extracted' AND content IS NOT NULL)
                            OR (status <> 'Extracted' AND content IS NULL))
                    );
                    PRAGMA user_version = 2;
                    """;
                migrationCommand.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }

            if (version < 3)
            {
                using var transaction = connection.BeginTransaction();
                using var migrationCommand = connection.CreateCommand();
                migrationCommand.Transaction = transaction;
                migrationCommand.CommandText = """
                    CREATE VIRTUAL TABLE file_search USING fts5(
                        filename,
                        content,
                        tokenize = 'unicode61 remove_diacritics 2'
                    );
                    INSERT INTO file_search(rowid, filename, content)
                    SELECT m.id, m.filename, COALESCE(t.content, '')
                    FROM file_metadata AS m
                    LEFT JOIN file_text AS t ON t.file_id = m.id;
                    PRAGMA user_version = 3;
                    """;
                migrationCommand.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }

            Trace.TraceInformation("Recall metadata database initialized.");
        }, cancellationToken);
    }

    public Task<long> SaveScanAsync(IEnumerable<ScannedFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        return Task.Run(() => SaveBatch(files, null, null, cancellationToken).StoredFiles, cancellationToken);
    }

    public Task<TextIndexResult> IndexScanAsync(IEnumerable<ScannedFile> files, PlainTextExtractor extractor,
        IProgress<TextIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(extractor);
        return Task.Run(() => SaveBatch(files, extractor, progress, cancellationToken), cancellationToken);
    }

    private TextIndexResult SaveBatch(IEnumerable<ScannedFile> files, PlainTextExtractor? extractor,
        IProgress<TextIndexProgress>? progress, CancellationToken cancellationToken)
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

        using var textCommand = connection.CreateCommand();
        textCommand.Transaction = transaction;
        textCommand.CommandText = """
            INSERT INTO file_text (file_id, status, content, processed_at_utc)
            VALUES ((SELECT id FROM file_metadata WHERE full_path = $path), $status, $content, $processed)
            ON CONFLICT(file_id) DO UPDATE SET status = excluded.status, content = excluded.content,
                processed_at_utc = excluded.processed_at_utc;
            """;
        var textPath = textCommand.Parameters.Add("$path", SqliteType.Text);
        var status = textCommand.Parameters.Add("$status", SqliteType.Text);
        var content = textCommand.Parameters.Add("$content", SqliteType.Text);
        var processedAt = textCommand.Parameters.Add("$processed", SqliteType.Text);
        textCommand.Prepare();
        using var reuseCommand = connection.CreateCommand();
        reuseCommand.Transaction = transaction;
        reuseCommand.CommandText = """
            SELECT COUNT(*) FROM file_metadata AS m JOIN file_text AS t ON t.file_id = m.id
            WHERE m.full_path = $path AND m.size = $size AND m.modified_at_utc = $modified
                AND t.status = 'Extracted';
            """;
        var reusePath = reuseCommand.Parameters.Add("$path", SqliteType.Text);
        var reuseSize = reuseCommand.Parameters.Add("$size", SqliteType.Integer);
        var reuseModified = reuseCommand.Parameters.Add("$modified", SqliteType.Text);
        reuseCommand.Prepare();
        using var invalidateCommand = connection.CreateCommand();
        invalidateCommand.Transaction = transaction;
        invalidateCommand.CommandText = """
            DELETE FROM file_text WHERE file_id IN
                (SELECT id FROM file_metadata WHERE full_path = $path
                    AND (size <> $size OR modified_at_utc <> $modified));
            """;
        var invalidatePath = invalidateCommand.Parameters.Add("$path", SqliteType.Text);
        var invalidateSize = invalidateCommand.Parameters.Add("$size", SqliteType.Integer);
        var invalidateModified = invalidateCommand.Parameters.Add("$modified", SqliteType.Text);
        invalidateCommand.Prepare();
        using var searchCommand = connection.CreateCommand();
        searchCommand.Transaction = transaction;
        searchCommand.CommandText = """
            DELETE FROM file_search WHERE rowid =
                (SELECT id FROM file_metadata WHERE full_path = $path);
            INSERT INTO file_search(rowid, filename, content)
            SELECT m.id, m.filename, COALESCE(t.content, '')
            FROM file_metadata AS m
            LEFT JOIN file_text AS t ON t.file_id = m.id
            WHERE m.full_path = $path;
            """;
        var searchPath = searchCommand.Parameters.Add("$path", SqliteType.Text);
        searchCommand.Prepare();
        var extracted = 0;
        var skipped = new Dictionary<TextExtractionStatus, int>();
        var processed = 0;
        var timer = Stopwatch.StartNew();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pathParameter.Value = FolderPath.Normalize(file.FullPath);
            nameParameter.Value = file.Filename;
            extensionParameter.Value = file.Extension;
            sizeParameter.Value = file.Size;
            modifiedParameter.Value = file.ModifiedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            reusePath.Value = invalidatePath.Value = pathParameter.Value;
            reuseSize.Value = invalidateSize.Value = sizeParameter.Value;
            reuseModified.Value = invalidateModified.Value = modifiedParameter.Value;
            var reuse = extractor is not null
                && Convert.ToInt64(reuseCommand.ExecuteScalar(), CultureInfo.InvariantCulture) > 0
                && extractor.CanReuse(file);
            invalidateCommand.ExecuteNonQuery();
            command.ExecuteNonQuery();
            if (extractor is not null)
            {
                var result = reuse
                    ? new TextExtractionResult(TextExtractionStatus.Extracted)
                    : extractor.Extract(file, cancellationToken);
                textPath.Value = pathParameter.Value;
                status.Value = result.Status.ToString();
                content.Value = (object?)result.Text ?? DBNull.Value;
                processedAt.Value = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                if (!reuse)
                    textCommand.ExecuteNonQuery();
                if (result.Status == TextExtractionStatus.Extracted)
                    extracted++;
                else
                {
                    skipped[result.Status] = skipped.GetValueOrDefault(result.Status) + 1;
                    Trace.TraceInformation("File content skipped ({0}): {1}", result.Status, file.FullPath);
                }
                processed++;
                if (timer.ElapsedMilliseconds >= 250)
                {
                    progress?.Report(new TextIndexProgress(processed, extracted));
                    timer.Restart();
                }
            }

            searchPath.Value = pathParameter.Value;
            searchCommand.ExecuteNonQuery();
        }

        using var countCommand = connection.CreateCommand();
        countCommand.Transaction = transaction;
        countCommand.CommandText = "SELECT COUNT(*) FROM file_metadata;";
        var count = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        countCommand.CommandText = "SELECT COUNT(*) FROM file_text WHERE status = 'Extracted';";
        var textCount = Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        progress?.Report(new TextIndexProgress(processed, extracted));
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        Trace.TraceInformation("Metadata batch committed; {0} files stored in total.", count);
        return new TextIndexResult(count, textCount, extracted, skipped);
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

    public Task<long> GetTextFileCountAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM file_text WHERE status = 'Extracted';";
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        var expression = SearchQuery.BuildFtsExpression(query);
        if (expression is null)
        {
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        }

        return Task.Run<IReadOnlyList<SearchResult>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT m.id, m.full_path, m.filename, m.extension, m.size, m.modified_at_utc,
                    bm25(file_search, 10.0, 1.0) AS rank
                FROM file_search
                JOIN file_metadata AS m ON m.id = file_search.rowid
                WHERE file_search MATCH $query
                ORDER BY rank, m.filename COLLATE NOCASE, m.full_path
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$query", expression);
            command.Parameters.AddWithValue("$limit", limit);
            using var registration = cancellationToken.Register(command.Cancel);
            var results = new List<SearchResult>();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    results.Add(new SearchResult(
                        reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                        reader.GetInt64(4), ParseUtc(reader.GetString(5)), reader.GetDouble(6)));
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (SqliteException exception) when (cancellationToken.IsCancellationRequested
                                                     && exception.SqliteErrorCode == 9)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return results.AsReadOnly();
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
            ForeignKeys = true,
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
