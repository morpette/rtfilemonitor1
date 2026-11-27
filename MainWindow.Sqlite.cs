using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Shape = System.Windows.Shapes.Shape;

namespace RtFileMonitor;

public partial class MainWindow
{
    private readonly ObservableCollection<SqliteActivity.Change> sqliteChanges = [];
    private string? sqlitePath;
    private int sqliteOpenGeneration;
    private CancellationTokenSource? sqliteRead;
    private CancellationTokenSource? dashboardRead;
    private const string ActivityChoice = "Activity monitor";
    private int browserGeneration;
    private long browserOffset;

    private void UpdateDatabaseChoices()
    {
        string selected = DatabaseChoice.SelectedItem as string ?? ActivityChoice;
        string[] tracked = SettingsWindow.KnownDatabases.Where(name =>
            preferences.Databases?.Contains(name) == true).ToArray();
        string[] choices = [ActivityChoice, .. tracked];
        DatabaseChoice.ItemsSource = choices;
        DatabaseChoice.SelectedItem = choices.Contains(selected) ? selected : ActivityChoice;
        _ = MonitorDashboardDatabases(CodexRoot, tracked);
    }

    private async Task MonitorDashboardDatabases(string root, string[] tracked)
    {
        dashboardRead?.Cancel();
        dashboardRead = null;
        SqliteLight.BeginAnimation(Shape.FillProperty, null);
        SqliteLastActive.Text = tracked.Length == 0 ? "No databases selected" : "Waiting for database activity…";
        if (tracked.Length == 0)
            return;

        using var read = new CancellationTokenSource();
        dashboardRead = read;
        try
        {
            await Task.Run(() => SqliteActivity.WatchCommits(root, tracked, names => Dispatch(() =>
            {
                if (dashboardRead != read || read.IsCancellationRequested)
                    return;
                SqliteLastActive.Text = "Last active: " + string.Join(", ", names);
                SqliteLight.BeginAnimation(Shape.FillProperty, activityFlash);
            }), read.Token));
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            if (!read.IsCancellationRequested)
                SqliteLastActive.Text = "SQLite monitoring is unavailable.";
        }
        finally
        {
            if (dashboardRead == read)
                dashboardRead = null;
        }
    }

    private async void ChangeDatabaseView(object sender, SelectionChangedEventArgs e)
    {
        int generation = ++browserGeneration;
        BrowserTables.ItemsSource = null;
        BrowserRows.ItemsSource = null;
        BrowserRows.Columns.Clear();
        PreviousRows.IsEnabled = NextRows.IsEnabled = false;
        string? name = DatabaseChoice.SelectedItem as string;
        bool browse = name is not null && name != ActivityChoice;
        ActivityView.Visibility = browse ? Visibility.Collapsed : Visibility.Visible;
        DatabaseView.Visibility = browse ? Visibility.Visible : Visibility.Collapsed;
        if (!browse)
            return;
        string path = Path.Combine(CodexRoot, name!);
        BrowserStatus.Text = "Loading tables…";
        try
        {
            var tables = await Task.Run(() => SqliteActivity.TablesAndViews(path).ToArray());
            if (generation != browserGeneration)
                return;
            BrowserTables.ItemsSource = tables;
            BrowserStatus.Text = "No tables or views.";
            BrowserTables.SelectedIndex = tables.Length > 0 ? 0 : -1;
        }
        catch (Exception error) when (error is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (generation == browserGeneration)
                BrowserStatus.Text = $"{name}: {error.Message}";
        }
    }

    private void ChangeBrowserTable(object sender, SelectionChangedEventArgs e) => LoadDatabaseRows(0);
    private void PreviousDatabaseRows(object sender, RoutedEventArgs e) => LoadDatabaseRows(Math.Max(0, browserOffset - SqliteActivity.PageSize));
    private void NextDatabaseRows(object sender, RoutedEventArgs e) => LoadDatabaseRows(browserOffset + SqliteActivity.PageSize);
    private void RefreshDatabaseRows(object sender, RoutedEventArgs e) => LoadDatabaseRows(browserOffset);

    private async void LoadDatabaseRows(long offset)
    {
        if (BrowserTables.SelectedItem is not SqliteActivity.SchemaObject table || DatabaseChoice.SelectedItem is not string name)
            return;
        int generation = ++browserGeneration;
        browserOffset = offset;
        PreviousRows.IsEnabled = NextRows.IsEnabled = false;
        BrowserRows.ItemsSource = null;
        BrowserRows.Columns.Clear();
        foreach (var column in table.Columns)
        {
            int index = BrowserRows.Columns.Count;
            BrowserRows.Columns.Add(new DataGridTextColumn { Header = column.Name,
                Binding = new System.Windows.Data.Binding($"[{index}]") { Mode = System.Windows.Data.BindingMode.OneWay } });
        }
        if (table.Error is not null || table.Columns.Count == 0)
        {
            BrowserStatus.Text = table.Error ?? "No columns available.";
            return;
        }
        BrowserStatus.Text = "Loading rows…";
        string path = Path.Combine(CodexRoot, name);
        try
        {
            var page = await Task.Run(() => SqliteActivity.ReadPage(path, table, offset));
            if (generation != browserGeneration)
                return;
            BrowserRows.ItemsSource = page.Rows;
            PreviousRows.IsEnabled = offset > 0;
            NextRows.IsEnabled = page.HasMore;
            BrowserStatus.Text = page.Rows.Count == 0 ? "No rows." : $"Rows {offset + 1:N0}–{offset + page.Rows.Count:N0}";
        }
        catch (Exception error) when (error is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (generation == browserGeneration)
                BrowserStatus.Text = error.Message;
        }
    }

    private async void OpenSqlite(object sender, RoutedEventArgs e)
    {
        string root = CodexRoot;
        var picker = new OpenFileDialog
        {
            Title = "Open a SQLite database",
            Filter = "SQLite databases|*.sqlite;*.sqlite3;*.db|All files|*.*",
            InitialDirectory = Directory.Exists(root) ? root : ""
        };
        if (picker.ShowDialog(this) != true)
            return;

        int generation = ++sqliteOpenGeneration;
        string path = picker.FileName;
        sqliteRead?.Cancel();
        sqliteChanges.Clear();
        SqliteTables.IsEnabled = false;
        SqliteTables.ItemsSource = null;
        sqlitePath = path;
        SqlitePath.Text = Path.GetFileName(path);
        SqlitePath.ToolTip = path;
        SqliteStatus.Text = "Loading tables…";
        try
        {
            string[] tables = await Task.Run(() => SqliteActivity.Tables(path));
            if (generation != sqliteOpenGeneration)
                return;
            SqliteTables.ItemsSource = tables;
            SqliteTables.IsEnabled = tables.Length > 0;
            SqliteStatus.Text = tables.Length > 0 ? "Select a table to start monitoring." : "No supported tables found.";
        }
        catch (Exception error) when (error is IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (generation == sqliteOpenGeneration)
                SqliteStatus.Text = error.Message;
        }
    }

    private async void SelectSqliteTable(object sender, SelectionChangedEventArgs e)
    {
        sqliteRead?.Cancel();
        sqliteChanges.Clear();
        if (sqlitePath is not { } path || SqliteTables.SelectedItem is not string table)
            return;

        using var read = new CancellationTokenSource();
        sqliteRead = read;
        SqliteActivity.Snapshot? previous = null;
        SqliteStatus.Text = "Taking the first snapshot…";
        try
        {
            while (!read.IsCancellationRequested)
            {
                try
                {
                    var result = await Task.Run(() =>
                    {
                        var current = SqliteActivity.Read(path, table, read.Token);
                        var change = previous is not null && previous.Schema == current.Schema
                            ? SqliteActivity.Compare(table, previous, current) : null;
                        return (Current: current, Change: change);
                    }, read.Token);
                    read.Token.ThrowIfCancellationRequested();
                    if (result.Change is { } change && (change.Added > 0 || change.Updated > 0 || change.Removed > 0))
                    {
                        sqliteChanges.Insert(0, change);
                        if (sqliteChanges.Count > 250)
                            sqliteChanges.RemoveAt(sqliteChanges.Count - 1);
                    }
                    bool schemaChanged = previous is not null && previous.Schema != result.Current.Schema;
                    previous = result.Current;
                    SqliteStatus.Text = schemaChanged ? "Table schema changed. A new baseline was recorded."
                        : result.Change is { Updated: null } || previous.Keyed is null
                        ? "Monitoring read-only. Without a usable primary key, edits appear as added and removed rows."
                        : "Monitoring read-only. Waiting for committed changes.";
                }
                catch (IOException error)
                {
                    read.Token.ThrowIfCancellationRequested();
                    SqliteStatus.Text = error.Message;
                }
                await Task.Delay(TimeSpan.FromSeconds(2), read.Token);
            }
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            if (!read.IsCancellationRequested)
                SqliteStatus.Text = error.Message;
        }
        finally
        {
            if (ReferenceEquals(sqliteRead, read))
                sqliteRead = null;
        }
    }
}
