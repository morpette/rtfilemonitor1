using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace RtFileMonitor;

public partial class MainWindow
{
    private readonly ObservableCollection<SqliteActivity.Change> sqliteChanges = [];
    private string? sqlitePath;
    private int sqliteOpenGeneration;
    private CancellationTokenSource? sqliteRead;

    private async void OpenSqlite(object sender, RoutedEventArgs e)
    {
        string root = preferences.Activity ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
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
