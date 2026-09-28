using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HisaabKitaab.Models;
using HisaabKitaab.Services;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace HisaabKitaab.ViewModels;

/// <summary>
/// One line of the category legend beside the doughnut chart.
/// </summary>
public record CategoryLegendRow(string Icon, string Name, string Color, string AmountText, string ShareText);

/// <summary>
/// "Everyone" (Value null) or one family member ("" for expenses with no member).
/// </summary>
public record MemberFilterOption(string? Value, string Label);

/// <summary>
/// One budget's progress bar on the Dashboard.
/// </summary>
public record BudgetRow(string Label, string Icon, string Color, double Percent, string AmountText, string PercentText,
    bool IsNear, bool IsOver);

/// <summary>
/// The Dashboard: a month's income, spending and savings, where the money
/// went (by category, overall or for one family member), the last six
/// months' trend, and spending per member.
/// </summary>
public partial class DashboardViewModel : PageViewModelBase
{
    private static readonly MemberFilterOption Everyone = new(null, "Everyone");

    // Neutral greys that read on both the light and dark themes.
    private static readonly SolidColorPaint AxisText = new(new SKColor(0x8A, 0x8F, 0x98));
    private static readonly SolidColorPaint GridLines = new(new SKColor(0x80, 0x80, 0x80, 0x30));
    private static readonly SKColor IncomeColor = SKColor.Parse("#0D9488");
    private static readonly SKColor SpentColor = SKColor.Parse("#F97316");

    private readonly DashboardService _service;
    private readonly SettingsService _settings;
    private readonly TimeProvider _clock;
    private int _loadVersion;
    private DashboardData? _data;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MonthTitle), nameof(IsCurrentMonth))]
    [NotifyCanExecuteChangedFor(nameof(GoToThisMonthCommand))]
    private YearMonth _month;

    [ObservableProperty]
    private string _incomeText = Pkr.Format(0);

    [ObservableProperty]
    private string _incomeNote = string.Empty;

    [ObservableProperty]
    private string _spentText = Pkr.Format(0);

    [ObservableProperty]
    private string _spentNote = string.Empty;

    [ObservableProperty]
    private string _savedText = Pkr.Format(0);

    [ObservableProperty]
    private string _savedNote = string.Empty;

    [ObservableProperty]
    private bool _isSavedNegative;

    [ObservableProperty]
    private string _moneyText = Pkr.Format(0);

    [ObservableProperty]
    private string _moneyNote = string.Empty;

    // ---- By category ----

    [ObservableProperty]
    private IReadOnlyList<MemberFilterOption> _memberOptions = new[] { Everyone };

    [ObservableProperty]
    private MemberFilterOption _selectedMember = Everyone;

    [ObservableProperty]
    private ISeries[] _categorySeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private IReadOnlyList<CategoryLegendRow> _categoryLegend = Array.Empty<CategoryLegendRow>();

    [ObservableProperty]
    private string _categoryTotalText = string.Empty;

    [ObservableProperty]
    private bool _hasCategoryData;

    // ---- Trend and members ----

    [ObservableProperty]
    private ISeries[] _trendSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] _trendXAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private Axis[] _trendYAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private bool _hasTrendData;

    [ObservableProperty]
    private ISeries[] _memberSeries = Array.Empty<ISeries>();

    [ObservableProperty]
    private Axis[] _memberXAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private Axis[] _memberYAxes = Array.Empty<Axis>();

    [ObservableProperty]
    private bool _hasMemberData;

    [ObservableProperty]
    private IReadOnlyList<BudgetRow> _budgetRows = Array.Empty<BudgetRow>();

    [ObservableProperty]
    private bool _hasBudgets;

    public DashboardViewModel(DashboardService service, SettingsService settings, TimeProvider? clock = null)
    {
        _service = service;
        _settings = settings;
        _clock = clock ?? TimeProvider.System;
        _month = YearMonth.Of(Today);

        _settings.CategoriesChanged += (_, _) => { if (_data is not null) { ShowCategories(); ShowBudgets(_data); } };
        _settings.BudgetsChanged += (_, _) => { if (_data is not null) ShowBudgets(_data); };
    }

    public override string Title => "Dashboard";

    public override string Description => "Where your money came from and where it went.";

    public string MonthTitle => Month.DisplayName;

    public bool IsCurrentMonth => Month == YearMonth.Of(Today);

    /// <summary>
    /// The data most recently shown.
    /// </summary>
    public DashboardData? Data => _data;

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    public override void OnNavigatedTo() => _ = LoadAsync();

    partial void OnSelectedMemberChanged(MemberFilterOption value)
    {
        if (_data is not null)
            ShowCategories();
    }

    // ---- Loading ------------------------------------------------------------

    public async Task LoadAsync()
    {
        var version = ++_loadVersion;
        var month = Month;
        var data = await Task.Run(() => _service.GetMonth(month));
        if (version != _loadVersion)
            return;

        _data = data;
        ShowTiles(data);
        ShowMemberOptions(data);
        ShowCategories();
        ShowTrend(data);
        ShowMembers(data);
        ShowBudgets(data);
    }

    private void ShowTiles(DashboardData d)
    {
        IncomeText = Pkr.Format(d.Income);
        IncomeNote = d.Income == 0 ? "Log salary and other income on Bank & Cash" : "Logged on Bank & Cash";

        SpentText = Pkr.Format(d.Spent);
        SpentNote = d.Expenses.Count switch
        {
            0 => "No expenses yet",
            1 => "1 expense",
            var n => $"{n.ToString("N0", CultureInfo.InvariantCulture)} expenses",
        };

        SavedText = Pkr.Format(d.Saved);
        IsSavedNegative = d.Saved < 0;
        SavedNote = d.SavingsRate switch
        {
            null => "No income logged",
            >= 0 and var r => $"{Percent(r)} of income",
            _ => "Spent more than came in",
        };

        MoneyText = Pkr.Format(d.BankBalance + d.CashInHand);
        MoneyNote = $"Bank {Pkr.Format(d.BankBalance)} · Cash {Pkr.Format(d.CashInHand)}" +
                    (d.CardOwed != 0 ? $" · Card owed {Pkr.Format(d.CardOwed)}" : string.Empty);
    }

    private void ShowMemberOptions(DashboardData d)
    {
        var previous = SelectedMember.Value;
        var options = new List<MemberFilterOption> { Everyone };
        options.AddRange(d.ByMember().Select(s => new MemberFilterOption(s.Name, s.Name.Length == 0 ? "(no member)" : s.Name)));
        MemberOptions = options;
        SelectedMember = options.FirstOrDefault(o =>
            o.Value is not null && string.Equals(o.Value, previous, StringComparison.CurrentCultureIgnoreCase)) ?? Everyone;
    }

    private void ShowCategories()
    {
        var shares = _data!.ByCategory(SelectedMember.Value);
        HasCategoryData = shares.Count > 0;
        var total = shares.Sum(s => s.Amount);
        CategoryTotalText = SelectedMember.Value is null
            ? $"{Pkr.Format(total)} spent"
            : $"{SelectedMember.Label} spent {Pkr.Format(total)}";

        var looks = shares.Select(s => (Share: s, Look: LookOf(s.Name))).ToList();
        CategoryLegend = looks.Select(x => new CategoryLegendRow(
                x.Look.Icon, x.Share.Name.Length == 0 ? "(no category)" : x.Share.Name, x.Look.Color,
                Pkr.Format(x.Share.Amount), Percent(x.Share.ShareOfTotal)))
            .ToList();

        CategorySeries = looks.Select(x => (ISeries)new PieSeries<double>
            {
                Values = new[] { (double)x.Share.Amount },
                Name = x.Share.Name.Length == 0 ? "(no category)" : x.Share.Name,
                Fill = new SolidColorPaint(SKColor.Parse(x.Look.Color)),
                InnerRadius = 70,
                HoverPushout = 6,
                ToolTipLabelFormatter = _ => $"{Pkr.Format(x.Share.Amount)} ({Percent(x.Share.ShareOfTotal)})",
            })
            .ToArray();
    }

    private void ShowTrend(DashboardData d)
    {
        HasTrendData = d.Trend.Any(t => t.Income != 0 || t.Spent != 0);
        TrendSeries = new ISeries[]
        {
            Column("Income", d.Trend.Select(t => (double)t.Income), IncomeColor),
            Column("Spent", d.Trend.Select(t => (double)t.Spent), SpentColor),
        };
        TrendXAxes = new[] { LabelAxis(d.Trend.Select(t => t.Month.FileName.Split('_')[0])) };
        TrendYAxes = new[] { AmountAxis() };
    }

    private void ShowMembers(DashboardData d)
    {
        var shares = d.ByMember();
        HasMemberData = shares.Count > 0;
        MemberSeries = new ISeries[] { Column("Spent", shares.Select(s => (double)s.Amount), SpentColor) };
        MemberXAxes = new[] { LabelAxis(shares.Select(s => s.Name.Length == 0 ? "(no member)" : s.Name)) };
        MemberYAxes = new[] { AmountAxis() };
    }

    private void ShowBudgets(DashboardData d)
    {
        var statuses = BudgetService.Calculate(_settings.ResolvedBudgets(), d.Expenses);
        HasBudgets = statuses.Count > 0;
        BudgetRows = statuses.Select(s =>
        {
            var (icon, color) = s.Budget.Target switch
            {
                BudgetTarget.Everything => (BudgetLook.EverythingIcon, BudgetLook.EverythingColor),
                BudgetTarget.Category => LookOf(s.Budget.Name),
                _ => (BudgetLook.MemberIcon, BudgetLook.MemberColor),
            };
            var left = s.Remaining >= 0 ? $"{Pkr.Format(s.Remaining)} left" : $"{Pkr.Format(-s.Remaining)} over";
            return new BudgetRow(s.Budget.Label, icon, color,
                (double)Math.Clamp(s.Share * 100, 0, 100),
                $"{Pkr.Format(s.Spent)} of {Pkr.Format(s.Budget.Amount)} · {left}",
                Percent(s.Share),
                s.Level == BudgetLevel.Near, s.Level == BudgetLevel.Over);
        }).ToList();
    }

    /// <summary>
    /// Reloads if already showing something, e.g. after recurring expenses were added.
    /// </summary>
    public void Refresh()
    {
        if (_data is not null)
            _ = LoadAsync();
    }

    private (string Icon, string Color) LookOf(string category) =>
        _settings.FindCategory(category) is { } c ? (c.Icon, c.Color) : (CategoryStyles.DefaultIcon, CategoryStyles.DefaultColor);

    private static ColumnSeries<double> Column(string name, IEnumerable<double> values, SKColor color) => new()
    {
        Name = name,
        Values = values.ToArray(),
        Fill = new SolidColorPaint(color),
        MaxBarWidth = 28,
        Rx = 4,
        Ry = 4,
        YToolTipLabelFormatter = point => Pkr.Format((decimal)point.Coordinate.PrimaryValue),
    };

    private static Axis LabelAxis(IEnumerable<string> labels) => new()
    {
        Labels = labels.ToArray(),
        LabelsPaint = AxisText,
        TextSize = 12,
    };

    private static Axis AmountAxis() => new()
    {
        Labeler = Compact,
        LabelsPaint = AxisText,
        SeparatorsPaint = GridLines,
        TextSize = 12,
        MinLimit = 0,
    };

    /// <summary>
    /// "69%" (no space, whatever the culture).
    /// </summary>
    public static string Percent(decimal share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// Short axis labels: 950, 12k, 1.5M.
    /// </summary>
    public static string Compact(double value) => Math.Abs(value) switch
    {
        >= 1_000_000 => (value / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (value / 1_000).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => value.ToString("0", CultureInfo.InvariantCulture),
    };

    // ---- Navigation -----------------------------------------------------------

    [RelayCommand]
    private Task PreviousMonthAsync() => ShowMonthAsync(Month.AddMonths(-1));

    [RelayCommand]
    private Task NextMonthAsync() => ShowMonthAsync(Month.AddMonths(1));

    private bool CanGoToThisMonth() => !IsCurrentMonth;

    [RelayCommand(CanExecute = nameof(CanGoToThisMonth))]
    private Task GoToThisMonthAsync() => ShowMonthAsync(YearMonth.Of(Today));

    private Task ShowMonthAsync(YearMonth month)
    {
        Month = month;
        return LoadAsync();
    }

    [RelayCommand]
    private Task ReloadAsync()
    {
        _service.Store.Invalidate(Month);
        return LoadAsync();
    }
}
