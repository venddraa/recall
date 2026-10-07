using Recall.Core.Scanning;

namespace Recall.Core.Storage;

public sealed record StoredFile(long Id, ScannedFile File, DateTime ScannedAtUtc);
