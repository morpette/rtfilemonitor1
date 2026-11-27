using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace RtFileMonitor;

public partial class SettingsWindow : Window
{
    private readonly Func<string> getCodexRoot;

    internal static readonly string[] KnownDatabases =
    [
        ".codex-global-state.sqlite",
        "goals_1.sqlite",
        "logs_2.sqlite",
        "memories_1.sqlite",
        "queue_1.sqlite",
        "state_5.sqlite",
        "thread_history_1.sqlite",
    ];

    private IEnumerable<CheckBox> DatabaseBoxes => DatabaseChecks.Children.OfType<CheckBox>();
    private bool AllDatabasesSelected => DatabaseBoxes.All(box => box.IsChecked == true);

    internal string[] SelectedDatabases => DatabaseBoxes
        .Where(box => box.IsChecked == true).Select(box => (string)box.Tag).ToArray();

    public SettingsWindow(string codexRoot, string[]? selected = null) : this(() => codexRoot, selected) { }

    internal SettingsWindow(Func<string> getCodexRoot, string[]? selected)
    {
        this.getCodexRoot = getCodexRoot;
        InitializeComponent();
        foreach (string name in KnownDatabases)
            DatabaseChecks.Children.Add(new CheckBox
            {
                Content = new TextBlock { Text = name }, Tag = name, IsChecked = selected?.Contains(name) == true
            });
        UpdateDatabaseToggleLabel();
    }

    private void ToggleDatabases(object sender, RoutedEventArgs e)
    {
        bool select = !AllDatabasesSelected;
        foreach (var box in DatabaseBoxes)
            box.IsChecked = select;
    }

    private void UpdateDatabaseToggle(object sender, RoutedEventArgs e) => UpdateDatabaseToggleLabel();

    private void UpdateDatabaseToggleLabel() =>
        ToggleDatabasesButton.Content = AllDatabasesSelected ? "Deselect all" : "Select all";

    private async void MapDatabases(object sender, RoutedEventArgs e)
    {
        MapDatabasesButton.IsEnabled = false;
        MapStatus.Text = "Mapping databases…";
        string codexRoot = getCodexRoot();
        string destination = Path.Combine(AppContext.BaseDirectory, "database-map.md");
        try
        {
            int count = await Task.Run(() => DatabaseMap.Write(codexRoot, destination));
            MapStatus.Text = $"Found {count} {(count == 1 ? "database" : "databases")}. Saved to {destination}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            MapStatus.Text = "Could not save the database map. " + error.Message;
        }
        finally
        {
            MapDatabasesButton.IsEnabled = true;
        }
    }
}
