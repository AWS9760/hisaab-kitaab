using System.Globalization;
using System.Text.RegularExpressions;

namespace HisaabKitaab.Services;

/// <summary>
/// One backup copy of a file.
/// </summary>
public record BackupInfo(string Path, DateTime Taken, long Size);

/// <summary>
/// A workbook that has backups: its year folder and file name, e.g. 2026 and "Sept_2026.xlsx".
/// </summary>
public record BackedUpWorkbook(int Year, string FileName);

/// <summary>
/// Copies each workbook (and settings.json) into a backup folder every time
/// it's saved, and prunes old copies:
///
///   {backup folder}/{year}/{name}/{name} 2026-09-29 14-05-12.xlsx
///   {backup folder}/Settings/settings 2026-09-29 14-05-12.json
///
/// Each file keeps its newest <see cref="KeepRecent"/> copies plus the last
/// copy of each of the last <see cref="KeepDays"/> days. Only files named
/// like backups are ever deleted. A failed backup never stops a save; it's
/// reported through <see cref="LastError"/>.
/// </summary>
public sealed partial class BackupService
{
    public const int KeepRecent = 20;

    public const int KeepDays = 30;

    public const string SettingsFolderName = "Settings";

    private const string StampFormat = "yyyy-MM-dd HH-mm-ss";

    private readonly Func<(bool Enabled, string Folder)> _options;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();

    /// <param name="options">Whether to back up, and where; read at every backup.</param>
    public BackupService(Func<(bool Enabled, string Folder)> options, TimeProvider? clock = null)
    {
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    public string Folder => _options().Folder;

    /// <summary>
    /// Why the most recent backup failed, or null if it worked.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// The most recent backup made.
    /// </summary>
    public BackupInfo? LastBackup { get; private set; }

    /// <summary>
    /// Backs up a workbook just saved at <paramref name="path"/> (in its year folder).
    /// </summary>
    public BackupInfo? BackUpWorkbook(string path)
    {
        var year = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? string.Empty;
        return BackUp(path, System.IO.Path.Combine(year, System.IO.Path.GetFileNameWithoutExtension(path)), force: false);
    }

    /// <summary>
    /// Backs up settings.json just saved at <paramref name="path"/>.
    /// </summary>
    public BackupInfo? BackUpSettings(string path) => BackUp(path, SettingsFolderName, force: false);

    /// <summary>
    /// Backs up a workbook even when backups are off, e.g. just before it's
    /// replaced by a restore. Returns null if there's no file to back up.
    /// </summary>
    /// <exception cref="IOException">The copy couldn't be made.</exception>
    public BackupInfo? BackUpWorkbookNow(string path)
    {
        if (!File.Exists(path))
            return null;

        var year = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? string.Empty;
        var result = BackUp(path, System.IO.Path.Combine(year, System.IO.Path.GetFileNameWithoutExtension(path)), force: true);
        return result ?? throw new IOException(LastError ?? "The backup couldn't be made.");
    }

    private BackupInfo? BackUp(string path, string seriesFolder, bool force)
    {
        var (enabled, root) = _options();
        if (!enabled && !force)
            return null;

        lock (_lock)
        {
            try
            {
                var folder = System.IO.Path.Combine(root, seriesFolder);
                Directory.CreateDirectory(folder);

                // Nothing new to keep if the newest copy is exactly this file
                // (e.g. backing up before a restore, when the last save was backed up already).
                if (List(folder).FirstOrDefault() is { } newest && SameContents(newest.Path, path))
                {
                    LastError = null;
                    return newest;
                }

                var now = _clock.GetLocalNow().DateTime;
                var name = System.IO.Path.GetFileNameWithoutExtension(path);
                var extension = System.IO.Path.GetExtension(path);
                var target = System.IO.Path.Combine(folder, $"{name} {now.ToString(StampFormat, CultureInfo.InvariantCulture)}{extension}");
                for (var n = 2; File.Exists(target); n++)
                    target = System.IO.Path.Combine(folder, $"{name} {now.ToString(StampFormat, CultureInfo.InvariantCulture)} ({n}){extension}");

                File.Copy(path, target);
                Prune(folder, now);

                LastBackup = new BackupInfo(target, now, new FileInfo(target).Length);
                LastError = null;
                return LastBackup;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                LastError = $"Couldn't back up {System.IO.Path.GetFileName(path)} to {root}: {ex.Message}";
                return null;
            }
        }
    }

    private static bool SameContents(string a, string b)
    {
        var (infoA, infoB) = (new FileInfo(a), new FileInfo(b));
        return infoA.Length == infoB.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
    }

    /// <summary>
    /// The name of the file a backup is a copy of, e.g. "Sept_2026.xlsx" or "settings.json".
    /// </summary>
    public static string OriginalName(string backupPath)
    {
        var series = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(backupPath)) ?? string.Empty;
        return series == SettingsFolderName ? "settings.json" : series + System.IO.Path.GetExtension(backupPath);
    }

    // ---- Listing --------------------------------------------------------------

    /// <summary>
    /// Workbooks that have backups in the current folder, newest year first.
    /// </summary>
    public IReadOnlyList<BackedUpWorkbook> BackedUpWorkbooks()
    {
        var root = _options().Folder;
        if (!Directory.Exists(root))
            return Array.Empty<BackedUpWorkbook>();

        var result = new List<BackedUpWorkbook>();
        try
        {
            foreach (var yearDir in Directory.EnumerateDirectories(root))
            {
                if (!int.TryParse(System.IO.Path.GetFileName(yearDir), out var year))
                    continue;

                foreach (var series in Directory.EnumerateDirectories(yearDir))
                {
                    var fileName = System.IO.Path.GetFileName(series) + ".xlsx";
                    var known = ExcelService.TryParseZakatFileName(fileName, out _)
                                || Models.YearMonth.TryParseFileName(fileName, out _);
                    if (known && List(series).Count > 0)
                        result.Add(new BackedUpWorkbook(year, fileName));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable backup folder just shows nothing to restore.
        }

        return result
            .OrderByDescending(w => w.Year)
            .ThenBy(w => ExcelService.TryParseZakatFileName(w.FileName, out _) ? 0 : 1)
            .ThenByDescending(w => Models.YearMonth.TryParseFileName(w.FileName, out var m) ? m.Month : 0)
            .ToList();
    }

    /// <summary>
    /// A workbook's backups, newest first.
    /// </summary>
    public IReadOnlyList<BackupInfo> BackupsOf(BackedUpWorkbook workbook) =>
        List(System.IO.Path.Combine(_options().Folder, workbook.Year.ToString("D4"), System.IO.Path.GetFileNameWithoutExtension(workbook.FileName)));

    private static IReadOnlyList<BackupInfo> List(string folder)
    {
        if (!Directory.Exists(folder))
            return Array.Empty<BackupInfo>();

        return Directory.EnumerateFiles(folder)
            .Select(path => TryParseStamp(path, out var taken, out var sequence)
                ? (Info: new BackupInfo(path, taken, new FileInfo(path).Length), Sequence: sequence)
                : (Info: null, Sequence: 0))
            .Where(x => x.Info is not null)
            .OrderByDescending(x => x.Info!.Taken)
            .ThenByDescending(x => x.Sequence)
            .Select(x => x.Info!)
            .ToList();
    }

    // ---- Pruning ---------------------------------------------------------------

    /// <summary>
    /// Deletes copies beyond the newest <see cref="KeepRecent"/>, except the
    /// last copy of each of the last <see cref="KeepDays"/> days.
    /// </summary>
    internal static void Prune(string folder, DateTime now)
    {
        var backups = List(folder);
        var keep = new HashSet<string>(backups.Take(KeepRecent).Select(b => b.Path), StringComparer.OrdinalIgnoreCase);
        var since = now.Date.AddDays(-(KeepDays - 1));
        foreach (var day in backups.Where(b => b.Taken >= since).GroupBy(b => b.Taken.Date))
            keep.Add(day.First().Path);

        foreach (var old in backups.Where(b => !keep.Contains(b.Path)))
        {
            try
            {
                File.Delete(old.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try again after the next backup.
            }
        }
    }

    // Sequence is 1 for the first copy in a second, then 2, 3… from the "(n)" suffix.
    private static bool TryParseStamp(string path, out DateTime taken, out int sequence)
    {
        taken = default;
        sequence = 1;
        var match = StampPattern().Match(System.IO.Path.GetFileNameWithoutExtension(path));
        if (!match.Success
            || !DateTime.TryParseExact(match.Groups[1].Value, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out taken))
            return false;

        if (match.Groups[2].Success)
            sequence = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    // "{name} 2026-09-29 14-05-12" or "{name} 2026-09-29 14-05-12 (2)".
    [GeneratedRegex(@" (\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2})(?: \((\d{1,6})\))?$")]
    private static partial Regex StampPattern();
}
