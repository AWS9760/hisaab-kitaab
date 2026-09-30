# Hisaab Kitaab

A personal and family expense tracker for Windows and Linux. It keeps every
month's accounts in a local Excel workbook (for example `Sept_2026.xlsx`), so
there's no cloud and no database. You can open the files in Excel or
LibreOffice at any time.

Built with C# / .NET 8, [Avalonia UI](https://avaloniaui.net/),
[FluentAvalonia](https://github.com/amwx/FluentAvalonia) (Windows 11 Fluent
look, light and dark themes), [ClosedXML](https://github.com/ClosedXML/ClosedXML)
and [LiveCharts2](https://livecharts.dev/).

> **Status:** early development. Dashboard, Settings, Expenses (with search and filters), Bank &
> Cash, Currency, Credit Card, Zakat, budgets, recurring expenses, reminders and backups work;
> all twelve stages of the plan are done (see *Roadmap* below).

## Where your data lives

| What | Windows | Linux |
|---|---|---|
| Settings (family members, …) | `%APPDATA%\HisaabKitaab\settings.json` | `~/.config/HisaabKitaab/settings.json` |
| Monthly workbooks | `Documents\Hisaab Kitaab\2026\Sept_2026.xlsx` | `~/Documents/Hisaab Kitaab/2026/Sept_2026.xlsx` |
| Zakat (one workbook per zakat year) | `Documents\Hisaab Kitaab\2026\Zakat_2026.xlsx` | `~/Documents/Hisaab Kitaab/2026/Zakat_2026.xlsx` |

Those are the defaults. To keep either somewhere else (another drive, or a
OneDrive / Google Drive folder), use **Settings → Where your data is kept**:

- **Workbooks → Change…**: pick a folder. If you already have workbooks, the
  app offers to **move** them there (with their backups, if the backup folder
  is the default one) or to start empty. All workbooks move or none do, so
  close them in Excel first. If the folder you pick already has Hisaab Kitaab
  workbooks (for example a synced folder from another computer), the app
  switches to those and leaves your current ones where they are.
- **Settings → Change…**: moves `settings.json` to the folder you pick. The app
  remembers the new place in a small `settings-location.txt` in the default
  settings folder, so it can find your settings at the next start. If that
  folder isn't available at startup (say, a drive that isn't plugged in), the
  app uses the settings in the default folder and tells you so.
- **Use default** puts either one back, moving the files again.

Changes take effect straight away; no restart is needed.

If the settings file ever gets corrupted, the app starts with default settings
and renames the bad file to `settings.corrupt-<date>.json` instead of deleting it.

**Keeping everything in one folder (portable mode / trying it out):** set the
`HISAAB_KITAAB_HOME` environment variable to a folder, and the app keeps its
settings and workbooks there instead (`settings.json` and `Workbooks/`),
leaving your real data untouched. In this mode the settings folder can't be
changed from the app (the workbook folder still can).

```powershell
$env:HISAAB_KITAAB_HOME = "D:\HisaabTest"; dotnet run --project HisaabKitaab
```

```bash
HISAAB_KITAAB_HOME=~/hisaab-test dotnet run --project HisaabKitaab
```

## Dashboard

The **Dashboard** opens first and shows, for the month you pick:

- **Income** (the "Income to bank" and "Income in cash" entries on Bank &
  Cash), **Spent** (every expense, however it was paid), **Saved** (the
  difference, and what share of income that is), and **Bank + cash** right now
  (with what's owed on the card).
- **Where the money went**: a doughnut chart and list by category, in each
  category's colour. Pick a family member above it to see just their spending.
- **Budgets**: how much of each monthly budget has been used (see *Budgets* below).
- **Income and spending over the last 6 months**, and **spending by family
  member**, as bar charts.

Each workbook also has a **Summary** sheet with the same figures (income,
spent, remaining, month-end balances, totals per family member and per
category, and budgets) as live Excel formulas. The app rebuilds it on every save, so edit
the other sheets rather than this one.

## Logging expenses

The **Expenses** screen is built for quick daily entry:

1. Type the amount. Sums work too, e.g. `250+180`.
2. Press **Enter**. The expense is saved and the cursor goes back to the amount
   box, ready for the next one.

Category, family member, payment method and date stay as you left them, so a
run of similar expenses is just amount, Enter, amount, Enter. Use the pencil
(or double-click a row) to edit, which fills the same form; **Esc** cancels.
The arrows at the top move between months.

Categories (with their emoji and colour) and family members are managed in
**Settings**.

### Searching and filtering

- **Search** (Ctrl+F) matches notes, categories, people, payment methods and
  amounts. Several words must all match, e.g. `jazz sara`.
- **Filters** narrow by date range, family members, categories, payment method
  and amount range, all combinable. The date range can span months; there are
  shortcuts for *This month*, *Last 30 days* and *This year*.
- The totals at the top always add up what's shown, e.g. "3 of 18 expenses".

Searching works on data held in memory. A workbook is only read again when its
file changes on disk, so edits made in Excel still show up.

## Bank & Cash

The **Bank & Cash** screen shows your bank balance and cash in hand. You never
re-enter expenses there:

- Expenses paid in **Cash** come out of cash in hand.
- Expenses paid by **Bank** come out of the bank balance.
- **Credit card** expenses touch neither until you repay the card (from the
  bank or in cash) on the Credit Card screen.
- **Zakat given** on the Zakat screen comes out of whichever it was paid from.

Log the money that moves *between* or *into* them:

| Entry | Bank | Cash |
|---|---|---|
| Withdrawal (e.g. ATM) | − | + |
| Deposit (cash paid in) | + | − |
| Income to bank (e.g. salary) | + | |
| Income in cash | | + |

Each month's **opening balance** is last month's closing balance. Click the ✎
next to it to type in your own figure (for example from a bank statement), or
to go back to carrying it forward. If cash in hand goes below zero, the screen
points out the day, since that usually means a withdrawal wasn't logged.

In the workbook, the *Bank & Cash* sheet uses real Excel formulas over the
transaction log and the Expenses sheet, so the balances stay correct if you
edit expenses directly in Excel.

## Credit Card

Expenses with Payment Method **Credit Card** are added to what you owe
automatically. On the **Credit Card** screen you log **repayments**, each
paid either from the bank or in cash. A repayment reduces what you owe *and*
comes off that balance on Bank & Cash.

- What's owed carries forward month to month. Click the ✎ to type the figure
  from your card statement instead.
- **Card details** (name, credit limit, and the day of the month the bill is
  due) are set once and apply to every month. The screen then shows how much of
  the limit you've used, what's still available, and when the next payment is
  due. It warns when a payment is due within 3 days, when you've used 80% of the
  limit, or when you're over it.
- **Pay in full** fills in the whole amount owed.

In the workbook, the *Credit Card* sheet has a formula-driven summary
(opening, card spending, repayments, outstanding, limit used, available, and
the due date for that month's bill), the repayment log, and alongside it a
copy of the month's card expenses from the Expenses sheet (change those on the
Expenses sheet; the copy is refreshed every time the app saves).

## Zakat

The **Zakat** screen keeps one **zakat year** at a time, and each year has its
own workbook, `Zakat_2026.xlsx`, next to that year's monthly files.

- **Pick the dates your zakat year runs over** (✎ next to the dates), for
  example Ramadan to Ramadan. Type the start date, then choose the end date or
  use **A full year** / **An Islamic year (354 days)**. A year must start in
  the year it's named after, be at most a year long, and not overlap the years
  either side. Until you save dates, the screen suggests the day after last
  year's zakat year ended (or 1 January to 31 December) and says so.
- Log zakat **Set aside** (worked out and put aside to give; moves no money)
  and zakat **Given** (to whom, and whether it was paid from the bank or in
  cash). Amounts accept sums and multiplication, e.g. `1200000*0.025`.
- The screen shows what's been **taken out** (carried in plus set aside this
  year), **given** (split into bank and cash) and what's **remaining** to give,
  with the remaining amount after each entry.
- Whatever's left **carries into the next zakat year**. Click ✎ next to
  "Carried from …" to type your own figure instead (e.g. zakat still owed from
  before you started using the app).
- Zakat given comes off the bank balance or cash in hand on **Bank & Cash**
  for the month it was given in, and shows up in that month's timeline.

The zakat workbook has the dates, a summary (carried in, set aside, total
taken out, given, remaining, given from bank and in cash) as Excel formulas,
and the log. Each monthly workbook's *Bank & Cash* sheet has a **− Zakat
given** row and, beside its transactions, a read-only copy of that month's
zakat payments, refreshed whenever the app saves the month. Change zakat on
the Zakat screen or in the zakat workbook, not in the copy. If a month's
workbook is open in Excel when you log zakat, the app says so and updates that
copy the next time it saves the month or starts; the app's own balances are
right either way.

## Budgets

In **Settings → Budgets**, set a monthly limit for **all spending**, for a
**category** or for a **family member** (one budget each; amounts accept sums
like `20000+5000`). A budget applies to every month until you change it, and
it follows renames. Removing a category or member removes its budget.

- The **Dashboard** shows a progress bar for each budget in the month you're
  viewing: amber from 80% used, red once it's over.
- After you add an expense, the Expenses screen warns you if it took one of
  its budgets past 80% or over the limit.
- The workbook's **Summary** sheet lists each budget with Excel formulas for
  what's been spent and the share used (as of the last time the app saved
  that month).

## Recurring expenses

In **Settings → Recurring expenses**, add rent, bills and subscriptions with
a name (written as the expense's note), amount, category, family member, how
they're paid and the day of the month (days past the end of a short month,
like the 31st, use its last day).

- On that day they're added as ordinary expenses, so they count everywhere
  (Bank & Cash, Credit Card, Dashboard, budgets). Anything missed while the app
  was closed is caught up the next time it starts.
- A new item whose day has already passed this month starts next month, unless
  you tick **Also add it for this month**.
- Each one is added once per month, even if you delete the expense it added
  (it won't come back). Switch one off to pause it; switched back on, it
  continues from its next day, skipping any it missed while paused.
- If that month's workbook is open in Excel, it's tried again a minute later.
- The Expenses screen tells you what was added automatically.

## Reminders

In **Settings → Notifications** you can turn on or off:

- a **daily reminder** to log expenses, at a time you choose, shown only if
  nothing has been added that day;
- a **credit card payment reminder**, a chosen number of days before the due
  day (set on the Credit Card screen), shown only if something is owed;
- a **budget exceeded** alert, the first time a budget goes over in a month.

They appear as desktop notifications while Hisaab Kitaab is open (including
minimised), each only once, even across restarts. **Send a test
notification** checks they work. On Windows they need notifications switched
on in *Settings → System → Notifications*; on Linux they use `notify-send`
(the `libnotify-bin` package on Debian/Ubuntu). If notifications can't be
shown, the Notifications section says why, and the in-app warnings above
still work.

## Backups

Every time the app saves a workbook (monthly or zakat) or your settings, it
also copies the file to a **backup folder**, by default `Backups` inside your
data folder:

```
Backups/2026/Sept_2026/Sept_2026 2026-09-29 14-05-12.xlsx
Backups/2026/Zakat_2026/Zakat_2026 2026-09-29 14-06-40.xlsx
Backups/Settings/settings 2026-09-29 14-07-03.json
```

- For each file it keeps the **newest 20 copies**, plus the **last copy of
  each day for 30 days**. Older copies are deleted automatically (only files
  named like backups; anything else you put in the folder is left alone).
- In **Settings → Backups** you can turn backups off, **Open** the folder, or
  **Change…** it. A folder on another drive, a USB stick or a cloud-synced
  folder (OneDrive, Google Drive) also protects you if this disk fails. Backups
  already made stay in the old folder.
- **Restore an earlier copy:** choose a workbook, then **Restore** next to the
  copy you want. The current file is backed up first (if it isn't already), so
  a restore can be undone by restoring that copy. Close the workbook in Excel
  first. Later months' carried-forward balances catch up automatically.
- If a backup fails (for example the backup drive is unplugged), saving still
  works and Settings shows what went wrong.
- Settings can't be restored from inside the app (it's using them). To go back
  to an earlier `settings.json`, close the app and copy the backup over it.

## Currency (cash count)

On the **Currency** screen, enter how many of each note you have (₨ 5,000,
1,000, 500, 100, 50, 20 and 10) plus any coins. **Tab** moves from one note to
the next. The total is compared with cash in hand from Bank & Cash **at the
end of the day you counted**, and the screen says whether it matches, or how
much more or less cash you have than your records show (differences under
₨ 1 count as a match).

Counts save automatically a moment after you stop typing, and again when you
switch screens or close the app. In the workbook, the *Currency
Denominations* sheet does the same comparison with Excel formulas.

### Editing the workbooks in Excel

You can open and edit the monthly files yourself. The app is built to cope:

- Rows stay sorted by date. The **Payment Method** column has a
  Cash / Bank / Credit Card dropdown.
- A hidden **ID** column lets the app find each expense again. Rows you add
  by hand get an ID the next time the app reads the file.
- You can reorder columns; they're found by their header names.
- Rows the app can't understand (for example a date like "someday" or a
  missing amount) are reported and left untouched, never deleted.
- Close a workbook in Excel before changing that month in the app. Otherwise
  the app reports that the file is in use and leaves it unchanged.
- Renaming a family member in Settings also updates their name in the
  **current year's** workbooks. Earlier years keep the name they were recorded with.
- Opening balances that carry forward (bank, cash, card) are rewritten in
  later months' workbooks whenever an earlier month changes, and again each
  time the app starts, so every sheet shows current figures in Excel. A
  workbook that's open in Excel at that moment is caught up next time. The
  amount carried into each zakat year is kept up to date the same way.
- Workbooks saved by this version (with zakat) can't be opened by older
  versions of Hisaab Kitaab, which would misread the new Bank & Cash layout;
  they ask you to update instead.

## Requirements

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer.
  Newer SDKs (9, 10) can build it too, but you still need the **.NET 8 runtime**
  to run it.
- **Windows:** Windows 10 or 11.
- **Linux:** an X11 or XWayland desktop. Avalonia also needs fontconfig and a
  few common libraries:

  ```bash
  # Debian / Ubuntu
  sudo apt install libfontconfig1 libice6 libsm6
  # Fedora
  sudo dnf install fontconfig libICE libSM
  ```

## Build and run

From the repository root (the folder containing `HisaabKitaab.sln`):

### Windows (PowerShell)

```powershell
dotnet restore
dotnet build
dotnet run --project HisaabKitaab
```

### Linux (bash)

```bash
dotnet restore
dotnet build
dotnet run --project HisaabKitaab
```

### Run the tests

```bash
dotnet test
```

## Publish a standalone app

These commands produce a self-contained build, so the target machine doesn't
need .NET installed.

### Windows

```powershell
dotnet publish HisaabKitaab -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```

Run `publish\win-x64\HisaabKitaab.exe`.

### Linux

```bash
dotnet publish HisaabKitaab -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64
chmod +x publish/linux-x64/HisaabKitaab
./publish/linux-x64/HisaabKitaab
```

To add it to your application menu, create
`~/.local/share/applications/hisaab-kitaab.desktop`:

```ini
[Desktop Entry]
Type=Application
Name=Hisaab Kitaab
Comment=Monthly family expense tracker
Exec=/path/to/publish/linux-x64/HisaabKitaab
Icon=/path/to/HisaabKitaab/Assets/hisaab-kitaab.png
Categories=Office;Finance;
```

## Project layout

```
HisaabKitaab.sln
HisaabKitaab/
├── Assets/        App icon (.ico for Windows, .png for Linux)
├── Models/        Plain data classes (expenses, members, settings, …)
├── Services/      ExcelService (all ClosedXML code), config, backups, notifications
├── Styles/        Shared XAML resources (navigation icons)
├── ViewModels/    CommunityToolkit.Mvvm view models, one per screen
├── Views/         Avalonia XAML views, one per screen
├── App.axaml      Theme setup (FluentAvalonia) and view locator registration
└── Program.cs     Entry point
HisaabKitaab.Tests/  xUnit tests for services and view models
```

## Roadmap

1. ✅ Project scaffold and navigation shell
2. ✅ Settings: family member management, saved to JSON
3. ✅ ExcelService: monthly workbook creation, Expenses sheet read/write
4. ✅ Expenses screen: add, edit, delete (plus categories in Settings)
5. ✅ Search and filter
6. ✅ Bank & Cash, with automatic deduction from expenses
7. ✅ Currency denominations
8. ✅ Credit card, with automatic linking from expenses
9. ✅ Dashboard with charts (and the Summary sheet)
10. ✅ Budgets, recurring expenses, notifications
11. ✅ Zakat (separate yearly file)
12. ✅ Backups (password protection was left out by choice)

---

## License

This project is licensed under the MIT License.

---

## Author

**Abdul Wali**

Computer Science Student | Software Developer

---

⭐ *If you find this project useful, consider giving it a star!*
