namespace Recall.Core.Settings;

public sealed record ApplicationSettings(bool AutomaticUpdates = true, bool ScanOnStartup = false);
