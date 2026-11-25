using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RtFileMonitor;

public partial class MainWindow : Window
{
    private sealed record Preferences(string? Activity = null, string? Sessions = null);
    private sealed record ActivityRow(string Name, string Folder, DateTime Modified, long Size);

    private sealed class Session(string path, string root)
    {
        public string Path { get; } = path;
        public string RelativePath { get; } = System.IO.Path.GetRelativePath(root, path);
        public string DisplayName { get; } = FormatTimestamp(path) ?? System.IO.Path.GetFileName(path);
        public SessionLog Log { get; } = new();
        public ObservableCollection<TimelineEntry> Entries { get; } = [];
        public bool LoadOnSelection { get; init; }
        public bool Dirty { get; set; } = true;
        public bool HasPreview { get; set; }
        public string? Error { get; set; }

        private static string? FormatTimestamp(string path)
        {
            const string prefix = "rollout-";
            const string format = "yyyy-MM-dd'T'HH-mm-ss";
            const int timestampLength = 19;
            string name = System.IO.Path.GetFileNameWithoutExtension(path);
            int timestampEnd = prefix.Length + timestampLength;
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || name.Length < timestampEnd ||
                (name.Length > timestampEnd && name[timestampEnd] != '-'))
                return null;

            return DateTime.TryParseExact(name.AsSpan(prefix.Length, timestampLength), format,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
                ? timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                : null;
        }
    }

    private readonly string settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private readonly ObservableCollection<ActivityRow> activity = [];
    private readonly ObservableCollection<Session> sessions = [];
    private readonly Dictionary<string, Session> sessionsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer sessionTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private Preferences preferences = new();
    private FolderMonitor? activityMonitor;
    private FolderMonitor? sessionMonitor;
    private ListCollectionView? timelineView;
    private int activityGeneration;
    private int sessionGeneration;
    private bool readingSessions;

    public MainWindow()
    {
        InitializeComponent();
        string? version = typeof(MainWindow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "PackageVersionLabel")?.Value;
        if (!string.IsNullOrEmpty(version))
            AppHeading.Text += " " + version;
        ActivityGrid.ItemsSource = activity;
        SessionList.ItemsSource = sessions;
        CategoryBox.ItemsSource = new[]
        {
            "All", "Conversation", "Session", "Tools", "Usage", "Configuration", "Other", "Invalid JSON"
        };
        CategoryBox.SelectedIndex = 0;
        try
        {
            if (File.Exists(settingsPath))
                preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(settingsPath)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            SettingsStatus.Text = "Could not read settings: " + error.Message;
        }
        StartActivity();
        StartSessions();
        sessionTimer.Tick += ReadSessions;
        sessionTimer.Start();
    }

    private void ChooseActivity(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose the .Codex folder") is { } path)
            SetActivityFolder(path);
    }

    private void ChooseDefaultActivity(object sender, RoutedEventArgs e)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0)
        {
            ActivityStatus.Text = "Could not find your home folder.";
            return;
        }
        SetActivityFolder(Path.Combine(home, ".codex"));
    }

    private void SetActivityFolder(string path)
    {
        preferences = preferences with { Activity = path };
        SavePreferences();
        StartActivity();
    }

    private void ChooseSessions(object sender, RoutedEventArgs e)
    {
        if (Pick("Choose the Codex JSONL sessions folder") is { } path)
            SetSessionsFolder(path);
    }

    private void ChooseDefaultSessions(object sender, RoutedEventArgs e)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0)
        {
            SessionStatus.Text = "Could not find your home folder.";
            return;
        }
        SetSessionsFolder(Path.Combine(home, ".codex", "sessions"));
    }

    private void SetSessionsFolder(string path)
    {
        preferences = preferences with { Sessions = path };
        SavePreferences();
        StartSessions();
    }

    private string? Pick(string title)
    {
        var picker = new OpenFolderDialog { Title = title };
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home))
            picker.InitialDirectory = home;
        return picker.ShowDialog(this) == true ? picker.FolderName : null;
    }

    private void SavePreferences()
    {
        try
        {
            string temporary = settingsPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(preferences));
            File.Move(temporary, settingsPath, overwrite: true);
            SettingsStatus.Text = "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SettingsStatus.Text = "Could not save settings: " + error.Message;
        }
    }

    private void StartActivity()
    {
        int generation = ++activityGeneration;
        activityMonitor?.Dispose();
        activityMonitor = null;
        activity.Clear();
        ActivityStatus.Text = "";
        ActivityFolder.Text = preferences.Activity ?? "No folder selected";
        ActivityEmpty.Text = preferences.Activity is null
            ? "Choose a folder to see file updates here."
            : "Waiting for file updates…";
        ActivityEmpty.Visibility = Visibility.Visible;
        if (preferences.Activity is not { Length: > 0 } root)
            return;

        activityMonitor = new FolderMonitor(root, (path, stamp, _) =>
        {
            if (stamp is not { } file)
                return;
            Dispatch(() =>
            {
                if (generation != activityGeneration)
                    return;
                var folder = Path.GetDirectoryName(path) ?? root;
                string relativeFolder = Path.GetRelativePath(root, folder);
                activity.Insert(0, new ActivityRow(Path.GetFileName(path), relativeFolder == "." ? "root" : relativeFolder,
                    file.ModifiedUtc.ToLocalTime(), file.Size));
                ActivityEmpty.Visibility = Visibility.Collapsed;
            });
        }, error => Dispatch(() =>
        {
            if (generation == activityGeneration)
                ActivityStatus.Text = error ?? "";
        }));
        activityMonitor.Start();
    }

    private void StartSessions()
    {
        int generation = ++sessionGeneration;
        sessionMonitor?.Dispose();
        sessionMonitor = null;
        sessions.Clear();
        sessionsByPath.Clear();
        SessionStatus.Text = "";
        PreviousSessionsStatus.Text = "";
        LoadPreviousButton.IsEnabled = true;
        SessionFolder.Text = preferences.Sessions ?? "No folder selected";
        UpdateTimelineStatus();
        if (preferences.Sessions is not { Length: > 0 } root)
            return;

        sessionMonitor = new FolderMonitor(root, (path, stamp, isNew) => Dispatch(() =>
        {
            if (generation != sessionGeneration)
                return;
            if (sessionsByPath.TryGetValue(path, out var session))
            {
                session.Dirty = stamp is not null;
                session.Error = stamp is null ? "The session file is unavailable." : null;
                UpdateTimelineStatus();
            }
            else if (stamp is not null && isNew)
                AddSession(new Session(path, root));
        }), error => Dispatch(() =>
        {
            if (generation == sessionGeneration)
                SessionStatus.Text = error ?? "";
        }), sessionsOnly: true);
        sessionMonitor.Start();
    }

    private async void LoadPreviousSessions(object sender, RoutedEventArgs e)
    {
        if (preferences.Sessions is not { Length: > 0 } root)
        {
            PreviousSessionsStatus.Text = "Choose a Codex JSONL sessions folder first.";
            return;
        }

        int generation = sessionGeneration;
        LoadPreviousButton.IsEnabled = false;
        PreviousSessionsStatus.Text = "Looking for previous sessions…";
        try
        {
            string[] paths = await Task.Run(() => FolderMonitor.EnumerateFiles(root, "*.jsonl")
                .OrderDescending(StringComparer.OrdinalIgnoreCase).ToArray());
            if (generation != sessionGeneration)
                return;

            int added = 0;
            foreach (string path in paths)
            {
                if (sessionsByPath.ContainsKey(path))
                    continue;
                AddSession(new Session(path, root) { LoadOnSelection = true });
                added++;
            }
            PreviousSessionsStatus.Text = added == 0
                ? "No additional sessions found."
                : $"Added {added:N0} {(added == 1 ? "session" : "sessions")}.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (generation == sessionGeneration)
                PreviousSessionsStatus.Text = "Could not load previous sessions. " + error.Message;
        }
        finally
        {
            if (generation == sessionGeneration)
                LoadPreviousButton.IsEnabled = true;
        }
    }

    private void AddSession(Session session)
    {
        sessionsByPath.Add(session.Path, session);
        sessions.Add(session);
        SessionList.SelectedItem ??= session;
    }

    private void Dispatch(Action action) => Dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    private async void ReadSessions(object? sender, EventArgs e)
    {
        if (readingSessions)
            return;
        readingSessions = true;
        int generation = sessionGeneration;
        try
        {
            foreach (Session session in sessions.Where(item => item.Dirty &&
                (!item.LoadOnSelection || item == SessionList.SelectedItem)).ToArray())
            {
                if (generation != sessionGeneration)
                    break;
                session.Dirty = false;
                try
                {
                    var received = await Task.Run(() => session.Log.ReadNew(session.Path));
                    if (generation != sessionGeneration)
                        break;
                    if (session.Log.NeedsReset)
                    {
                        session.Entries.Clear();
                        session.Log.ResetAcknowledged();
                    }
                    else if (session.HasPreview)
                        session.Entries.RemoveAt(session.Entries.Count - 1);
                    foreach (var entry in received)
                        session.Entries.Add(entry);
                    session.HasPreview = session.Log.Preview is not null;
                    if (session.Log.Preview is { } preview)
                        session.Entries.Add(preview);
                    session.Error = null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    session.Log.RestartRead();
                    session.Error = error.Message;
                    session.Dirty = error is not (FileNotFoundException or DirectoryNotFoundException);
                }
            }
            UpdateTimelineStatus();
        }
        finally
        {
            readingSessions = false;
        }
    }

    private void SelectSession(object sender, SelectionChangedEventArgs e)
    {
        timelineView = SessionList.SelectedItem is Session session
            ? new ListCollectionView(session.Entries) { Filter = Matches }
            : null;
        Timeline.ItemsSource = timelineView;
        UpdateTimelineStatus();
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        timelineView?.Refresh();
        if (TimelineStatus is not null)
            UpdateTimelineStatus();
    }

    private bool Matches(object item)
    {
        var entry = (TimelineEntry)item;
        string search = SearchBox.Text.Trim();
        return (CategoryBox.SelectedItem is not string category || category == "All" || entry.Category == category)
            && (search.Length == 0 || entry.Raw.Contains(search, StringComparison.OrdinalIgnoreCase)
                || entry.Title.Contains(search, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateTimelineStatus()
    {
        TimelineStatus.Text = SessionList.SelectedItem is Session session
            ? session.Error ?? $"{timelineView?.Count ?? 0:N0} of {session.Entries.Count:N0} records"
            : "New JSONL files will appear here after monitoring starts.";
    }

    protected override void OnClosed(EventArgs e)
    {
        sessionTimer.Stop();
        activityGeneration++;
        sessionGeneration++;
        activityMonitor?.Dispose();
        sessionMonitor?.Dispose();
        base.OnClosed(e);
    }
}
