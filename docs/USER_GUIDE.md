<<<<<<<< HEAD:docs/USER_GUIDE.md
# Hisaab Kitaab: user guide

How each screen works, where your data is kept, and how the Excel workbooks are laid out.
For an overview and build instructions, see the [README](../README.md).
========
<div align="center">

<img src="Assets/hisaab-kitaab.png" alt="Hisaab Kitaab logo" width="96" />

# Hisaab Kitaab

**A family expense tracker for Windows and Linux that keeps every rupee in plain Excel files on your own computer.**

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)
![Avalonia UI](https://img.shields.io/badge/Avalonia-11.3-8B44AC)
![Platforms](https://img.shields.io/badge/platforms-Windows%20%7C%20Linux-2EA44F)
![Data](https://img.shields.io/badge/data-local%20Excel%20files-217346?logo=microsoftexcel&logoColor=white)

<img src="docs/screenshots/dashboard.png" alt="The Dashboard: income, spending, savings, a spending-by-category chart and budgets" width="860" />
>>>>>>>> 626042180b5ae321668f4ebb3cf2af3de7700142:README.md

</div>

*Hisaab kitaab* (حساب کتاب) is Urdu for "keeping accounts". The app tracks a
household's expenses, bank balance, cash in hand, credit card and zakat in
Pakistani rupees (₨). Every month is an ordinary `.xlsx` workbook that you can
open in Excel or LibreOffice whenever you like. There's no account, no cloud
service and no database.

## Features

**Daily use**
- **Quick expense entry.** Type an amount (sums like `250+180` work), press
  Enter, and you're ready for the next one. Category, family member, payment
  method and date stay as you left them.
- **Search and filters.** Search notes, categories, people and amounts, and
  combine filters by date range, member, category, payment method and amount.
- **Family members and categories** with their own emoji and colour, all
  managed in Settings.

**Money, worked out for you**
- **Bank & Cash.** Cash expenses come out of cash in hand, bank expenses out of
  the bank balance. You log only withdrawals, deposits and income; balances
  carry from month to month.
- **Credit card.** Card spending adds to what you owe and repayments come off
  your bank or cash. Shows your limit, how much of it you've used, and when
  the bill is due.
- **Cash count.** Count your notes (₨ 5,000 down to ₨ 10) and coins to check
  that the cash in your wallet matches your records.
- **Zakat.** One file per zakat year, over dates you choose (for example
  Ramadan to Ramadan). Log zakat set aside and given; what's left carries into
  next year, and zakat given comes off your bank or cash.

**Planning**
- **Dashboard.** Income, spending, savings, where the money went (overall or
  per person), a 6-month trend and a per-member comparison.
- **Budgets** for all spending, a category or a person, with warnings when one
  is nearly used up or over.
- **Recurring expenses** (rent, bills, subscriptions) added automatically on
  their day, including months missed while the app was closed.
- **Reminders.** Desktop notifications to log the day's expenses, before the
  card bill is due, and when a budget goes over.

**Your data stays yours**
- **Plain Excel workbooks**, one per month, with live formulas (balances,
  totals, budgets) that stay right even if you edit the file in Excel.
- **Automatic backups** of every file on every save, with one-click restore.
- **Choose where files live**, for example a OneDrive or Google Drive folder
  to keep a copy off your computer.

<<<<<<<< HEAD:docs/USER_GUIDE.md
```powershell
$env:HISAAB_KITAAB_HOME = "D:\HisaabTest"; dotnet run
```

```bash
HISAAB_KITAAB_HOME=~/hisaab-test dotnet run
```
========
## Screenshots

| | |
|---|---|
| <img src="docs/screenshots/expenses.png" alt="Expenses screen with the quick-add form and the month's expenses" /> **Expenses**: quick entry, search and filters | <img src="docs/screenshots/bank-cash.png" alt="Bank & Cash screen with balances and a timeline of money in and out" /> **Bank & Cash**: balances worked out from everything you log |
| <img src="docs/screenshots/credit-card.png" alt="Credit Card screen with the outstanding amount, limit used and due date" /> **Credit Card**: what's owed, limit used and the due date | <img src="docs/screenshots/zakat.png" alt="Zakat screen with remaining, taken out and given totals and a log" /> **Zakat**: set aside, given and remaining for your zakat year |
| <img src="docs/screenshots/dashboard-charts.png" alt="Dashboard budgets and bar charts of the last six months and spending per family member" /> **Budgets and trends** | <img src="docs/screenshots/settings.png" alt="Settings with budgets and recurring expenses" /> **Settings**: budgets and recurring expenses |
>>>>>>>> 626042180b5ae321668f4ebb3cf2af3de7700142:README.md

## Getting started

### Requirements

<<<<<<<< HEAD:docs/USER_GUIDE.md
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

## Editing the workbooks in Excel

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

========
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build
  (newer SDKs work too, but you need the .NET 8 runtime to run it).
- **Windows** 10 or 11, or **Linux** with an X11 or XWayland desktop. On Linux,
  Avalonia also needs fontconfig and a few common libraries:

  ```bash
  sudo apt install libfontconfig1 libice6 libsm6     # Debian / Ubuntu
  sudo dnf install fontconfig libICE libSM           # Fedora
  ```

### Run from source

```bash
git clone https://github.com/AWS9760/hisaab-kitaab.git
cd hisaab-kitaab
dotnet run
```

### Build a standalone app

These produce a single self-contained executable, so the computer you run it
on doesn't need .NET installed.

```powershell
# Windows: then run publish\win-x64\HisaabKitaab.exe
dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```

```bash
# Linux
dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o publish/linux-x64
chmod +x publish/linux-x64/HisaabKitaab && ./publish/linux-x64/HisaabKitaab
```

To add it to your Linux application menu, create
`~/.local/share/applications/hisaab-kitaab.desktop`:

```ini
[Desktop Entry]
Type=Application
Name=Hisaab Kitaab
Comment=Family expense tracker
Exec=/path/to/publish/linux-x64/HisaabKitaab
Icon=/path/to/hisaab-kitaab/Assets/hisaab-kitaab.png
Categories=Office;Finance;
```

### Try it without touching your real data

Set `HISAAB_KITAAB_HOME` to any folder and the app keeps its settings and
workbooks there instead:

```powershell
$env:HISAAB_KITAAB_HOME = "D:\HisaabTest"; dotnet run
```

```bash
HISAAB_KITAAB_HOME=~/hisaab-test dotnet run
```

## Where your data is kept

| What | Default location (Windows) | Default location (Linux) |
|---|---|---|
| Monthly workbooks | `Documents\Hisaab Kitaab\2026\Sept_2026.xlsx` | `~/Documents/Hisaab Kitaab/2026/Sept_2026.xlsx` |
| Zakat workbooks | `Documents\Hisaab Kitaab\2026\Zakat_2026.xlsx` | `~/Documents/Hisaab Kitaab/2026/Zakat_2026.xlsx` |
| Backups | `Documents\Hisaab Kitaab\Backups\` | `~/Documents/Hisaab Kitaab/Backups/` |
| Settings | `%APPDATA%\HisaabKitaab\settings.json` | `~/.config/HisaabKitaab/settings.json` |

You can move any of these from **Settings**. Each monthly workbook has five
sheets: **Expenses**, **Summary**, **Bank & Cash**, **Currency Denominations**
and **Credit Card**. You can edit them in Excel: rows are matched by a hidden
ID column, columns are found by their headers, and rows the app can't read are
reported rather than changed or deleted.

The **[user guide](docs/USER_GUIDE.md)** explains every screen and the
workbook layout in detail.

## Built with

- [C# / .NET 8](https://dotnet.microsoft.com/)
- [Avalonia UI](https://avaloniaui.net/) with
  [FluentAvalonia](https://github.com/amwx/FluentAvalonia) (light and dark themes)
- [ClosedXML](https://github.com/ClosedXML/ClosedXML) for reading and writing Excel files
- [LiveCharts2](https://livecharts.dev/) for the charts
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) for the MVVM plumbing

## Project structure

```
Assets/       App icon (.ico for Windows, .png for Linux)
Models/       Plain data types: expenses, entries, budgets, zakat, settings
Services/     Balances, budgets, recurring expenses, reminders, backups, settings
  Excel/      One class per worksheet; the only code that touches ClosedXML
Styles/       Shared styles (cards, DataGrid fixes, icons)
ViewModels/   One view model per screen (CommunityToolkit.Mvvm)
Views/        Avalonia XAML views, one per screen (Settings/ holds its sections)
docs/         User guide and screenshots
```

Balances are never stored as data: the app works them out from your expenses
and entries every time, and the workbooks hold Excel formulas that do the same
sums, so both always agree.
>>>>>>>> 626042180b5ae321668f4ebb3cf2af3de7700142:README.md
