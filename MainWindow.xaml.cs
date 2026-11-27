using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Shape = System.Windows.Shapes.Shape;

namespace RtFileMonitor;

public partial class MainWindow : Window
{
    private void OpenSettings(object sender, RoutedEventArgs e)
    {
        var settings = CreateSettingsWindow();
        settings.ShowDialog();
        if (!(preferences.Databases ?? []).SequenceEqual(settings.SelectedDatabases))
        {
            preferences = preferences with { Databases = settings.SelectedDatabases };
            SavePreferences();
            UpdateDatabaseChoices();
        }
    }

    private SettingsWindow CreateSettingsWindow()
    {
        var settings = new SettingsWindow(() => CodexRoot, preferences.Databases)
        {
            Owner = this,
            DataContext = new { ActivityFolder, SessionFolder, ActivityStatus, SessionStatus, SettingsStatus }
        };
        settings.ChooseActivityButton.Click += ChooseActivity;
        settings.DefaultActivityButton.Click += ChooseDefaultActivity;
        settings.ChooseSessionsButton.Click += ChooseSessions;
        settings.DefaultSessionsButton.Click += ChooseDefaultSessions;
        settings.TimelineOrder.SelectedIndex = preferences.TimelineOldestFirst ? 1 : 0;
        settings.TimelineOrder.SelectionChanged += (_, _) =>
        {
            preferences = preferences with { TimelineOldestFirst = settings.TimelineOrder.SelectedIndex == 1 };
            SavePreferences();
            ApplyTimelineOrder();
        };
        return settings;
    }

    private string CodexRoot => preferences.Activity ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private sealed record Preferences(string? Activity = null, string? Sessions = null, string[]? Databases = null,
        bool TimelineOldestFirst = false);
    private sealed class ActivityRow(string name, string folder, FileStamp stamp) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string Folder { get; } = folder;
        public DateTime Modified => stamp.ModifiedUtc.ToLocalTime();
        public decimal SizeMB => stamp.Size / 1_000_000m;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(FileStamp latest)
        {
            stamp = latest;
            PropertyChanged?.Invoke(this, new(nameof(Modified)));
            PropertyChanged?.Invoke(this, new(nameof(SizeMB)));
        }
    }

    private sealed record TypeFilterOption(string? Value)
    {
        public override string ToString() => Value ?? "All types";
    }

    private sealed class Session
    {
        public string Path { get; }
        public string RelativePath { get; }
        public string DisplayName { get; }
        public DateTime Timestamp { get; }
        public SessionLog Log { get; } = new();
        public ObservableCollection<TimelineEntry> Entries { get; } = [];
        public HashSet<string> CommittedTypes { get; } = new(StringComparer.Ordinal);
        public bool LoadOnSelection { get; init; }
        public bool Dirty { get; set; } = true;
        public bool HasPreview { get; set; }
        public string? Error { get; set; }

        public Session(string path, string root, DateTime? modified = null)
        {
            Path = path;
            RelativePath = System.IO.Path.GetRelativePath(root, path);
            DateTime? timestamp = ParseTimestamp(path);
            DisplayName = timestamp?.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                ?? System.IO.Path.GetFileName(path);
            Timestamp = timestamp ?? modified ?? File.GetLastWriteTime(path);
        }

        private static DateTime? ParseTimestamp(string path)
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
                ? timestamp
                : null;
        }
    }

    private readonly string settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
    private readonly ObservableCollection<ActivityRow> activity = [];
    private readonly Dictionary<string, ActivityRow> activityByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<Session> sessions = [];
    private readonly Dictionary<string, Session> sessionsByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer sessionTimer = new() { Interval = TimeSpan.FromMilliseconds(650) };
    private readonly ObjectAnimationUsingKeyFrames activityFlash;
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
        activityFlash = (ObjectAnimationUsingKeyFrames)Dashboard.FindResource("ActivityFlash");
        SqliteGrid.ItemsSource = sqliteChanges;
        string? version = typeof(MainWindow).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "PackageVersionLabel")?.Value;
        if (!string.IsNullOrEmpty(version))
            AppHeading.Text += " " + version;
        ActivityGrid.ItemsSource = activity;
        SessionList.ItemsSource = sessions;
        SessionList.Items.SortDescriptions.Add(new SortDescription(nameof(Session.Timestamp), ListSortDirection.Descending));
        SessionList.Items.SortDescriptions.Add(new SortDescription(nameof(Session.RelativePath), ListSortDirection.Ascending));
        UpdateTypeFilter(null);
        try
        {
            if (File.Exists(settingsPath))
                preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(settingsPath)) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            SettingsStatus.Text = "Could not read settings: " + error.Message;
        }
        UpdateDatabaseChoices();
        StartActivity();
        StartSessions();
        sessionTimer.Tick += ReadSessions;
        sessionTimer.Start();
    }

    private void ResizeDashboard(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged)
            return;

        bool stacked = e.NewSize.Width < 900;
        Grid.SetColumnSpan(ActivityGroup, stacked ? 3 : 1);
        Grid.SetColumnSpan(SessionsGroup, stacked ? 2 : 1);
        Grid.SetRow(SqliteGroup, stacked ? 1 : 0);
        SqliteGroup.Padding = stacked ? new Thickness(16, 10, 0, 0) : new Thickness(16, 0, 0, 0);
        SqliteGroup.BorderThickness = stacked ? new Thickness(1, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        Grid.SetRow(SessionsGroup, stacked ? 1 : 0);
        Grid.SetColumn(SessionsGroup, stacked ? 0 : 1);
        ActivityGroup.Margin = stacked ? new Thickness(0, 0, 0, 10) : new Thickness(0, 0, 16, 0);
        SessionsGroup.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        SessionsGroup.Padding = stacked ? new Thickness(0, 10, 0, 0) : new Thickness(16, 0, 0, 0);
    }

    private void ChooseActivity(object sender, RoutedEventArgs e)
    {
        if (Pick(Window.GetWindow((DependencyObject)sender), "Choose the .Codex folder") is { } path)
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
        UpdateDatabaseChoices();
        StartActivity();
    }

    private void ChooseSessions(object sender, RoutedEventArgs e)
    {
        if (Pick(Window.GetWindow((DependencyObject)sender), "Choose the Codex JSONL sessions folder") is { } path)
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

    private static string? Pick(Window owner, string title)
    {
        var picker = new OpenFolderDialog { Title = title };
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (Directory.Exists(home))
            picker.InitialDirectory = home;
        return picker.ShowDialog(owner) == true ? picker.FolderName : null;
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
        CodexLight.BeginAnimation(Shape.FillProperty, null);
        activityMonitor?.Dispose();
        activityMonitor = null;
        activity.Clear();
        activityByPath.Clear();
        ActivityStatus.Text = "";
        ActivityFolder.Text = preferences.Activity ?? "No folder selected";
        ActivityEmpty.Text = preferences.Activity is null
            ? "Choose a folder in Settings to see file updates here."
            : "Waiting for file updates…";
        ActivityEmpty.Visibility = Visibility.Visible;
        if (preferences.Activity is not { Length: > 0 } root)
            return;

        activityMonitor = new FolderMonitor(root, (path, stamp, _) => Dispatch(() =>
        {
            if (generation != activityGeneration)
                return;
            CodexLight.BeginAnimation(Shape.FillProperty, activityFlash);
            if (stamp is not { } file)
                return;
            if (activityByPath.TryGetValue(path, out var existing))
            {
                existing.Update(file);
                return;
            }
            var folder = Path.GetDirectoryName(path) ?? root;
            string relativeFolder = Path.GetRelativePath(root, folder);
            var row = new ActivityRow(Path.GetFileName(path), relativeFolder == "." ? "root" : relativeFolder, file);
            activityByPath.Add(path, row);
            activity.Insert(0, row);
            ActivityEmpty.Visibility = Visibility.Collapsed;
        }), error => Dispatch(() =>
        {
            if (generation == activityGeneration)
                ActivityStatus.Text = error ?? "";
        }));
        activityMonitor.Start();
    }

    private void StartSessions()
    {
        int generation = ++sessionGeneration;
        SessionLight.BeginAnimation(Shape.FillProperty, null);
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
            SessionLight.BeginAnimation(Shape.FillProperty, activityFlash);
            if (sessionsByPath.TryGetValue(path, out var session))
            {
                session.Dirty = stamp is not null;
                session.Error = stamp is null ? "The session file is unavailable." : null;
                UpdateTimelineStatus();
            }
            else if (stamp is { } file && isNew)
            {
                var added = new Session(path, root, file.ModifiedUtc.ToLocalTime());
                AddSession(added);
                SessionList.SelectedItem ??= added;
            }
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
            PreviousSessionsStatus.Text = "Choose a JSONL sessions folder in Settings first.";
            return;
        }

        int generation = sessionGeneration;
        LoadPreviousButton.IsEnabled = false;
        PreviousSessionsStatus.Text = "Looking for previous sessions…";
        try
        {
            var previous = await Task.Run(() => FolderMonitor.EnumerateFiles(root, "*.jsonl")
                .Select(path => new Session(path, root) { LoadOnSelection = true }).ToArray());
            if (generation != sessionGeneration)
                return;

            int added = 0;
            foreach (var session in previous)
            {
                if (sessionsByPath.ContainsKey(session.Path))
                    continue;
                AddSession(session);
                added++;
            }
            if (SessionList.SelectedItem is null && added > 0)
                SessionList.SelectedIndex = 0;
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
                        session.CommittedTypes.Clear();
                        session.Log.ResetAcknowledged();
                    }
                    else if (session.HasPreview)
                        session.Entries.RemoveAt(session.Entries.Count - 1);
                    foreach (var entry in received)
                    {
                        session.Entries.Add(entry);
                        if (entry.RecordType.Length > 0)
                            session.CommittedTypes.Add(entry.RecordType);
                    }
                    session.HasPreview = session.Log.Preview is not null;
                    if (session.Log.Preview is { } preview)
                        session.Entries.Add(preview);
                    if (session == SessionList.SelectedItem)
                        UpdateTypeFilter(session);
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
        var session = SessionList.SelectedItem as Session;
        UpdateTypeFilter(session);
        timelineView = session is not null
            ? new ListCollectionView(session.Entries) { Filter = Matches }
            : null;
        ApplyTimelineOrder();
        Timeline.ItemsSource = timelineView;
        UpdateTimelineStatus();
    }

    private void UpdateTypeFilter(Session? session)
    {
        IEnumerable<string> types = session?.CommittedTypes ?? Enumerable.Empty<string>();
        if (session is { HasPreview: true } && session.Entries[^1].RecordType is { Length: > 0 } previewType
            && !session.CommittedTypes.Contains(previewType))
            types = types.Append(previewType);

        TypeFilterOption[] options = [new(null), .. types.Order(StringComparer.Ordinal).Select(type => new TypeFilterOption(type))];
        if (TypeBox.ItemsSource is TypeFilterOption[] existing && existing.SequenceEqual(options))
            return;

        var selected = TypeBox.SelectedItem as TypeFilterOption;
        TypeBox.ItemsSource = options;
        TypeBox.SelectedItem = options.FirstOrDefault(option => option == selected) ?? options[0];
    }

    private void ApplyTimelineOrder()
    {
        if (timelineView is null)
            return;

        var direction = preferences.TimelineOldestFirst ? ListSortDirection.Ascending : ListSortDirection.Descending;
        using (timelineView.DeferRefresh())
        {
            var sort = timelineView.SortDescriptions;
            sort.Clear();
            sort.Add(new SortDescription(nameof(TimelineEntry.HasTimestamp), ListSortDirection.Descending));
            sort.Add(new SortDescription(nameof(TimelineEntry.Timestamp), direction));
            sort.Add(new SortDescription(nameof(TimelineEntry.Line), direction));
        }
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
        return (TypeBox.SelectedItem is not TypeFilterOption { Value: string type } || entry.RecordType == type)
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
        sqliteOpenGeneration++;
        dashboardRead?.Cancel();
        sqliteRead?.Cancel();
        sessionTimer.Stop();
        activityGeneration++;
        sessionGeneration++;
        activityMonitor?.Dispose();
        sessionMonitor?.Dispose();
        base.OnClosed(e);
    }
}
