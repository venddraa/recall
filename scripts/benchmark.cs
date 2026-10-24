#:project ../src/Recall.Core/Recall.Core.csproj
#:property PublishAot=false
#:property Nullable=enable

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Recall.Core.Extraction;
using Recall.Core.Scanning;
using Recall.Core.Search;
using Recall.Core.Storage;

// Run from the repository root:
// dotnet run --file scripts/benchmark.cs --configuration Release --no-cache -- --files 10000
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args is ["--help"])
{
    PrintUsage();
    return 0;
}

var fileCount = 10_000;
var iterations = 25;
var options = new HashSet<string>(StringComparer.Ordinal);
for (var index = 0; index < args.Length; index += 2)
{
    var option = args[index];
    if (index + 1 >= args.Length || option is not ("--files" or "--iterations") || !options.Add(option)
        || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        || (option == "--files" ? value is < 1 or > 100_000 : value is < 5 or > 100))
    {
        Console.Error.WriteLine("Argumen tidak valid. --files: 1–100000; --iterations: 5–100. Opsi tidak boleh berulang.");
        PrintUsage();
        return 2;
    }
    if (option == "--files") fileCount = value;
    else iterations = value;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += onCancel;
DirectoryInfo? temporaryDirectory = null;
try
{
    var token = cancellation.Token;
    temporaryDirectory = Directory.CreateTempSubdirectory("recall-benchmark-");
    var documents = Path.Combine(temporaryDirectory.FullName, "documents");
    var database = Path.Combine(temporaryDirectory.FullName, "data", "recall.db");
    Console.WriteLine($"Recall benchmark | {RuntimeInformation.FrameworkDescription} | {RuntimeInformation.ProcessArchitecture}");
    Console.WriteLine($"{RuntimeInformation.OSDescription} | {Environment.ProcessorCount} logical processors");
#if DEBUG
    Console.WriteLine("PERHATIAN: build Debug. Gunakan --configuration Release untuk membandingkan performa.");
#endif
    Console.WriteLine($"Corpus: {fileCount:N0} file TXT sintetis; {iterations} pengukuran per query setelah 1 pemanasan.");
    Console.WriteLine("Hanya data sementara milik benchmark yang dipakai. Ctrl+C untuk membatalkan.");
    Console.WriteLine("Menyiapkan corpus (waktu persiapan tidak termasuk pengukuran)...");
    const string paragraph = "Synthetic local documents describe project notes, calculations, observations, and daily work.\n";
    var padding = string.Concat(Enumerable.Repeat(paragraph, 12));
    long sourceBytes = 0;
    for (var index = 0; index < fileCount; index++)
    {
        token.ThrowIfCancellationRequested();
        var folder = Path.Combine(documents, $"batch-{index / 100:D4}");
        if (index % 100 == 0) Directory.CreateDirectory(folder);
        var name = index % 10 == 0 ? $"BAPP-report-{index:D6}.txt" : $"note-{index:D6}.txt";
        var text = $"Recordcode{index:D6}\n" + padding
            + (index % 20 == 0 ? "The clear-sky index transformation is applied.\n" : "Other unrelated measurements.\n");
        await File.WriteAllTextAsync(Path.Combine(folder, name), text, new UTF8Encoding(false), token);
        sourceBytes += Encoding.UTF8.GetByteCount(text);
    }
    Console.WriteLine($"Ukuran corpus: {sourceBytes / 1048576d:F2} MiB");

    var scanner = new FileScanner();
    var clock = Stopwatch.StartNew();
    var scan = await scanner.ScanAsync([documents], cancellationToken: token);
    clock.Stop();
    Require(scan.Files.Count == fileCount && scan.Failures.Count == 0 && scan.SkippedLinks == 0,
        "Scanner tidak menemukan seluruh corpus.");
    PrintDuration("Scan awal", clock.Elapsed, fileCount);

    var store = new FileMetadataStore(database);
    await store.InitializeAsync(token); // Schema creation is intentionally outside indexing timing.
    var extractor = new CountingExtractor(new FileTextExtractor(5 * 1024 * 1024, 20 * 1024 * 1024, 5 * 1024 * 1024));
    clock.Restart();
    var initial = await store.SynchronizeScanAsync(scan, [documents], extractor, cancellationToken: token);
    clock.Stop();
    Require(initial.StoredFiles == fileCount && initial.StoredTextFiles == fileCount
        && initial.SkippedFiles.Count == 0 && extractor.ExtractionCount == fileCount,
        "Index awal tidak menyimpan seluruh corpus.");
    PrintDuration("Index awal (ekstraksi + SQLite + FTS5)", clock.Elapsed, fileCount);

    clock.Restart();
    var unchangedScan = await scanner.ScanAsync([documents], cancellationToken: token);
    clock.Stop();
    Require(unchangedScan.Files.Count == fileCount && unchangedScan.Failures.Count == 0,
        "Scan ulang tidak menemukan seluruh corpus.");
    PrintDuration("Scan ulang tanpa perubahan", clock.Elapsed, fileCount);
    var previousExtractions = extractor.ExtractionCount;
    clock.Restart();
    var unchanged = await store.SynchronizeScanAsync(unchangedScan, [documents], extractor, cancellationToken: token);
    clock.Stop();
    Require(unchanged.StoredFiles == fileCount && unchanged.StoredTextFiles == fileCount
        && unchanged.RemovedFiles == 0 && unchanged.SkippedFiles.Count == 0
        && extractor.ExtractionCount == previousExtractions, "Index ulang membaca kembali teks yang tidak berubah.");
    PrintDuration("Index ulang tanpa perubahan", clock.Elapsed, fileCount);
    Console.WriteLine("Validasi: teks file yang tidak berubah tidak diekstrak ulang — PASS");

    var uniqueQuery = $"recordcode{fileCount / 2:D6}";
    QueryCase[] queries =
    [
        new("Isi langka", uniqueQuery, documents, 1),
        new("Isi clear sky index", "clear sky index", documents, Math.Min(101, (fileCount + 19) / 20)),
        new("Prefix nama BAP", "BAP", documents, Math.Min(101, (fileCount + 9) / 10)),
        new("Isi umum, halaman 1", "synthetic", documents, Math.Min(101, fileCount)),
        new("Isi umum, halaman 2", "synthetic", documents, Math.Clamp(fileCount - 100, 0, 101), 100),
        new("Isi pada satu subfolder", "synthetic", Path.Combine(documents, "batch-0000"), Math.Min(100, fileCount)),
        new("Tanpa hasil", "notpresenttoken", documents, 0)
    ];
    Console.WriteLine();
    Console.WriteLine($"{"Query",-30} {"p50 ms",10} {"p95 ms",10} {"Baris",7}");
    foreach (var query in queries)
    {
        await Search(query); // Warm up this query before collecting samples.
        var samples = new double[iterations];
        for (var index = 0; index < iterations; index++)
        {
            clock.Restart();
            var results = await store.SearchFoldersAsync(query.Query, [query.Folder], 101, token, query.Offset);
            clock.Stop();
            samples[index] = clock.Elapsed.TotalMilliseconds;
            ValidateResults(query, results);
        }
        Array.Sort(samples);
        Console.WriteLine($"{query.Label,-30} {Percentile(samples, 0.50),10:F2} {Percentile(samples, 0.95),10:F2} {query.ExpectedRows,7}");
    }

    // Prove content search uses the saved index, even when its source folder is no longer available.
    Directory.Move(documents, Path.Combine(temporaryDirectory.FullName, "offline-documents"));
    var offline = await store.SearchFoldersAsync(uniqueQuery, [documents], cancellationToken: token);
    Require(offline.Count == 1 && offline[0].FullPath.StartsWith(documents + Path.DirectorySeparatorChar, StringComparison.Ordinal),
        "Pencarian isi tidak bisa membaca indeks tanpa folder corpus.");
    Console.WriteLine("Validasi pencarian dari indeks tanpa folder sumber — PASS");
    var databaseBytes = new[] { database, database + "-wal", database + "-shm" }
        .Where(File.Exists).Sum(path => new FileInfo(path).Length);
    Console.WriteLine($"Ukuran database termasuk WAL/SHM: {databaseBytes / 1048576d:F2} MiB");
    Console.WriteLine("Waktu query mencakup koneksi SQLite, filter folder, ranking, dan snippet (101 baris untuk lookahead UI).");
    Console.WriteLine("Ini baseline sintetis dengan cache hangat, bukan waktu render UI atau pengujian PDF/DOCX/IPYNB.");
    return 0;

    async Task Search(QueryCase query)
        => ValidateResults(query, await store.SearchFoldersAsync(query.Query, [query.Folder], 101, token, query.Offset));
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Benchmark dibatalkan.");
    return 130;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Benchmark gagal: {exception.GetType().Name}: {exception.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= onCancel;
    if (temporaryDirectory is not null)
    {
        try { temporaryDirectory.Delete(recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Data uji sementara belum terhapus: {temporaryDirectory.FullName}");
        }
    }
}

static void ValidateResults(QueryCase query, IReadOnlyList<SearchResult> results)
{
    Require(results.Count == query.ExpectedRows, $"Jumlah hasil tidak sesuai untuk {query.Label}.");
    Require(results.Select(result => result.FullPath).Distinct(StringComparer.Ordinal).Count() == results.Count,
        $"Hasil duplikat untuk {query.Label}.");
    if (query.Query != "BAP")
        Require(results.All(result => result.SnippetParts?.Any(part => part.IsMatch) == true),
            $"Snippet tidak menyorot isi yang cocok untuk {query.Label}.");
}

static double Percentile(double[] sorted, double fraction) => sorted[(int)Math.Ceiling(sorted.Length * fraction) - 1];

static void PrintDuration(string label, TimeSpan duration, int files) =>
    Console.WriteLine($"{label}: {duration.TotalMilliseconds:F2} ms ({files / duration.TotalSeconds:N0} file/detik)");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void PrintUsage() => Console.WriteLine(
    "dotnet run --file scripts/benchmark.cs --configuration Release --no-cache -- [--files 10000] [--iterations 25]");

sealed record QueryCase(string Label, string Query, string Folder, int ExpectedRows, int Offset = 0);

sealed class CountingExtractor(ITextExtractor inner) : ITextExtractor
{
    private int _extractionCount;
    public int ExtractionCount => Volatile.Read(ref _extractionCount);
    public bool CanReuse(ScannedFile file) => inner.CanReuse(file);
    public TextExtractionResult Extract(ScannedFile file, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _extractionCount);
        return inner.Extract(file, cancellationToken);
    }
}
