using System.Text.Json;
using HisaabKitaab.Models;

namespace HisaabKitaab.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to a JSON file in the user's
/// app-data folder, and owns the rules for editing family members and
/// categories. Every change is written to disk immediately.
///
/// Background work (recurring expenses, reminders, workbook saves) reads and
/// changes settings too, so each change and its save happen under one lock.
/// The lists exposed as properties are for the UI thread; other threads use
/// the methods that take the lock.
/// </summary>
public class SettingsService
{
    public const int MaxMemberNameLength = 40;

    public const int MaxCategoryNameLength = 40;

    private AppSettings _settings = CreateDefaultSettings();

    private readonly string _defaultDataFolder;

    // Reentrant, so a locked method can call another.
    private readonly object _sync = new();

    /// <param name="defaultDataFolder">Where workbooks go unless the user picks a folder. Defaults to <see cref="DefaultDataFolder"/>.</param>
    public SettingsService(string filePath, string? defaultDataFolder = null)
    {
        FilePath = filePath;
        _defaultDataFolder = defaultDataFolder ?? DefaultDataFolder;
    }

    /// <summary>
    /// %APPDATA%\HisaabKitaab\settings.json on Windows,
    /// ~/.config/HisaabKitaab/settings.json on Linux.
    /// </summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "HisaabKitaab",
        "settings.json");

    /// <summary>
    /// Documents/Hisaab Kitaab, falling back to the home folder on Linux
    /// systems without a Documents folder.
    /// </summary>
    public static string DefaultDataFolder
    {
        get
        {
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(documents))
                documents = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(documents, "Hisaab Kitaab");
        }
    }

    /// <summary>
    /// Where settings.json is. Changes when the user moves it (<see cref="MoveTo"/>).
    /// </summary>
    public string FilePath { get; private set; }

    /// <summary>
    /// The pointer file that remembers a moved settings folder (see
    /// <see cref="SettingsLocation"/>), or null if settings can't be moved
    /// (e.g. HISAAB_KITAAB_HOME keeps everything in one folder).
    /// </summary>
    public string? LocationPointer { get; init; }

    /// <summary>
    /// Where settings.json goes when it hasn't been moved.
    /// </summary>
    public string DefaultSettingsFilePath { get; init; } = DefaultFilePath;

    public bool CanMoveSettings => LocationPointer is not null;

    public bool IsSettingsFileMoved => !PathsEqual(FilePath, DefaultSettingsFilePath);

    /// <summary>
    /// Folder holding the monthly workbooks.
    /// </summary>
    public string DataFolder =>
        string.IsNullOrWhiteSpace(_settings.DataFolder) ? _defaultDataFolder : _settings.DataFolder;

    /// <summary>
    /// Where workbooks go unless the user has chosen a folder.
    /// </summary>
    public string DefaultDataFolderPath => _defaultDataFolder;

    public bool IsDataFolderCustom => !string.IsNullOrWhiteSpace(_settings.DataFolder);

    /// <summary>
    /// Set when <see cref="Load"/> found a settings file it couldn't read.
    /// The unreadable file is kept alongside under a new name so nothing is lost.
    /// </summary>
    public string? LoadWarning { get; private set; }

    /// <summary>
    /// Adds to <see cref="LoadWarning"/>, e.g. when the chosen settings folder wasn't found.
    /// </summary>
    public void AddLoadWarning(string warning) =>
        LoadWarning = LoadWarning is null ? warning : $"{LoadWarning} {warning}";

    /// <summary>
    /// Records a new workbook folder (null for the default). The files
    /// themselves are moved by <see cref="ExcelService.MoveDataFolder"/>.
    /// </summary>
    public void SetDataFolder(string? folder)
    {
        if (folder is not null && !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Choose a full folder path.", nameof(folder));

        lock (_sync)
        {
            var old = _settings.DataFolder;
            _settings.DataFolder = folder is null || PathsEqual(folder, _defaultDataFolder) ? null : Path.GetFullPath(folder);
            SaveOrRollBack(() => _settings.DataFolder = old);
        }
    }

    /// <summary>
    /// Moves settings.json to <paramref name="folder"/> (null for the default
    /// folder) and remembers the new place for the next start. A settings.json
    /// already there is kept, renamed "settings.replaced-…json".
    /// </summary>
    public void MoveTo(string? folder)
    {
        if (LocationPointer is not { } pointer)
            throw new InvalidOperationException("Settings are kept with the workbooks (HISAAB_KITAAB_HOME), so they can't be moved.");
        if (folder is not null && !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Choose a full folder path.", nameof(folder));

        var newPath = folder is null ? DefaultSettingsFilePath : Path.GetFullPath(Path.Combine(folder, "settings.json"));
        var isDefault = PathsEqual(newPath, DefaultSettingsFilePath);

        lock (_sync)
        {
            if (PathsEqual(newPath, FilePath))
                return;

            var oldPath = FilePath;
            if (File.Exists(newPath))
                File.Move(newPath, Path.Combine(Path.GetDirectoryName(newPath)!, $"settings.replaced-{DateTime.Now:yyyyMMdd-HHmmss}.json"));

            FilePath = newPath;
            try
            {
                Save();
                SettingsLocation.Write(pointer, isDefault ? null : Path.GetDirectoryName(newPath));
            }
            catch
            {
                FilePath = oldPath;
                throw;
            }

            // The old copy is no longer read; the new one has everything.
            try
            {
                File.Delete(oldPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    internal static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public IReadOnlyList<FamilyMember> FamilyMembers => _settings.FamilyMembers;

    public IReadOnlyList<Category> Categories => _settings.Categories!;

    /// <summary>
    /// Raised after a family member add, rename or remove has been saved.
    /// </summary>
    public event EventHandler? FamilyMembersChanged;

    /// <summary>
    /// Raised after any category change (including icon or colour) has been saved.
    /// </summary>
    public event EventHandler? CategoriesChanged;

    public void Load()
    {
        LoadWarning = null;

        if (!File.Exists(FilePath))
        {
            _settings = CreateDefaultSettings();
            return;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            _settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings)
                        ?? throw new JsonException("Settings file is empty.");
            Normalize(_settings);
        }
        catch (JsonException)
        {
            var backupPath = Path.Combine(
                Path.GetDirectoryName(FilePath)!,
                $"settings.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Move(FilePath, backupPath, overwrite: true);
            _settings = CreateDefaultSettings();
            LoadWarning = "Your settings file couldn't be read, so Hisaab Kitaab started with default settings. " +
                          $"The old file was kept as {Path.GetFileName(backupPath)}.";
        }
    }

    public void Save()
    {
        lock (_sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Write to a temp file first so a crash mid-write can't leave a half-written settings.json.
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(_settings, SettingsJsonContext.Default.AppSettings));
            File.Move(tempPath, FilePath, overwrite: true);

            // Under the lock, so a backup copies the file just written.
            Saved?.Invoke(FilePath);
        }
    }

    /// <summary>
    /// Raised after settings.json has been written (with its path), on the saving thread.
    /// </summary>
    public event Action<string>? Saved;

    // ---- Family members -----------------------------------------------------

    /// <summary>
    /// Returns a message explaining why <paramref name="name"/> can't be used,
    /// or null if it's fine. Pass <paramref name="ignoreId"/> when renaming so
    /// a member doesn't clash with its own current name.
    /// </summary>
    public string? ValidateMemberName(string? name, Guid? ignoreId = null) =>
        ValidateName(name, ignoreId, MaxMemberNameLength, "family member",
            _settings.FamilyMembers.Select(m => (m.Id, m.Name)));

    public FamilyMember AddFamilyMember(string name)
    {
        FamilyMember member;
        lock (_sync)
        {
            ThrowIfInvalid(ValidateMemberName(name));

            member = new FamilyMember { Name = name.Trim() };
            _settings.FamilyMembers.Add(member);
            SaveOrRollBack(() => _settings.FamilyMembers.Remove(member));
        }

        FamilyMembersChanged?.Invoke(this, EventArgs.Empty);
        return member;
    }

    public void RenameFamilyMember(Guid id, string newName)
    {
        lock (_sync)
        {
            var member = FindMember(id);
            ThrowIfInvalid(ValidateMemberName(newName, id));

            var oldName = member.Name;
            member.Name = newName.Trim();
            var snapshots = _settings.Recurring.Where(r => r.MemberId == id).Select(r => (r, r.MemberName)).ToList();
            snapshots.ForEach(s => s.r.MemberName = member.Name);
            SaveOrRollBack(() =>
            {
                member.Name = oldName;
                snapshots.ForEach(s => s.r.MemberName = s.MemberName);
            });
        }

        FamilyMembersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Removes a member and any budget for them. Recurring expenses keep the
    /// name they were saved with.
    /// </summary>
    public void RemoveFamilyMember(Guid id)
    {
        List<Budget> budgets;
        lock (_sync)
        {
            var member = FindMember(id);
            var index = _settings.FamilyMembers.IndexOf(member);
            budgets = _settings.Budgets.Where(b => b.Target == BudgetTarget.Member && b.TargetId == id).ToList();

            _settings.FamilyMembers.RemoveAt(index);
            _settings.Budgets.RemoveAll(budgets.Contains);
            SaveOrRollBack(() =>
            {
                _settings.FamilyMembers.Insert(index, member);
                _settings.Budgets.AddRange(budgets);
            });
        }

        FamilyMembersChanged?.Invoke(this, EventArgs.Empty);
        if (budgets.Count > 0)
            BudgetsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Categories ---------------------------------------------------------

    public string? ValidateCategoryName(string? name, Guid? ignoreId = null) =>
        ValidateName(name, ignoreId, MaxCategoryNameLength, "category",
            _settings.Categories!.Select(c => (c.Id, c.Name)));

    /// <summary>
    /// Finds a category by name, ignoring case and surrounding spaces.
    /// Returns null for names that aren't (or are no longer) a category.
    /// </summary>
    public Category? FindCategory(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : _settings.Categories!.FirstOrDefault(c =>
                string.Equals(c.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// Adds a category. Without an explicit colour it takes the next one in
    /// the palette so neighbouring categories look different.
    /// </summary>
    public Category AddCategory(string name, string? icon = null, string? color = null)
    {
        Category category;
        lock (_sync)
        {
            ThrowIfInvalid(ValidateCategoryName(name));

            category = new Category
            {
                Name = name.Trim(),
                Icon = string.IsNullOrWhiteSpace(icon) ? CategoryStyles.DefaultIcon : icon.Trim(),
                Color = CategoryStyles.IsValidColor(color)
                    ? color!
                    : CategoryStyles.Colors[_settings.Categories!.Count % CategoryStyles.Colors.Count],
            };
            _settings.Categories!.Add(category);
            SaveOrRollBack(() => _settings.Categories!.Remove(category));
        }

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
        return category;
    }

    public void RenameCategory(Guid id, string newName)
    {
        lock (_sync)
        {
            var category = FindCategory(id);
            ThrowIfInvalid(ValidateCategoryName(newName, id));

            var oldName = category.Name;
            category.Name = newName.Trim();
            var snapshots = _settings.Recurring.Where(r => r.CategoryId == id).Select(r => (r, r.CategoryName)).ToList();
            snapshots.ForEach(s => s.r.CategoryName = category.Name);
            SaveOrRollBack(() =>
            {
                category.Name = oldName;
                snapshots.ForEach(s => s.r.CategoryName = s.CategoryName);
            });
        }

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetCategoryAppearance(Guid id, string icon, string color)
    {
        if (string.IsNullOrWhiteSpace(icon))
            throw new ArgumentException("Choose an icon.", nameof(icon));
        if (!CategoryStyles.IsValidColor(color))
            throw new ArgumentException("Colour must look like #RRGGBB.", nameof(color));

        lock (_sync)
        {
            var category = FindCategory(id);
            var (oldIcon, oldColor) = (category.Icon, category.Color);
            category.Icon = icon.Trim();
            category.Color = color;
            SaveOrRollBack(() => (category.Icon, category.Color) = (oldIcon, oldColor));
        }

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveCategory(Guid id)
    {
        List<Budget> budgets;
        lock (_sync)
        {
            var category = FindCategory(id);
            var index = _settings.Categories!.IndexOf(category);
            budgets = _settings.Budgets.Where(b => b.Target == BudgetTarget.Category && b.TargetId == id).ToList();

            _settings.Categories.RemoveAt(index);
            _settings.Budgets.RemoveAll(budgets.Contains);
            SaveOrRollBack(() =>
            {
                _settings.Categories.Insert(index, category);
                _settings.Budgets.AddRange(budgets);
            });
        }

        CategoriesChanged?.Invoke(this, EventArgs.Empty);
        if (budgets.Count > 0)
            BudgetsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Credit card --------------------------------------------------------

    public CardSettings CreditCard => _settings.CreditCard;

    /// <summary>
    /// Raised after the card's name, limit or due day has been saved.
    /// </summary>
    public event EventHandler? CardSettingsChanged;

    /// <summary>
    /// Saves the card's details. Pass null to clear the limit or due day.
    /// </summary>
    public void UpdateCreditCard(string? name, decimal? limit, int? dueDay)
    {
        if (limit is < 0)
            throw new ArgumentException("The credit limit can't be negative.", nameof(limit));
        if (dueDay is < 1 or > 31)
            throw new ArgumentException("The due day must be between 1 and 31.", nameof(dueDay));

        lock (_sync)
        {
            var card = _settings.CreditCard;
            var old = (card.Name, card.Limit, card.DueDay);
            card.Name = string.IsNullOrWhiteSpace(name) ? "Credit card" : name.Trim();
            card.Limit = limit is { } l ? Math.Round(l, 2) : null;
            card.DueDay = dueDay;
            SaveOrRollBack(() => (card.Name, card.Limit, card.DueDay) = old);
        }

        CardSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- Budgets ------------------------------------------------------------

    public IReadOnlyList<Budget> Budgets => _settings.Budgets;

    /// <summary>
    /// Raised after a budget is added, changed or removed.
    /// </summary>
    public event EventHandler? BudgetsChanged;

    /// <summary>
    /// Budgets with their category's or member's current name.
    /// </summary>
    /// <remarks>Safe to call from any thread (workbook saves use it for the Summary sheet).</remarks>
    public IReadOnlyList<ResolvedBudget> ResolvedBudgets()
    {
        lock (_sync)
        {
            return _settings.Budgets
                .Select(b => new ResolvedBudget(b.Id, b.Target, TargetName(b.Target, b.TargetId) ?? string.Empty, b.Amount))
                .Where(b => b.Target == BudgetTarget.Everything || b.Name.Length > 0)
                .ToList();
        }
    }

    /// <summary>
    /// Sets the monthly budget for a target, adding it or replacing the amount
    /// of the existing one (there's at most one per target).
    /// </summary>
    public Budget SetBudget(BudgetTarget target, Guid targetId, decimal amount)
    {
        if (amount <= 0)
            throw new ArgumentException("A budget must be more than zero.", nameof(amount));

        Budget budget;
        lock (_sync)
        {
            if (target == BudgetTarget.Everything)
                targetId = Guid.Empty;
            else if (TargetName(target, targetId) is null)
                throw new KeyNotFoundException($"No {(target == BudgetTarget.Category ? "category" : "family member")} with id {targetId}.");

            amount = Math.Round(amount, 2);
            var existing = _settings.Budgets.FirstOrDefault(b => b.Target == target && b.TargetId == targetId);
            if (existing is not null)
            {
                budget = existing;
                var old = existing.Amount;
                existing.Amount = amount;
                SaveOrRollBack(() => existing.Amount = old);
            }
            else
            {
                budget = new Budget { Target = target, TargetId = targetId, Amount = amount };
                _settings.Budgets.Add(budget);
                SaveOrRollBack(() => _settings.Budgets.Remove(budget));
            }
        }

        BudgetsChanged?.Invoke(this, EventArgs.Empty);
        return budget;
    }

    public void RemoveBudget(Guid id)
    {
        lock (_sync)
        {
            var budget = _settings.Budgets.FirstOrDefault(b => b.Id == id) ?? throw new KeyNotFoundException($"No budget with id {id}.");
            var index = _settings.Budgets.IndexOf(budget);
            _settings.Budgets.RemoveAt(index);
            SaveOrRollBack(() => _settings.Budgets.Insert(index, budget));
        }

        BudgetsChanged?.Invoke(this, EventArgs.Empty);
    }

    private string? TargetName(BudgetTarget target, Guid id) => target switch
    {
        BudgetTarget.Everything => string.Empty,
        BudgetTarget.Category => _settings.Categories!.FirstOrDefault(c => c.Id == id)?.Name,
        BudgetTarget.Member => _settings.FamilyMembers.FirstOrDefault(m => m.Id == id)?.Name,
        _ => null,
    };

    // ---- Recurring expenses ---------------------------------------------------

    public const int MaxRecurringNameLength = 60;

    public IReadOnlyList<RecurringExpense> Recurring => _settings.Recurring;

    /// <summary>
    /// Raised after a recurring expense is added, changed, paused or removed
    /// (not when one is marked as added for a month).
    /// </summary>
    public event EventHandler? RecurringChanged;

    public string? ValidateRecurring(RecurringExpense item)
    {
        if (string.IsNullOrWhiteSpace(item.Name))
            return "Give it a name, e.g. House rent.";
        if (item.Name.Trim().Length > MaxRecurringNameLength)
            return $"Names can be at most {MaxRecurringNameLength} characters.";
        if (item.Amount <= 0)
            return "The amount must be more than zero.";
        if (item.DayOfMonth is < 1 or > 31)
            return "The day must be between 1 and 31.";
        if (!Enum.IsDefined(item.PaymentMethod))
            return "Choose how it's paid.";
        return null;
    }

    /// <summary>
    /// Adds or replaces (by id) a recurring expense. Category and member names
    /// are refreshed from their ids.
    /// </summary>
    public RecurringExpense SaveRecurring(RecurringExpense item)
    {
        ThrowIfInvalid(ValidateRecurring(item));

        lock (_sync)
        {
            item.Name = item.Name.Trim();
            item.Amount = Math.Round(item.Amount, 2);
            RefreshNames(item);

            var index = _settings.Recurring.FindIndex(r => r.Id == item.Id);
            if (index >= 0)
            {
                var old = _settings.Recurring[index];

                // Editing never changes what's been added; recurring expenses may
                // have been added in the background while the form was open.
                item.LastAddedFor = old.LastAddedFor;
                _settings.Recurring[index] = item;
                SaveOrRollBack(() => _settings.Recurring[index] = old);
            }
            else
            {
                _settings.Recurring.Add(item);
                SaveOrRollBack(() => _settings.Recurring.Remove(item));
            }
        }

        RecurringChanged?.Invoke(this, EventArgs.Empty);
        return item;
    }

    /// <summary>
    /// Pauses or resumes a recurring expense. When resuming with
    /// <paramref name="today"/>, dates that passed while it was paused are
    /// skipped rather than added all at once.
    /// </summary>
    public void SetRecurringPaused(Guid id, bool paused, DateOnly? today = null)
    {
        lock (_sync)
        {
            var item = FindRecurring(id);
            var old = (item.IsPaused, item.LastAddedFor);
            item.IsPaused = paused;
            if (!paused && today is { } from && item.NextDate < from)
            {
                var month = YearMonth.Of(from);
                if (item.DateIn(month) < from)
                    month = month.AddMonths(1);
                item.LastAddedFor = item.DateIn(month.AddMonths(-1));
            }

            SaveOrRollBack(() => (item.IsPaused, item.LastAddedFor) = old);
        }

        RecurringChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveRecurring(Guid id)
    {
        lock (_sync)
        {
            var item = FindRecurring(id);
            var index = _settings.Recurring.IndexOf(item);
            _settings.Recurring.RemoveAt(index);
            SaveOrRollBack(() => _settings.Recurring.Insert(index, item));
        }

        RecurringChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A copy of each recurring expense as it is now, for use off the UI thread.
    /// </summary>
    public IReadOnlyList<RecurringExpense> RecurringSnapshot() =>
        Locked(() => _settings.Recurring.Select(r => r.Copy()).ToList());

    /// <summary>
    /// A copy of one recurring expense as it is now, or null if it's been removed.
    /// </summary>
    public RecurringExpense? GetRecurring(Guid id) =>
        Locked(() => _settings.Recurring.FirstOrDefault(r => r.Id == id)?.Copy());

    /// <summary>
    /// Records that a recurring expense has been added for <paramref name="date"/>.
    /// Never moves backwards.
    /// </summary>
    public void MarkRecurringAdded(Guid id, DateOnly date)
    {
        lock (_sync)
        {
            var item = FindRecurring(id);
            var old = item.LastAddedFor;
            if (old >= date)
                return;
            item.LastAddedFor = date;
            SaveOrRollBack(() => item.LastAddedFor = old);
        }
    }

    /// <summary>
    /// The category and member names to write on the expense: the current names
    /// if they still exist, otherwise the names saved with the item.
    /// </summary>
    public (string Category, string Member) NamesFor(RecurringExpense item) => Locked(() => (
        (item.CategoryId is { } c ? _settings.Categories!.FirstOrDefault(x => x.Id == c)?.Name : null) ?? item.CategoryName,
        (item.MemberId is { } m ? _settings.FamilyMembers.FirstOrDefault(x => x.Id == m)?.Name : null) ?? item.MemberName));

    private void RefreshNames(RecurringExpense item) => (item.CategoryName, item.MemberName) = NamesFor(item);

    private RecurringExpense FindRecurring(Guid id) =>
        _settings.Recurring.FirstOrDefault(r => r.Id == id) ?? throw new KeyNotFoundException($"No recurring expense with id {id}.");

    // ---- Notifications --------------------------------------------------------

    public NotificationSettings Notifications => _settings.Notifications;

    public void UpdateNotifications(bool dailyReminder, TimeOnly dailyReminderTime, bool cardDueReminder,
        int cardDueDaysBefore, bool budgetAlerts)
    {
        if (cardDueDaysBefore is < 0 or > 30)
            throw new ArgumentException("Choose between 0 and 30 days before.", nameof(cardDueDaysBefore));

        lock (_sync)
        {
            var n = _settings.Notifications;
            var old = (n.DailyReminder, n.DailyReminderTime, n.CardDueReminder, n.CardDueDaysBefore, n.BudgetAlerts);
            (n.DailyReminder, n.DailyReminderTime, n.CardDueReminder, n.CardDueDaysBefore, n.BudgetAlerts) =
                (dailyReminder, dailyReminderTime, cardDueReminder, cardDueDaysBefore, budgetAlerts);
            SaveOrRollBack(() => (n.DailyReminder, n.DailyReminderTime, n.CardDueReminder, n.CardDueDaysBefore, n.BudgetAlerts) = old);
        }
    }

    /// <summary>
    /// Whether a budget's over-budget alert has been shown for <paramref name="month"/>.
    /// </summary>
    public bool WasBudgetAlertShown(Guid budgetId, YearMonth month) =>
        Locked(() => _settings.Notifications.BudgetAlertsSent.Contains(BudgetAlertKey(budgetId, month)));

    public void MarkDailyReminderShown(DateOnly day) => UpdateState(n => n.LastDailyReminder = day);

    public void MarkCardDueReminderShown(DateOnly dueDate) => UpdateState(n => n.LastCardDueReminder = dueDate);

    public static string BudgetAlertKey(Guid budgetId, YearMonth month) => $"{budgetId:N}:{month.Year:D4}-{month.Month:D2}";

    public void MarkBudgetAlertShown(Guid budgetId, YearMonth month) => UpdateState(n =>
    {
        n.BudgetAlertsSent.Add(BudgetAlertKey(budgetId, month));

        // Only the last few months matter.
        var keep = Enumerable.Range(0, 3).Select(i => month.AddMonths(-i)).Select(m => $":{m.Year:D4}-{m.Month:D2}").ToList();
        n.BudgetAlertsSent.RemoveAll(k => !keep.Any(k.EndsWith));
    });

    // State changes: saved, but a failed save isn't worth bothering anyone about.
    private void UpdateState(Action<NotificationSettings> change)
    {
        lock (_sync)
        {
            change(_settings.Notifications);
            try
            {
                Save();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private T Locked<T>(Func<T> read)
    {
        lock (_sync)
            return read();
    }

    // ---- Backups --------------------------------------------------------------

    public BackupSettings Backups => _settings.Backups;

    /// <summary>
    /// The folder backups go in: the one chosen, or "Backups" inside the data folder.
    /// </summary>
    public string BackupFolder => Locked(() =>
        string.IsNullOrWhiteSpace(_settings.Backups.Folder) ? Path.Combine(DataFolder, "Backups") : _settings.Backups.Folder);

    /// <summary>
    /// Whether to back up, and where. Safe to call from any thread.
    /// </summary>
    public (bool Enabled, string Folder) BackupOptions() => Locked(() => (_settings.Backups.Enabled, BackupFolder));

    /// <summary>
    /// Turns backups on or off and sets their folder (null for the default).
    /// </summary>
    public void UpdateBackups(bool enabled, string? folder)
    {
        if (!string.IsNullOrWhiteSpace(folder) && !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("Choose a full folder path, e.g. D:\\Backups.", nameof(folder));

        lock (_sync)
        {
            var b = _settings.Backups;
            var old = (b.Enabled, b.Folder);
            (b.Enabled, b.Folder) = (enabled, string.IsNullOrWhiteSpace(folder) ? null : folder.Trim());
            SaveOrRollBack(() => (b.Enabled, b.Folder) = old);
        }
    }

    // ---- Helpers ------------------------------------------------------------

    private static AppSettings CreateDefaultSettings() => new() { Categories = CategoryStyles.CreateDefaults() };

    private static string? ValidateName(string? name, Guid? ignoreId, int maxLength, string noun,
        IEnumerable<(Guid Id, string Name)> existing)
    {
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
            return "Enter a name.";

        if (trimmed.Length > maxLength)
            return $"Names can be at most {maxLength} characters.";

        var clash = existing.FirstOrDefault(e =>
            e.Id != ignoreId && string.Equals(e.Name, trimmed, StringComparison.CurrentCultureIgnoreCase));
        if (clash.Name is not null)
            return $"There's already a {noun} called \"{clash.Name}\".";

        return null;
    }

    private static void ThrowIfInvalid(string? error)
    {
        if (error is not null)
            throw new ArgumentException(error, "name");
    }

    // Callers hold _sync, so the change, the save and any rollback happen together.
    private void SaveOrRollBack(Action rollBack)
    {
        try
        {
            Save();
        }
        catch
        {
            rollBack();
            throw;
        }
    }

    private FamilyMember FindMember(Guid id) =>
        _settings.FamilyMembers.FirstOrDefault(m => m.Id == id)
        ?? throw new KeyNotFoundException($"No family member with id {id}.");

    private Category FindCategory(Guid id) =>
        _settings.Categories!.FirstOrDefault(c => c.Id == id)
        ?? throw new KeyNotFoundException($"No category with id {id}.");

    // Tidies up a file that was hand-edited or written by an older version:
    // drops blank and duplicate names, repairs missing or repeated ids, and
    // adds the default categories if the file predates them.
    private static void Normalize(AppSettings settings)
    {
        settings.FamilyMembers = CleanList(settings.FamilyMembers, m => m.Name, (m, n) => m.Name = n,
            m => m.Id, (m, id) => m.Id = id);

        if (settings.Categories is null)
        {
            settings.Categories = CategoryStyles.CreateDefaults();
        }
        else
        {
            settings.Categories = CleanList(settings.Categories, c => c.Name, (c, n) => c.Name = n,
                c => c.Id, (c, id) => c.Id = id);
            foreach (var category in settings.Categories)
            {
                category.Icon = string.IsNullOrWhiteSpace(category.Icon) ? CategoryStyles.DefaultIcon : category.Icon.Trim();
                if (!CategoryStyles.IsValidColor(category.Color))
                    category.Color = CategoryStyles.DefaultColor;
            }
        }

        // Budgets must point at something that exists, one per target.
        settings.Budgets = (settings.Budgets ?? new List<Budget>())
            .Where(b => b is not null && b.Amount > 0 && Enum.IsDefined(b.Target))
            .Where(b => b.Target switch
            {
                BudgetTarget.Everything => true,
                BudgetTarget.Category => settings.Categories!.Any(c => c.Id == b.TargetId),
                _ => settings.FamilyMembers.Any(m => m.Id == b.TargetId),
            })
            .GroupBy(b => (b.Target, b.Target == BudgetTarget.Everything ? Guid.Empty : b.TargetId))
            .Select(g => g.First())
            .ToList();

        settings.Recurring = (settings.Recurring ?? new List<RecurringExpense>())
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Name) && r.Amount > 0 && Enum.IsDefined(r.PaymentMethod))
            .GroupBy(r => r.Id)
            .Select(g => g.First())
            .ToList();
        foreach (var r in settings.Recurring)
        {
            r.DayOfMonth = Math.Clamp(r.DayOfMonth, 1, 31);
            r.CategoryName ??= string.Empty;
            r.MemberName ??= string.Empty;
        }

        settings.Backups ??= new BackupSettings();
        if (settings.Backups.Folder is { } folder && (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)))
            settings.Backups.Folder = null;

        settings.Notifications ??= new NotificationSettings();
        settings.Notifications.BudgetAlertsSent ??= new List<string>();
        settings.Notifications.CardDueDaysBefore = Math.Clamp(settings.Notifications.CardDueDaysBefore, 0, 30);

        settings.CreditCard ??= new CardSettings();
        var card = settings.CreditCard;
        if (string.IsNullOrWhiteSpace(card.Name))
            card.Name = "Credit card";
        if (card.Limit is < 0)
            card.Limit = null;
        if (card.DueDay is < 1 or > 31)
            card.DueDay = null;

        settings.Version = AppSettings.CurrentVersion;
    }

    private static List<T> CleanList<T>(List<T>? items, Func<T, string?> getName, Action<T, string> setName,
        Func<T, Guid> getId, Action<T, Guid> setId) where T : class
    {
        var seenIds = new HashSet<Guid>();
        var seenNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var cleaned = new List<T>();

        foreach (var item in items ?? new List<T>())
        {
            if (item is null)
                continue;

            var name = (getName(item) ?? string.Empty).Trim();
            setName(item, name);
            if (name.Length == 0 || !seenNames.Add(name))
                continue;

            if (getId(item) == Guid.Empty || !seenIds.Add(getId(item)))
            {
                setId(item, Guid.NewGuid());
                seenIds.Add(getId(item));
            }

            cleaned.Add(item);
        }

        return cleaned;
    }
}
