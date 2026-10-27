using System.Windows.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using MeetingDesktop.App.Localization;
using MeetingDesktop.Core;
using MeetingDesktop.Core.Models;
using MeetingDesktop.Core.Workflows;

namespace MeetingDesktop.App.ViewModels;

/// <summary>
/// Saved icon layouts: list, save, restore, delete.
/// </summary>
/// <remarks>
/// <para>
/// This page never touches the desktop path. Saving and restoring icon
/// positions is the half of a swap people need on its own - Windows re-flows a
/// desktop for reasons that have nothing to do with a presentation - so the
/// same two Core calls the Prepare workflow makes are run here, without the
/// registry work in between.
/// </para>
/// <para>
/// Every read of a snapshot file happens inside <see cref="Task.Run"/>. The
/// list needs each file's icon count, which means opening and deserialising
/// every snapshot in the folder; doing that on the UI thread would stall the
/// window for as long as the folder is large, and a stalled window during a
/// click is exactly what a presenter reads as "it froze".
/// </para>
/// <para>
/// The desktop interop never runs on the UI thread. <c>SendMessageTimeout</c>
/// into explorer is a blocking cross-process call with a multi-second timeout
/// built in; see <see cref="DesktopIconPlacer"/>.
/// </para>
/// </remarks>
public sealed class LayoutsViewModel : ObservableObject, IPageViewModel
{
    private readonly LayoutStore _layouts;
    private readonly AppPaths _paths;
    private readonly SessionStore _sessions;
    private readonly MainViewModel _shell;
    private readonly AsyncRelayCommand _saveCurrent;
    private readonly AsyncRelayCommand _restoreSelected;
    private readonly AsyncRelayCommand _deleteSelected;
    private readonly AsyncRelayCommand _refresh;

    private LayoutListItem? _selected;
    private bool _isEmpty = true;
    private bool _isRefreshing;
    private string _searchText = string.Empty;
    private string[] _searchTerms = Array.Empty<string>();

    /// <summary>
    /// Creates the page.
    /// </summary>
    /// <param name="layouts">The snapshot store, already pointed at the layouts folder.</param>
    /// <param name="paths">
    /// The paths used to identify the current session's protected layout.
    /// </param>
    /// <param name="shell">The shell, for the shared status line and busy flag.</param>
    public LayoutsViewModel(LayoutStore layouts, AppPaths paths, MainViewModel shell)
    {
        _layouts = layouts ?? throw new ArgumentNullException(nameof(layouts));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _sessions = new SessionStore(paths.SessionFilePath);
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        FilteredLayouts = new ListCollectionView(Layouts) { Filter = MatchesSearch };
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);
        FocusSearchCommand = new RelayCommand<TextBox>(search =>
        {
            search?.Focus();
            search?.SelectAll();
        });

        _saveCurrent = new AsyncRelayCommand(SaveCurrentAsync, CanStartOperation);
        _restoreSelected = new AsyncRelayCommand(RestoreSelectedAsync, () => CanStartOperation() && Selected?.Snapshot is not null);
        _deleteSelected = new AsyncRelayCommand(DeleteSelectedAsync,
            () => CanStartOperation() && Selected is not null && !IsSelectedActiveSnapshot);
        _refresh = new AsyncRelayCommand(() => RefreshAsync(), () => !_isRefreshing);

        SaveCurrentCommand = _saveCurrent;
        RestoreSelectedCommand = _restoreSelected;
        DeleteSelectedCommand = _deleteSelected;
        RefreshCommand = _refresh;
    }

    public ObservableCollection<LayoutListItem> Layouts { get; } = new();

    public ICollectionView FilteredLayouts { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                _searchTerms = NormalizeSearch(_searchText).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                RefreshFilter();
            }
        }
    }

    public bool HasSearchText => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasSelection => Selected is not null;
    public bool IsSelectedActiveSnapshot
    {
        get
        {
            string? active = _sessions.Load()?.AutoLayoutSnapshotPath;
            if (Selected is null || string.IsNullOrWhiteSpace(active) || !Path.IsPathFullyQualified(active)) return false;
            try
            {
                string selectedPath = Path.Combine(_paths.LayoutsDirectory, Selected.FileName + ".json");
                return string.Equals(Path.GetFullPath(active), Path.GetFullPath(selectedPath), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
        }
    }
    public bool ShowEmpty => IsEmpty && !_isRefreshing;
    public bool ShowNoMatches => !IsEmpty && FilteredLayouts.IsEmpty && !_isRefreshing;
    public bool ShowLoading => IsEmpty && _isRefreshing;
    public string ResultsSummary => LocalizationManager.Instance.Format(
        "LayoutSearchResults", FilteredLayouts.Cast<LayoutListItem>().Count(), Layouts.Count);
    public ICommand ClearSearchCommand { get; }
    public ICommand FocusSearchCommand { get; }

    public LayoutListItem? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(IsSelectedActiveSnapshot));
                RaiseCanExecuteChanged();
            }
        }
    }

    public ICommand SaveCurrentCommand { get; }

    public ICommand RestoreSelectedCommand { get; }

    public ICommand DeleteSelectedCommand { get; }

    public ICommand RefreshCommand { get; }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    /// <summary>Re-reads the folder whenever the page becomes visible.</summary>
    /// <remarks>
    /// Deliberately fire-and-forget: the navigation handler is synchronous, and
    /// the refresh reports its own failures into the status line rather than
    /// through a task nobody awaits.
    /// </remarks>
    public void OnActivated()
    {
        OnPropertyChanged(nameof(IsSelectedActiveSnapshot));
        RaiseCanExecuteChanged();
        _ = RefreshAsync();
    }

    /// <summary>Rebuilds the per-row text after a language change.</summary>
    public void RefreshLocalizedText()
    {
        foreach (LayoutListItem item in Layouts)
        {
            item.RefreshText();
        }

        RefreshFilter();
    }

    private static string NormalizeSearch(string text) => text.Replace('×', 'x');

    private bool MatchesSearch(object value) => value is LayoutListItem item &&
        _searchTerms.All(term => LocalizationManager.Instance.Culture.CompareInfo.IndexOf(
            item.SearchableText, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);

    private void RefreshFilter()
    {
        LayoutListItem? selected = Selected;
        FilteredLayouts.Refresh();
        // Hidden rows must never remain actionable through Restore or Delete.
        Selected = selected is not null && FilteredLayouts.Contains(selected) ? selected : null;
        NotifySearchState();
    }

    private void NotifySearchState()
    {
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(ResultsSummary));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowNoMatches));
        OnPropertyChanged(nameof(ShowLoading));
    }

    public void RaiseCanExecuteChanged()
    {
        _saveCurrent.RaiseCanExecuteChanged();
        _restoreSelected.RaiseCanExecuteChanged();
        _deleteSelected.RaiseCanExecuteChanged();
        _refresh.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// True while no other operation is running. Two icon operations at once
    /// would interleave writes to the same listview and leave the desktop in a
    /// state that matches neither snapshot.
    /// </summary>
    private bool CanStartOperation() => !_shell.IsBusy && !_isRefreshing;

    // -------------------------------------------------------------------
    // Save
    // -------------------------------------------------------------------

    private async Task SaveCurrentAsync()
    {
        string? name = LayoutNameDialog.Ask(Application.Current?.MainWindow);

        if (string.IsNullOrWhiteSpace(name))
        {
            // Cancelled. The status line still describes whatever happened
            // last, which is more useful than replacing it with nothing.
            return;
        }

        SetBusy(true);
        IProgress<ProgressReport> progress = _shell.CreateProgress();

        try
        {
            progress.Report(new ProgressReport(ProgressKey.ReadingCurrentLayout));

            (int iconCount, string path) = await Task.Run(() =>
            {
                IReadOnlyList<IconPosition> icons = DesktopIconReader.Read();

                var snapshot = new LayoutSnapshot
                {
                    Name = name,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    Topology = MonitorTopologyReader.Capture(),
                    Icons = icons,
                };

                // The capture is written before anything else happens to it,
                // so a write that fails leaves no half-saved layout behind and
                // the live desktop untouched.
                return (icons.Count, _layouts.Save(snapshot, name + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fff")));
            });

            _shell.StatusText = LocalizationManager.Instance.Format("LayoutsSaved", iconCount, name);

            await RefreshAsync(Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex)
        {
            // Nothing may escape: this runs from an async void command handler,
            // where an unhandled exception ends the process. The snapshot is
            // written before the save is reported, so a failure here costs the
            // user a layout, never their desktop.
            _shell.StatusText = LocalizationManager.Instance[ErrorKeyFor(ex)];
        }
        finally
        {
            SetBusy(false);
        }
    }

    // -------------------------------------------------------------------
    // Restore
    // -------------------------------------------------------------------

    private async Task RestoreSelectedAsync()
    {
        LayoutSnapshot? snapshot = Selected?.Snapshot;

        if (snapshot is null)
        {
            return;
        }

        SetBusy(true);
        IProgress<ProgressReport> progress = _shell.CreateProgress();

        try
        {
            progress.Report(new ProgressReport(ProgressKey.ApplyingLayout));

            // Place already reads the desktop back and compares, so there is no
            // separate verification step to report progress for.
            PlaceResult result = await Task.Run(() => DesktopIconPlacer.Place(snapshot.Icons));

            _shell.StatusText = DescribeRestore(result);
        }
        catch (Exception ex)
        {
            // See the save path: an escaping exception ends the process.
            _shell.StatusText = LocalizationManager.Instance[ErrorKeyFor(ex)];
        }
        finally
        {
            SetBusy(false);
        }
    }

    // -------------------------------------------------------------------
    // Delete
    // -------------------------------------------------------------------

    private async Task DeleteSelectedAsync()
    {
        LayoutListItem? item = Selected;

        if (item is null)
        {
            return;
        }

        string question = LocalizationManager.Instance.Format("LayoutsDeleteConfirm", item.DisplayName);
        string title = LocalizationManager.Instance["LayoutsDeleteSelected"];

        // Default answer No, and the owner is set so the prompt cannot open
        // behind the window that raised it. Deleting is the one action here
        // that cannot be undone from inside the app.
        bool confirmed = Application.Current?.MainWindow is { IsLoaded: true } owner
            ? MessageBox.Show(owner, question, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes
            : MessageBox.Show(question, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

        if (!confirmed)
        {
            return;
        }

        SetBusy(true);

        try
        {
            string fileName = item.FileName;
            LayoutDeleteResult result = await Task.Run(() => _layouts.DeleteWithReason(fileName));

            _shell.StatusText = LocalizationManager.Instance[result switch
            {
                LayoutDeleteResult.Deleted => "ProgressDone",
                LayoutDeleteResult.SessionReferenced => "LayoutsDeleteSessionReferenced",
                LayoutDeleteResult.SessionUnreadable => "LayoutsDeleteSessionUnreadable",
                _ => "ErrorUnexpected",
            }];

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _shell.StatusText = LocalizationManager.Instance[ErrorKeyFor(ex)];
        }
        finally
        {
            SetBusy(false);
        }
    }

    // -------------------------------------------------------------------
    // Refresh
    // -------------------------------------------------------------------

    /// <summary>
    /// Rebuilds the list from the folder, keeping the selection where possible.
    /// </summary>
    /// <param name="selectFileName">
    /// File to select afterwards, overriding the current selection. Used after
    /// a save so the row the user just created is the selected one.
    /// </param>
    private async Task RefreshAsync(string? selectFileName = null)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        NotifySearchState();
        RaiseCanExecuteChanged();

        try
        {
            // A refresh must not move the user's selection: the list is
            // re-read on every visit to the page, and dropping the selection
            // would silently disable restore and delete.
            string? keep = selectFileName ?? Selected?.FileName;

            List<LayoutListItem> items = await Task.Run(() => LoadItems(_layouts));

            // A newly saved layout must remain visible and selected even if the
            // previous query would exclude it.
            if (selectFileName is not null)
            {
                SearchText = string.Empty;
            }

            // ListBox and the empty-state bindings query this view as rows are
            // added. Deferring refresh would make those queries throw and leave
            // the list partially populated.
            Layouts.Clear();

            foreach (LayoutListItem item in items)
            {
                // The language may have changed while files were loading.
                item.RefreshText();
                Layouts.Add(item);
            }

            IsEmpty = Layouts.Count == 0;
            Selected = FilteredLayouts.Cast<LayoutListItem>().FirstOrDefault(i => i.FileName == keep)
                ?? FilteredLayouts.Cast<LayoutListItem>().FirstOrDefault();
        }
        catch (Exception ex)
        {
            // The folder can be removed by a sync client or a cleanup tool
            // between visits. The old rows are left in place rather than
            // cleared, so a transient read failure does not empty a list the
            // user was looking at.
            _shell.StatusText = LocalizationManager.Instance[ErrorKeyFor(ex)];
        }
        finally
        {
            _isRefreshing = false;
            NotifySearchState();
            RaiseCanExecuteChanged();
        }
    }

    internal static List<LayoutListItem> LoadItems(LayoutStore layouts)
    {
        var items = new List<LayoutListItem>();
        foreach (string fileName in layouts.List())
            items.Add(new LayoutListItem(fileName, layouts.Load(fileName)));
        return items;
    }

    /// <summary>
    /// Maps a failure to the string that names it.
    /// </summary>
    /// <remarks>
    /// The interop kinds map onto <c>Error</c> plus the enum member name,
    /// because the kind is Core's contract and the wording belongs to the UI.
    /// </remarks>
    internal static string DescribeRestore(PlaceResult result)
    {
        var localization = LocalizationManager.Instance;
        string message = result.IsExact
            ? localization.Format("LayoutsRestored", result.Placed, result.Requested)
            : localization.Format("LayoutsRestorePartial", result.Placed, result.Requested, result.OffTarget.Count, result.NotFound.Count);
        if (result.UsedTwoPhase && !result.ParkingSucceeded)
            message += " " + localization["ResultParkingDegraded"];
        return message;
    }

    private static string ErrorKeyFor(Exception error) => error switch
    {
        DesktopInteropException interop => "Error" + interop.Kind,
        _ => "ErrorUnexpected",
    };

    private void SetBusy(bool busy)
    {
        _shell.IsBusy = busy;
        _shell.ProgressValue = 0;
        RaiseCanExecuteChanged();
    }

    // -------------------------------------------------------------------
    // List item
    // -------------------------------------------------------------------

    /// <summary>
    /// One row of the list.
    /// </summary>
    /// <remarks>
    /// Everything a row shows is computed once, when the row is built, and
    /// stored. Rebuilding the strings from the bindings on every refresh would
    /// mean re-opening every snapshot file for the sake of a label.
    /// </remarks>
    public sealed class LayoutListItem : ObservableObject
    {
        private string _summary;
        private string _searchableText = string.Empty;

        public LayoutListItem(string fileName, LayoutSnapshot? snapshot)
        {
            FileName = fileName;
            Snapshot = snapshot;

            // Null when the file could not be read - a damaged snapshot, or one
            // written by a newer build. It is still listed, because hiding it
            // would leave the user unable to delete the file that is failing.
            DisplayName = snapshot?.Name is { Length: > 0 } name ? name : fileName;
            IconCount = snapshot?.Icons.Count ?? 0;
            IsAutomatic = snapshot?.IsAutomatic ?? false;

            // "g" rather than a fixed pattern: the date is read at a glance by
            // someone comparing two rows, and every culture writes it
            // differently. The culture is the one the UI is in, not the
            // machine's, since the language is chosen in this app.
            Thumbnail = BuildThumbnail(snapshot);
            _summary = BuildSummary();
            RefreshText();
        }

        /// <summary>Name without the extension; the identity used by the store.</summary>
        public string FileName { get; }

        public string DisplayName { get; }

        public int IconCount { get; }

        public string CreatedLocal => Snapshot is null || Snapshot.CreatedUtc == default
            ? string.Empty
            : Snapshot.CreatedUtc.ToLocalTime().ToString("g", LocalizationManager.Instance.Culture);

        public bool HasSnapshot => Snapshot is not null;

        public string IconCountText => HasSnapshot
            ? LocalizationManager.Instance.Format("LayoutsCounts", IconCount)
            : LocalizationManager.Instance["CommonNotAvailable"];

        public string DisplayCountText => HasSnapshot
            ? LocalizationManager.Instance.Format("LayoutSearchDisplayCount", Snapshot!.Topology.Monitors.Count)
            : LocalizationManager.Instance["CommonNotAvailable"];

        public string CreatedDetailed => Snapshot is null || Snapshot.CreatedUtc == default
            ? LocalizationManager.Instance["CommonNotAvailable"]
            : LocalizationManager.Instance.Format("LayoutSearchLocalTime",
                Snapshot.CreatedUtc.ToLocalTime().ToString("G", LocalizationManager.Instance.Culture),
                Snapshot.CreatedUtc.ToLocalTime().ToString("zzz", CultureInfo.InvariantCulture));

        public string TopologyDetails => Snapshot is null || Snapshot.Topology.Monitors.Count == 0
            ? LocalizationManager.Instance["LayoutSearchNoTopology"]
            : string.Join(Environment.NewLine + Environment.NewLine, Snapshot.Topology.Monitors.Select((monitor, index) =>
                LocalizationManager.Instance.Format("LayoutSearchMonitorDetails", index + 1, monitor.DeviceName,
                    monitor.IsPrimary ? " · " + LocalizationManager.Instance["LayoutSearchPrimary"] : string.Empty,
                    monitor.Width, monitor.Height, monitor.X, monitor.Y)));

        internal string SearchableText => _searchableText;

        /// <summary>True for the pre-swap snapshots the Prepare workflow writes.</summary>
        public bool IsAutomatic { get; }

        public ImageSource Thumbnail { get; }

        private static ImageSource BuildThumbnail(LayoutSnapshot? snapshot)
        {
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
            {
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(230, 238, 250)), null,
                    new Rect(0, 0, 144, 84), 8, 8);
                if (snapshot is not null && snapshot.Icons.Count > 0)
                {
                    double minX = Math.Min(0, snapshot.Icons.Min(i => i.X));
                    double minY = Math.Min(0, snapshot.Icons.Min(i => i.Y));
                    double width = Math.Max(1, snapshot.Icons.Max(i => i.X) - minX + 100);
                    double height = Math.Max(1, snapshot.Icons.Max(i => i.Y) - minY + 100);
                    foreach (var icon in snapshot.Icons)
                        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(41, 99, 233)), null,
                            new Rect(6 + (icon.X - minX) / width * 130, 6 + (icon.Y - minY) / height * 70, 3, 3), 1, 1);
                }
            }
            drawing.Freeze();
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }

        public string Summary => _summary;

        /// <summary>The loaded snapshot, or null when the file could not be read.</summary>
        internal LayoutSnapshot? Snapshot { get; }

        /// <summary>Rebuilds the summary after a language change.</summary>
        public void RefreshText()
        {
            SetProperty(ref _summary, BuildSummary(), nameof(Summary));
            _searchableText = NormalizeSearch(string.Join(" ", DisplayName, FileName, Summary,
                CreatedDetailed, TopologyDetails, DisplayCountText,
                IsAutomatic ? LocalizationManager.Instance["LayoutsAutomaticTag"] : string.Empty,
                Snapshot is null || Snapshot.CreatedUtc == default ? string.Empty :
                    Snapshot.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Snapshot is null || Snapshot.CreatedUtc == default ? string.Empty :
                    Snapshot.CreatedUtc.ToString("O", CultureInfo.InvariantCulture)));
            OnPropertyChanged(nameof(CreatedLocal));
            OnPropertyChanged(nameof(CreatedDetailed));
            OnPropertyChanged(nameof(IconCountText));
            OnPropertyChanged(nameof(DisplayCountText));
            OnPropertyChanged(nameof(TopologyDetails));
        }

        private string BuildSummary()
        {
            if (Snapshot is null)
            {
                return LocalizationManager.Instance["CommonNotAvailable"];
            }

            string counts = LocalizationManager.Instance.Format("LayoutsCounts", IconCount);
            string displays = string.Join(" / ", Snapshot.Topology.Monitors.Select(m => $"{m.Width}×{m.Height}"));
            return $"{counts} · {CreatedLocal}" + (displays.Length == 0 ? string.Empty : $" · {displays}");
        }
    }

    // -------------------------------------------------------------------
    // Name prompt
    // -------------------------------------------------------------------

    /// <summary>
    /// The one-field modal that asks what to call a layout.
    /// </summary>
    /// <remarks>
    /// Built in code because it is one label, one text box and two buttons;
    /// a XAML file plus its code-behind would be longer than this and would
    /// have to be compiled into the window's resources for no benefit.
    /// <c>Microsoft.VisualBasic.Interaction.InputBox</c> is not an option - it
    /// is a Win32 dialog with its own fonts and colours, so it lands on the
    /// poster's blue field looking like a window from another program.
    /// </remarks>
    private sealed class LayoutNameDialog : Window
    {
        private readonly TextBox _input;

        private LayoutNameDialog()
        {
            LocalizationManager strings = LocalizationManager.Instance;

            Title = strings["LayoutsNameTitle"];
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            SetResourceReference(BackgroundProperty, "Brush.WindowGradient");
            SetResourceReference(ForegroundProperty, "Brush.Text.Primary");

            var prompt = new TextBlock
            {
                Text = strings["LayoutsNamePrompt"],
                Margin = new Thickness(0, 0, 0, 8),
            };

            prompt.SetResourceReference(StyleProperty, "Text.Secondary");

            _input = new TextBox { MinWidth = 300 };
            _input.SetResourceReference(StyleProperty, "TextBox.Themed");

            var cancel = new Button
            {
                Content = strings["CommonCancel"],
                IsCancel = true,
                MinWidth = 88,
                Margin = new Thickness(0, 0, 8, 0),
            };

            cancel.SetResourceReference(StyleProperty, "Button.Ghost");

            var accept = new Button
            {
                Content = strings["CommonOk"],
                IsDefault = true,
                IsEnabled = false,
                MinWidth = 88,
            };

            accept.SetResourceReference(StyleProperty, "Button.Primary");
            accept.Click += (_, _) => DialogResult = true;

            // An all-whitespace name would be sanitised into a default on the
            // way to disk, saving a layout under a name the user never chose.
            _input.TextChanged += (_, _) => accept.IsEnabled = !string.IsNullOrWhiteSpace(_input.Text);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };

            buttons.Children.Add(cancel);
            buttons.Children.Add(accept);

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(prompt);
            panel.Children.Add(_input);
            panel.Children.Add(buttons);

            Content = panel;
        }

        /// <summary>
        /// Shows the prompt and blocks until it closes.
        /// </summary>
        /// <returns>The trimmed name, or null when the user cancelled.</returns>
        public static string? Ask(Window? owner)
        {
            var dialog = new LayoutNameDialog();

            if (owner is not null)
            {
                dialog.Owner = owner;
            }

            dialog.Loaded += (_, _) =>
            {
                dialog._input.Focus();
                dialog._input.SelectAll();
            };

            string? entered = dialog.ShowDialog() == true ? dialog._input.Text.Trim() : null;

            // One shot: a window shown twice throws, and nothing here needs a
            // second answer to the same question.
            return entered;
        }
    }
}
