using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Watcher.Core;
using Forms = System.Windows.Forms;

namespace Watcher;

public partial class MainWindow : Window
{
    private const int DrainBudgetPerTick = 4_000;

    private readonly List<FileEvent> _batch = new(DrainBudgetPerTick);
    private readonly DispatcherTimer _drainTimer;
    private readonly DispatcherTimer _resortTimer;

    private readonly ObservableCollection<string> _ignorePatterns = new();
    private readonly ObservableCollection<string> _ignoredProcesses = new();
    private readonly ObservableCollection<ProcessHit> _topProcesses = new();
    private readonly ObservableCollection<LogLine> _logLines = new();
    private readonly ObservableCollection<TargetRow> _targetRows = new();
    private readonly List<PathNode> _treeRoots = new();

    private ListCollectionView? _activityView;
    private ListCollectionView? _targetView;
    private ListCollectionView? _logView;

    private long _lastEventCount;
    private DateTime _lastRateSample = DateTime.UtcNow;
    private bool _settingsDirty;
    private bool _loadingSettings;
    private LogLevel _minLogLevel = LogLevel.Debug;

    public sealed record ProcessHit(string Name, int Hits);

    private static App CurrentApp => (App)Application.Current;
    private static AppSettings Settings => App.Settings;
    private static MonitorService Monitor => App.Monitor;
    private static ActivityStore Store => App.Store;

    public MainWindow()
    {
        InitializeComponent();

        // Trace de controle : si le XAML n'a pas ete applique entierement, WindowStyle
        // et les dimensions ne correspondront pas aux valeurs declarees
        // (None, 1320x820) et la fenetre apparaitra avec le chrome Windows par defaut.
        AppLogger.Debug($"XAML applique : style={WindowStyle}, taille={Width}x{Height}, " +
                        $"min={MinWidth}x{MinHeight}, transparence={AllowsTransparency}");

        // Vidange de la file de capture par lots : les collections observables ne
        // supportent pas la cadence brute des evenements noyau.
        _drainTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _drainTimer.Tick += (_, _) => DrainTick();

        // Le tri du tableau est reapplique periodiquement, jamais a chaque lot :
        // reordonner en continu rendrait la lecture impossible.
        _resortTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _resortTimer.Tick += (_, _) => ResortActivity();

        Loaded += OnLoaded;
        Closed += (_, _) => { _drainTimer.Stop(); _resortTimer.Stop(); };

        // Revenir au premier plan ou sortir d'une reduction peut laisser une surface
        // perimee : on redemande un trace complet a chaque fois.
        Activated += (_, _) => Waves.InvalidateVisual();
        StateChanged += (_, _) => Waves.InvalidateVisual();
        IsVisibleChanged += (_, _) => Waves.InvalidateVisual();
    }

    /// <summary>
    /// Le mode de rendu ne peut etre choisi qu'une fois le handle Win32 cree.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Tier 0 : aucune acceleration materielle utilisable, le rendu logiciel est
        // alors le seul chemin viable, quel que soit le reglage de l'utilisateur.
        var noHardware = (RenderCapability.Tier >> 16) == 0;
        if (noHardware && !Settings.SoftwareRendering)
        {
            AppLogger.Warn("Aucune acceleration materielle disponible : rendu logiciel force");
            Settings.SoftwareRendering = true;
        }

        var handle = PresentationSource.FromVisual(this) is HwndSource s ? s.Handle : IntPtr.Zero;
        AppLogger.Info($"Fenetre realisee : hwnd=0x{handle.ToInt64():X}, style={WindowStyle}, " +
                       $"acceleration materielle tier={RenderCapability.Tier >> 16}");

        ApplyRenderMode(Settings.SoftwareRendering);
    }

    private void ApplyRenderMode(bool software)
    {
        try
        {
            if (PresentationSource.FromVisual(this) is not HwndSource { CompositionTarget: { } target })
            {
                AppLogger.Warn("Surface de rendu indisponible : mode de rendu inchange");
                return;
            }

            target.RenderMode = software ? RenderMode.SoftwareOnly : RenderMode.Default;

            // Le rendu logiciel rasterise chaque pixel sur le processeur : l'animation
            // passe en mode econome pour ne pas monopoliser un coeur.
            Waves.ReducedQuality = software;

            AppLogger.Info($"Rendu {(software ? "logiciel (processeur), animation en mode econome" : "materiel (GPU)")} actif");

            // Force un repaint complet : le basculement laisse sinon l'ancienne surface.
            Waves.InvalidateVisual();
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Changement de mode de rendu impossible : {ex.Message}");
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // Les cibles doivent etre connues du magasin avant toute ingestion, pour que
        // les nouvelles lignes soient marquees des leur creation.
        Store.Targets = new WatchTargetSet(Settings.WatchTargets);

        BuildActivityView();
        BuildTargetView();
        BuildFilters();
        BuildLogView();

        LiveGrid.ItemsSource = Store.Live;
        IgnoreList.ItemsSource = _ignorePatterns;
        IgnoredProcessList.ItemsSource = _ignoredProcesses;
        TopProcesses.ItemsSource = _topProcesses;
        DriveSummary.ItemsSource = DriveEnumerator.All();
        DataPathText.Text = AppPaths.Abbreviate(AppPaths.Root);
        LogSubtitle.Text = $"Fichier du jour : {AppPaths.Abbreviate(AppLogger.CurrentLogFile)}";

        LoadSettingsIntoUi();
        ApplyElevationState();

        Monitor.StatusChanged += OnStatusChanged;
        AppLogger.Logged += OnLogged;
        foreach (var line in AppLogger.Snapshot()) _logLines.Add(line);

        RefreshStatusUi(Monitor.Status);
        RefreshTargetRows();
        UpdateStats();

        Waves.IsAnimated = Settings.AnimatedBackground;

        // Nav_Checked est declenche pendant l'analyse du XAML, avant que Pages n'existe :
        // on fixe la page d'accueil explicitement une fois l'arbre visuel construit.
        NavDashboard.IsChecked = true;
        Pages.SelectedIndex = 0;

        _drainTimer.Start();
        _resortTimer.Start();
    }

    // ==================================================================
    //  Capture -> interface
    // ==================================================================

    private void DrainTick()
    {
        _batch.Clear();
        Monitor.Drain(DrainBudgetPerTick, e => _batch.Add(e));

        if (_batch.Count > 0)
            Store.Ingest(_batch);

        UpdateStats();
    }

    private void UpdateStats()
    {
        StatEvents.Text = Format(Store.TotalEvents);
        StatFiles.Text = Format(Store.UniqueFiles);
        StatProcesses.Text = Format(Store.UniqueProcesses);
        StatFiltered.Text = Format(Monitor.FilteredCount);
        StatDropped.Text = $"{Format(Monitor.DroppedCount)} perdu(s) — {Format(Monitor.PendingCount)} en file";
        StatTrim.Text = $"plafond {Settings.MaxActivityEntries:N0}";

        // Cadence instantanee, lissee sur l'intervalle reel entre deux mesures.
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastRateSample).TotalSeconds;
        if (elapsed >= 1.0)
        {
            var delta = Store.TotalEvents - _lastEventCount;
            StatRate.Text = $"{delta / elapsed:0} /s";
            _lastEventCount = Store.TotalEvents;
            _lastRateSample = now;

            RefreshTopProcesses();
        }

        StatAttribution.Text = Monitor.Status.ProcessAttribution
            ? "attribution active"
            : "attribution indisponible";

        DashSubtitle.Text = Monitor.Status.Running
            ? $"{Monitor.Status.Message} — depuis {Store.SessionStart:HH:mm:ss}"
            : "Aucune capture en cours. Activez la surveillance dans le panneau de gauche.";

        if (_activityView is not null)
            ActivitySubtitle.Text =
                $"{_activityView.Count:N0} ligne(s) affichee(s) sur {Store.UniqueFiles:N0}.";
    }

    private void RefreshTopProcesses()
    {
        var top = Store.TopProcesses(14).ToList();
        _topProcesses.Clear();
        foreach (var (name, hits) in top)
            _topProcesses.Add(new ProcessHit(name, hits));

        TopHint.Text = top.Count > 0
            ? $"{Store.UniqueProcesses} processus distinct(s) observe(s)."
            : Monitor.Status.ProcessAttribution
                ? "En attente d'evenements attribues."
                : "Requiert le moteur ETW (relancez en administrateur).";
    }

    private static string Format(long n) => n.ToString("N0", CultureInfo.CurrentCulture);

    // ==================================================================
    //  Vue du tableau d'activite
    // ==================================================================

    private void BuildActivityView()
    {
        _activityView = new ListCollectionView(Store.Files) { Filter = ActivityFilter };
        _activityView.SortDescriptions.Add(new SortDescription(nameof(FileActivityEntry.Count),
            ListSortDirection.Descending));
        ActivityGrid.ItemsSource = _activityView;
    }

    private bool ActivityFilter(object item)
    {
        if (item is not FileActivityEntry f) return false;

        if (DriveFilter.SelectedItem is string drive && drive != AllDrives &&
            !string.Equals(f.Drive, drive, StringComparison.OrdinalIgnoreCase))
            return false;

        if (ActionFilter.SelectedItem is string action && action != AllActions)
        {
            var ok = action switch
            {
                "Lectures" => f.Reads > 0,
                "Ecritures" => f.Writes > 0,
                "Suppressions" => f.Deletes > 0,
                _ => true
            };
            if (!ok) return false;
        }

        var q = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(q)) return true;

        return f.FileName.Contains(q, StringComparison.OrdinalIgnoreCase)
               || f.Directory.Contains(q, StringComparison.OrdinalIgnoreCase)
               || f.ProcessSummary.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reapplique le tri. Gele quand l'utilisateur a une selection en cours ou que
    /// le flux est en pause : deplacer les lignes sous le curseur serait insupportable.
    /// </summary>
    private void ResortActivity()
    {
        // Le recalcul des compteurs de cibles parcourt tout le tableau : on ne le fait
        // que lorsque l'onglet concerne est reellement affiche.
        if (Pages.SelectedIndex == 2)
        {
            RefreshTargetRows();
            if (TargetGrid.SelectedItems.Count == 0)
                try { _targetView?.Refresh(); }
                catch (Exception ex) { AppLogger.Debug($"Rafraichissement des cibles ignore : {ex.Message}"); }
            return;
        }

        if (_activityView is null) return;
        if (Pages.SelectedIndex != 1) return;
        if (Store.Paused) return;
        if (ActivityGrid.SelectedItems.Count > 0) return;

        try { _activityView.Refresh(); }
        catch (Exception ex) { AppLogger.Debug($"Rafraichissement du tri ignore : {ex.Message}"); }
    }

    private const string AllDrives = "Tous les disques";
    private const string AllActions = "Tous les acces";

    private void BuildFilters()
    {
        DriveFilter.Items.Add(AllDrives);
        foreach (var d in DriveEnumerator.All())
            DriveFilter.Items.Add(d.Letter);
        DriveFilter.SelectedIndex = 0;

        foreach (var a in new[] { AllActions, "Lectures", "Ecritures", "Suppressions" })
            ActionFilter.Items.Add(a);
        ActionFilter.SelectedIndex = 0;
    }

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (_activityView is null) return;

        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        _activityView.Refresh();
        ActivitySubtitle.Text = $"{_activityView.Count:N0} ligne(s) affichee(s) sur {Store.UniqueFiles:N0}.";
    }

    private void ActivityGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActivityGrid.SelectedItem is not FileActivityEntry f)
        {
            DetailPane.Visibility = Visibility.Collapsed;
            return;
        }

        DetailPane.Visibility = Visibility.Visible;
        DetailPath.Text = f.Path;
        DetailStats.Text =
            $"{f.Count:N0} acces — {f.Reads:N0} lecture(s), {f.Writes:N0} ecriture(s), {f.Deletes:N0} suppression(s)  •  " +
            $"premier le {f.FirstSeen:dd/MM/yyyy a HH:mm:ss}  •  dernier le {f.LastSeen:dd/MM/yyyy a HH:mm:ss}";

        var breakdown = f.ProcessBreakdown().Take(12).ToList();
        DetailProcesses.Text = breakdown.Count == 0
            ? "Accedants : non disponibles avec le moteur actuel."
            : "Accedants : " + string.Join("   ", breakdown.Select(p => $"{p.Process} x{p.Count}"));
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        ActivityGrid.UnselectAll();
        DetailPane.Visibility = Visibility.Collapsed;
    }

    private void PauseLive_Click(object sender, RoutedEventArgs e)
        => Store.Paused = PauseLive.IsChecked == true;

    private void ClearActivity_Click(object sender, RoutedEventArgs e)
    {
        Store.Clear();
        Monitor.ResetCounters();
        _lastEventCount = 0;
        _topProcesses.Clear();
        UpdateStats();
        AppLogger.Info("Tableau d'activite vide par l'utilisateur");
    }

    // ==================================================================
    //  Exclusions depuis le tableau
    // ==================================================================

    private void IgnoreFile_Click(object sender, RoutedEventArgs e)
        => AddIgnorePatterns(SelectedEntries().Select(f => f.Path), "fichier");

    private void IgnoreFolder_Click(object sender, RoutedEventArgs e)
        => AddIgnorePatterns(SelectedEntries().Select(f => f.Directory), "dossier");

    private List<FileActivityEntry> SelectedEntries()
        => ActivityGrid.SelectedItems.OfType<FileActivityEntry>().ToList();

    private void AddIgnorePatterns(IEnumerable<string> paths, string kind)
    {
        var added = 0;
        foreach (var p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (_ignorePatterns.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            _ignorePatterns.Add(p);
            added++;
        }

        if (added == 0)
        {
            SetHint("Rien a ajouter : selectionnez une ou plusieurs lignes.");
            return;
        }

        // L'exclusion prend effet immediatement : c'est le geste attendu quand on
        // veut faire taire du bruit, sans passer par l'onglet Parametres.
        Settings.IgnorePatterns = _ignorePatterns.ToList();
        CurrentApp.ApplySettings();

        var purged = Store.PurgeIgnored(new IgnoreMatcher(Settings.IgnorePatterns));
        _activityView?.Refresh();

        AppLogger.Info($"{added} exclusion(s) de type {kind} ajoutee(s) ; {purged} ligne(s) retiree(s) du tableau");
        SetHint($"{added} exclusion(s) ajoutee(s) et appliquee(s).");
        MarkClean();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
        => RevealEntry(ActivityGrid.SelectedItem as FileActivityEntry);

    private void RevealEntry(FileActivityEntry? f)
    {
        if (f is null) return;
        try
        {
            if (File.Exists(f.Path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{f.Path}\"")
                { UseShellExecute = true });
            else if (Directory.Exists(f.Directory))
                Process.Start(new ProcessStartInfo(f.Directory) { UseShellExecute = true });
            else
                SetHint("Ce chemin n'existe plus sur le disque.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Ouverture de l'emplacement impossible : {ex.Message}");
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedEntries().Select(f => f.Path).ToList();
        if (paths.Count == 0) return;
        try { Clipboard.SetText(string.Join(Environment.NewLine, paths)); }
        catch (Exception ex) { AppLogger.Warn($"Copie dans le presse-papiers impossible : {ex.Message}"); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
        => ExportView(_activityView, "activite");

    private void ExportView(ListCollectionView? view, string label)
    {
        if (view is null) return;

        try
        {
            AppPaths.EnsureCreated();
            var file = Path.Combine(AppPaths.ExportDirectory,
                $"watcher-{label}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

            var sb = new StringBuilder();
            sb.AppendLine("Chemin;Fichier;Dossier;Disque;Cible;Acces;Lectures;Ecritures;Suppressions;" +
                          "PremierAcces;DernierAcces;DerniereAction;DernierProcessus;DernierPID;Processus");

            foreach (var f in view.Cast<FileActivityEntry>())
            {
                sb.Append(Csv(f.Path)).Append(';')
                  .Append(Csv(f.FileName)).Append(';')
                  .Append(Csv(f.Directory)).Append(';')
                  .Append(Csv(f.Drive)).Append(';')
                  .Append(f.IsTargeted ? "oui" : "non").Append(';')
                  .Append(f.Count).Append(';')
                  .Append(f.Reads).Append(';')
                  .Append(f.Writes).Append(';')
                  .Append(f.Deletes).Append(';')
                  .Append(f.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                  .Append(f.LastSeen.ToString("yyyy-MM-dd HH:mm:ss")).Append(';')
                  .Append(Csv(f.LastAction)).Append(';')
                  .Append(Csv(f.LastProcess)).Append(';')
                  .Append(f.LastPid).Append(';')
                  .Append(Csv(string.Join(" | ", f.ProcessBreakdown().Select(p => $"{p.Process} x{p.Count}"))))
                  .AppendLine();
            }

            // BOM UTF-8 : sans elle, Excel casse les accents a l'ouverture.
            File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
            AppLogger.Info($"Export CSV ecrit : {file}");

            Process.Start(new ProcessStartInfo(AppPaths.ExportDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Export CSV impossible : {ex.Message}");
            MessageBox.Show($"L'export a echoue :\n{ex.Message}", "Watcher",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Csv(string? v)
    {
        v ??= "";
        return v.Contains(';') || v.Contains('"') || v.Contains('\n')
            ? '"' + v.Replace("\"", "\"\"") + '"'
            : v;
    }

    // ==================================================================
    //  Parametres
    // ==================================================================

    private void LoadSettingsIntoUi()
    {
        _loadingSettings = true;
        try
        {
            switch (Settings.Scope)
            {
                case ScopeMode.All: ScopeAll.IsChecked = true; break;
                case ScopeMode.None: ScopeNone.IsChecked = true; break;
                default: ScopeSpecific.IsChecked = true; break;
            }

            OptReads.IsChecked = Settings.TrackReads;
            OptWrites.IsChecked = Settings.TrackWrites;
            OptDeletes.IsChecked = Settings.TrackDeletes;
            OptRenames.IsChecked = Settings.TrackRenames;
            OptAttribution.IsChecked = Settings.EnableProcessAttribution;
            OptSkipDirs.IsChecked = Settings.IgnoreDirectoryEvents;

            OptStartup.IsChecked = StartupManager.IsEnabled();
            OptStartMinimized.IsChecked = Settings.StartMinimized;
            OptLogFile.IsChecked = Settings.LogToFile;
            OptAnimation.IsChecked = Settings.AnimatedBackground;
            OptSoftwareRender.IsChecked = Settings.SoftwareRendering;
            OptMaxEntries.Text = Settings.MaxActivityEntries.ToString(CultureInfo.InvariantCulture);

            _ignorePatterns.Clear();
            foreach (var p in Settings.IgnorePatterns) _ignorePatterns.Add(p);

            _ignoredProcesses.Clear();
            foreach (var p in Settings.IgnoredProcesses) _ignoredProcesses.Add(p);

            BuildTree();
            UpdateTreeEnabled();
        }
        finally
        {
            _loadingSettings = false;
        }

        HookDirtyTracking();
        MarkClean();
    }

    /// <summary>Marque la configuration comme modifiee des qu'un controle bouge.</summary>
    private void HookDirtyTracking()
    {
        foreach (var cb in new[]
                 {
                     OptReads, OptWrites, OptDeletes, OptRenames, OptAttribution, OptSkipDirs,
                     OptStartup, OptStartMinimized, OptLogFile, OptAnimation
                 })
        {
            cb.Checked -= AnySetting_Changed;
            cb.Unchecked -= AnySetting_Changed;
            cb.Checked += AnySetting_Changed;
            cb.Unchecked += AnySetting_Changed;
        }

        OptMaxEntries.TextChanged -= MaxEntries_Changed;
        OptMaxEntries.TextChanged += MaxEntries_Changed;
    }

    private void AnySetting_Changed(object sender, RoutedEventArgs e) => MarkDirty();
    private void MaxEntries_Changed(object sender, TextChangedEventArgs e) => MarkDirty();

    private void MarkDirty()
    {
        if (_loadingSettings) return;
        _settingsDirty = true;
        SettingsHint.Text = "Modifications en attente — cliquez sur « Appliquer et enregistrer ».";
    }

    private void MarkClean()
    {
        _settingsDirty = false;
        SettingsHint.Text = "Aucune modification en attente.";
    }

    private void SetHint(string text) => SettingsHint.Text = text;

    private void Scope_Changed(object sender, RoutedEventArgs e)
    {
        UpdateTreeEnabled();
        MarkDirty();
    }

    private void UpdateTreeEnabled()
    {
        var specific = ScopeSpecific.IsChecked == true;
        if (TreeSection is null) return;

        TreeSection.IsEnabled = specific;
        TreeSection.Opacity = specific ? 1.0 : 0.45;
        TreeHint.Text = specific
            ? "Cochez les disques ou les dossiers a suivre. Un dossier coche couvre tout son contenu."
            : ScopeAll.IsChecked == true
                ? "Tous les disques fixes sont suivis integralement."
                : "Aucune cible : la surveillance ne remontera rien.";
    }

    private void BuildTree()
    {
        _treeRoots.Clear();

        foreach (var d in DriveEnumerator.All())
        {
            var node = new PathNode(d.RootPath, d.Display, true);
            node.Detail = d.Capacity;
            _treeRoots.Add(node);
        }

        DriveTree.ItemsSource = _treeRoots;

        if (Settings.Scope == ScopeMode.Specific && Settings.WatchedPaths.Count > 0)
        {
            var selected = Settings.WatchedPaths;
            foreach (var root in _treeRoots) root.ApplySelection(selected);
        }
    }

    private void TreeCheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _treeRoots) r.IsChecked = true;
        MarkDirty();
    }

    private void TreeUncheckAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _treeRoots) r.IsChecked = false;
        MarkDirty();
    }

    private void TreeCollapse_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _treeRoots) Collapse(r);

        static void Collapse(PathNode n)
        {
            n.IsExpanded = false;
            foreach (var c in n.Children) Collapse(c);
        }
    }

    private void AddIgnore_Click(object sender, RoutedEventArgs e) => CommitIgnoreInput();

    private void IgnoreInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitIgnoreInput();
        e.Handled = true;
    }

    private void CommitIgnoreInput()
    {
        var v = IgnoreInput.Text.Trim();
        if (v.Length == 0) return;

        if (_ignorePatterns.Contains(v, StringComparer.OrdinalIgnoreCase))
        {
            SetHint("Ce motif est deja dans la liste.");
            return;
        }

        _ignorePatterns.Add(v);
        IgnoreInput.Clear();
        MarkDirty();
    }

    private void BrowseIgnore_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog
        {
            Description = "Dossier a exclure de la surveillance",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(dlg.SelectedPath)) return;

        if (!_ignorePatterns.Contains(dlg.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            _ignorePatterns.Add(dlg.SelectedPath);
            MarkDirty();
        }
    }

    private void RemoveIgnore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string pattern })
        {
            _ignorePatterns.Remove(pattern);
            MarkDirty();
        }
    }

    private void ResetIgnore_Click(object sender, RoutedEventArgs e)
    {
        _ignorePatterns.Clear();
        foreach (var p in AppSettings.DefaultIgnorePatterns) _ignorePatterns.Add(p);
        MarkDirty();
    }

    private void ApplySettings_Click(object sender, RoutedEventArgs e)
    {
        Settings.Scope = ScopeNone.IsChecked == true ? ScopeMode.None
            : ScopeSpecific.IsChecked == true ? ScopeMode.Specific
            : ScopeMode.All;

        if (Settings.Scope == ScopeMode.Specific)
        {
            var picked = new List<string>();
            foreach (var r in _treeRoots) r.CollectSelectedRoots(picked);
            Settings.WatchedPaths = picked;

            if (picked.Count == 0)
            {
                SetHint("Selection specifique vide : cochez au moins un disque ou un dossier.");
                return;
            }
        }

        Settings.TrackReads = OptReads.IsChecked == true;
        Settings.TrackWrites = OptWrites.IsChecked == true;
        Settings.TrackDeletes = OptDeletes.IsChecked == true;
        Settings.TrackRenames = OptRenames.IsChecked == true;
        Settings.EnableProcessAttribution = OptAttribution.IsChecked == true;
        Settings.IgnoreDirectoryEvents = OptSkipDirs.IsChecked == true;

        Settings.StartMinimized = OptStartMinimized.IsChecked == true;
        Settings.LogToFile = OptLogFile.IsChecked == true;
        Settings.AnimatedBackground = OptAnimation.IsChecked == true;
        Settings.IgnorePatterns = _ignorePatterns.ToList();

        if (int.TryParse(OptMaxEntries.Text, out var max) && max >= 500 && max <= 500_000)
            Settings.MaxActivityEntries = max;
        else
            OptMaxEntries.Text = Settings.MaxActivityEntries.ToString(CultureInfo.InvariantCulture);

        var wantStartup = OptStartup.IsChecked == true;
        if (wantStartup != StartupManager.IsEnabled())
        {
            StartupManager.SetEnabled(wantStartup);
            Settings.LaunchAtStartup = StartupManager.IsEnabled();
            OptStartup.IsChecked = Settings.LaunchAtStartup;
        }

        Waves.IsAnimated = Settings.AnimatedBackground;

        CurrentApp.ApplySettings();
        Store.PurgeIgnored(new IgnoreMatcher(Settings.IgnorePatterns));
        _activityView?.Refresh();

        MarkClean();
        SetHint($"Enregistre a {DateTime.Now:HH:mm:ss}. {Monitor.Status.Message}");
    }

    private void RevertSettings_Click(object sender, RoutedEventArgs e)
    {
        LoadSettingsIntoUi();
        SetHint("Modifications annulees.");
    }

    /// <summary>
    /// Reglage de depannage : applique et enregistre sur-le-champ, sans attendre
    /// « Appliquer », pour que l'utilisateur voie tout de suite si le noir disparait.
    /// </summary>
    private void SoftwareRender_Click(object sender, RoutedEventArgs e)
    {
        Settings.SoftwareRendering = OptSoftwareRender.IsChecked == true;
        ApplyRenderMode(Settings.SoftwareRendering);
        SettingsStore.Save(Settings);

        SetHint(Settings.SoftwareRendering
            ? "Rendu logiciel actif. Deplacez la fenetre pour verifier que le noir a disparu."
            : "Rendu materiel (GPU) restaure.");
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenPath(AppPaths.Root);

    // ==================================================================
    //  Menu contextuel du tableau d'activite
    // ==================================================================

    /// <summary>Action portee par une entree de menu : surveiller ou ignorer quoi.</summary>
    private sealed record MenuAction(bool Watch, TargetKind Kind, string Value);

    /// <summary>
    /// Reconstruit le menu a chaque ouverture : la chaine des dossiers parents et le nom
    /// du processus dependent de la ligne visee.
    /// </summary>
    private void ActivityMenu_Opened(object sender, RoutedEventArgs e)
    {
        ActivityMenu.Items.Clear();

        var entry = ActivityGrid.SelectedItem as FileActivityEntry;
        if (entry is null)
        {
            ActivityMenu.Items.Add(new MenuItem
            {
                Header = "Selectionnez d'abord une ligne",
                IsEnabled = false
            });
            return;
        }

        var multiple = ActivityGrid.SelectedItems.Count > 1;
        var suffix = multiple ? $" ({ActivityGrid.SelectedItems.Count} lignes)" : "";

        ActivityMenu.Items.Add(BuildActionSubmenu(entry, watch: true, suffix));
        ActivityMenu.Items.Add(BuildActionSubmenu(entry, watch: false, suffix));
        ActivityMenu.Items.Add(new Separator());

        ActivityMenu.Items.Add(Item("Retirer de la surveillance ciblee", UnwatchSelection_Click));
        ActivityMenu.Items.Add(new Separator());
        ActivityMenu.Items.Add(Item("Ouvrir l'emplacement", Reveal_Click));
        ActivityMenu.Items.Add(Item("Copier le chemin complet", CopyPath_Click));
    }

    private MenuItem BuildActionSubmenu(FileActivityEntry entry, bool watch, string suffix)
    {
        var root = new MenuItem { Header = watch ? "★  Surveiller" : "Ignorer" };

        root.Items.Add(Action($"Le fichier  —  {entry.FileName}{suffix}",
            new MenuAction(watch, TargetKind.File, entry.Path)));

        root.Items.Add(Action($"Le dossier  —  {Shorten(entry.Directory)}{suffix}",
            new MenuAction(watch, TargetKind.Folder, entry.Directory)));

        // Chaine des dossiers parents : pratique quand la ligne est profondement enfouie.
        var ancestors = Ancestors(entry.Directory).ToList();
        if (ancestors.Count > 0)
        {
            root.Items.Add(new Separator());
            root.Items.Add(new MenuItem { Header = "Dossiers parents", IsEnabled = false });

            foreach (var path in ancestors)
                root.Items.Add(Action("     " + Shorten(path),
                    new MenuAction(watch, TargetKind.Folder, path)));
        }

        root.Items.Add(new Separator());

        // Les processus ne sont connus qu'avec le moteur ETW.
        var processes = entry.ProcessBreakdown().Select(p => p.Process).ToList();
        if (processes.Count == 0)
        {
            root.Items.Add(new MenuItem
            {
                Header = "Processus indisponible sans le moteur ETW",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var proc in processes.Take(8))
                root.Items.Add(Action($"Le processus  —  {proc}",
                    new MenuAction(watch, TargetKind.Process, proc)));
        }

        return root;
    }

    /// <summary>Dossiers parents, du plus proche a la racine du volume.</summary>
    private static IEnumerable<string> Ancestors(string directory)
    {
        var current = directory;
        while (true)
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            yield return parent.Length == 2 && parent[1] == ':' ? parent + "\\" : parent;
            current = parent;
        }
    }

    /// <summary>Raccourcit un chemin long par le milieu, pour garder un menu lisible.</summary>
    private static string Shorten(string path, int max = 58)
    {
        if (path.Length <= max) return path;
        var keep = (max - 3) / 2;
        return path.Substring(0, keep) + "..." + path.Substring(path.Length - keep);
    }

    private static MenuItem Item(string header, RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += handler;
        return item;
    }

    private MenuItem Action(string header, MenuAction action)
    {
        var item = new MenuItem { Header = header, Tag = action };
        item.Click += MenuAction_Click;
        return item;
    }

    private void MenuAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: MenuAction action }) return;

        // Sur une selection multiple, l'action porte sur toutes les lignes pour les
        // fichiers ; pour un dossier ou un processus, la valeur choisie est explicite.
        switch (action.Kind)
        {
            case TargetKind.File when ActivityGrid.SelectedItems.Count > 1:
                var files = SelectedEntries().Select(f => f.Path).ToList();
                if (action.Watch) AddTargets(files, TargetKind.File);
                else AddIgnorePatterns(files, "fichier");
                break;

            case TargetKind.Process:
                if (action.Watch) AddTargets(new[] { action.Value }, TargetKind.Process);
                else IgnoreProcess(action.Value);
                break;

            default:
                if (action.Watch) AddTargets(new[] { action.Value }, action.Kind);
                else AddIgnorePatterns(new[] { action.Value },
                    action.Kind == TargetKind.File ? "fichier" : "dossier");
                break;
        }
    }

    /// <summary>Ecarte a la source tous les acces d'un processus.</summary>
    private void IgnoreProcess(string processName)
    {
        if (Settings.IgnoredProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
        {
            SetHint($"« {processName} » est deja ignore.");
            return;
        }

        Settings.IgnoredProcesses.Add(processName);
        _ignoredProcesses.Add(processName);

        // Une cible sur ce processus deviendrait contradictoire.
        var dropped = Settings.WatchTargets.RemoveAll(t => t.CoversProcess(processName));

        CurrentApp.ApplySettings();
        Store.Targets = new WatchTargetSet(Settings.WatchTargets);
        RefreshTargetRows();
        _activityView?.Refresh();

        var note = dropped > 0 ? " (retire aussi des cibles surveillees)" : "";
        AppLogger.Info($"Processus ignore : {processName}{note}");
        SetHint($"« {processName} » est desormais ignore{note}. Les lignes deja collectees sont conservees.");
    }

    private void RemoveIgnoredProcessItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string name }) return;

        Settings.IgnoredProcesses.RemoveAll(p =>
            string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
        _ignoredProcesses.Remove(name);

        CurrentApp.ApplySettings();
        AppLogger.Info($"Processus plus ignore : {name}");
        SetHint($"« {name} » n'est plus ignore.");
    }

    private void AddIgnoredProcess_Click(object sender, RoutedEventArgs e) => CommitProcessInput();

    private void ProcessInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitProcessInput();
        e.Handled = true;
    }

    private void CommitProcessInput()
    {
        var name = ProcessInput.Text.Trim();
        if (name.Length == 0) return;

        IgnoreProcess(name);
        ProcessInput.Clear();
    }

    // ==================================================================
    //  Surveillance ciblee
    // ==================================================================

    private void WatchFolder_Click(object sender, RoutedEventArgs e)
        => AddTargets(SelectedEntries().Select(f => f.Directory), TargetKind.Folder);

    private void AddTargetFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.FolderBrowserDialog
        {
            Description = "Dossier a suivre de pres",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        AddTargets(new[] { dlg.SelectedPath }, TargetKind.Folder);
    }

    private void AddTargetFile_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new Forms.OpenFileDialog
        {
            Title = "Fichier a suivre de pres",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        AddTargets(dlg.FileNames, TargetKind.File);
    }

    /// <summary>
    /// Epingle des cibles puis s'assure qu'elles sont reellement observees : « Surveiller »
    /// doit produire des donnees, pas seulement un filtre. On leve donc les exclusions qui
    /// les bloqueraient et on etend la portee de capture si besoin.
    /// </summary>
    private void AddTargets(IEnumerable<string> paths, TargetKind kind)
    {
        var added = new List<WatchTarget>();

        foreach (var raw in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = kind == TargetKind.Process ? raw.Trim() : Normalize(raw);
            if (path.Length == 0) continue;

            if (Settings.WatchTargets.Any(t =>
                    t.Kind == kind &&
                    string.Equals(Normalize(t.Path), Normalize(path), StringComparison.OrdinalIgnoreCase)))
                continue;

            // Surveiller un processus explicitement ignore serait contradictoire.
            if (kind == TargetKind.Process)
            {
                Settings.IgnoredProcesses.RemoveAll(p =>
                    string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                _ignoredProcesses.Remove(path);
            }

            var target = new WatchTarget { Path = path, Kind = kind };
            Settings.WatchTargets.Add(target);
            added.Add(target);
        }

        if (added.Count == 0)
        {
            SetTargetsHint(paths.Any()
                ? "Deja dans les cibles."
                : "Selectionnez d'abord une ou plusieurs lignes.");
            return;
        }

        var liftedIgnores = LiftConflictingIgnores(added);
        var scopeChanged = ExtendScope(added);

        PersistTargets();

        var notes = new List<string>();
        if (liftedIgnores > 0) notes.Add($"{liftedIgnores} exclusion(s) levee(s)");
        if (scopeChanged) notes.Add("portee de capture etendue");

        var suffix = notes.Count > 0 ? $" — {string.Join(", ", notes)}" : "";
        AppLogger.Info($"{added.Count} cible(s) ajoutee(s){suffix} : " +
                       string.Join(" | ", added.Select(t => t.Path)));
        SetTargetsHint($"{added.Count} cible(s) ajoutee(s){suffix}.");
    }

    /// <summary>
    /// Forme canonique d'un chemin : absolue, en noms longs, sans antislash final.
    /// Indispensable pour qu'une cible saisie en 8.3 (« PROGRA~1 ») rencontre les
    /// evenements rapportes en noms complets — sinon elle reste muette sans erreur.
    /// </summary>
    private static string Normalize(string path) => PathNormalizer.Normalize(path);

    /// <summary>
    /// Retire les motifs d'exclusion qui empecheraient de voir l'activite d'une cible.
    /// Sans cela, « Surveiller » resterait sans effet visible sur un dossier deja exclu.
    /// </summary>
    private int LiftConflictingIgnores(List<WatchTarget> targets)
    {
        // Une cible processus n'est pas concernee par les motifs de chemin.
        var pathTargets = targets.Where(t => !t.IsProcess).ToList();
        if (pathTargets.Count == 0) return 0;

        var conflicting = new List<string>();

        foreach (var pattern in Settings.IgnorePatterns)
        {
            var matcher = new IgnoreMatcher(new[] { pattern });

            var blocks = pathTargets.Any(t =>
                matcher.IsIgnored(t.Path) ||
                // Pour un dossier, un motif peut ne toucher que son contenu.
                (t.Kind == TargetKind.Folder && matcher.IsIgnored(Path.Combine(t.Path, "fichier.txt"))));

            if (blocks) conflicting.Add(pattern);
        }

        if (conflicting.Count == 0) return 0;

        foreach (var p in conflicting)
        {
            Settings.IgnorePatterns.Remove(p);
            _ignorePatterns.Remove(p);
            AppLogger.Info($"Exclusion levee car elle bloquait une cible surveillee : {p}");
        }
        return conflicting.Count;
    }

    /// <summary>Etend la portee de capture pour couvrir les cibles qui n'y sont pas.</summary>
    private bool ExtendScope(List<WatchTarget> targets)
    {
        // Toute la machine est deja observee : rien a faire.
        if (Settings.Scope == ScopeMode.All) return false;

        var missing = targets
            // Un processus n'a pas de place dans la portee, qui est une liste de chemins.
            .Where(t => !t.IsProcess && !IsWithinScope(t.Path))
            .Select(t => t.Kind == TargetKind.Folder ? t.Path : Path.GetDirectoryName(t.Path) ?? t.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 0) return false;

        foreach (var p in missing)
            if (!Settings.WatchedPaths.Contains(p, StringComparer.OrdinalIgnoreCase))
                Settings.WatchedPaths.Add(p);

        // « Rien » deviendrait contradictoire avec une demande explicite de surveillance.
        if (Settings.Scope == ScopeMode.None)
        {
            Settings.Scope = ScopeMode.Specific;
            AppLogger.Info("Portee « Rien » remplacee par « Selection specifique » : une cible a ete ajoutee");
        }

        return true;
    }

    private bool IsWithinScope(string path)
    {
        foreach (var root in Settings.WatchedPaths)
        {
            var r = Normalize(root);
            if (r.Length == 0) continue;
            if (!path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.EndsWith('\\') || path.Length == r.Length || path[r.Length] == '\\') return true;
        }
        return false;
    }

    private void RemoveTarget_Click(object sender, RoutedEventArgs e)
    {
        if (TargetList.SelectedItem is not TargetRow row)
        {
            SetTargetsHint("Selectionnez d'abord une cible dans la liste.");
            return;
        }

        Settings.WatchTargets.RemoveAll(t =>
            string.Equals(t.Path, row.Path, StringComparison.OrdinalIgnoreCase));
        PersistTargets();
        AppLogger.Info($"Cible retiree : {row.Path}");
        SetTargetsHint("Cible retiree.");
    }

    /// <summary>Retire des cibles les lignes selectionnees dans le tableau d'activite.</summary>
    private void UnwatchSelection_Click(object sender, RoutedEventArgs e)
    {
        var paths = SelectedEntries()
            .SelectMany(f => new[] { Normalize(f.Path), Normalize(f.Directory) })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removed = Settings.WatchTargets.RemoveAll(t => paths.Contains(Normalize(t.Path)));
        if (removed == 0)
        {
            SetTargetsHint("Aucune cible ne correspond a la selection.");
            return;
        }

        PersistTargets();
        AppLogger.Info($"{removed} cible(s) retiree(s) depuis le tableau d'activite");
        SetTargetsHint($"{removed} cible(s) retiree(s).");
    }

    private void ClearTargets_Click(object sender, RoutedEventArgs e)
    {
        if (Settings.WatchTargets.Count == 0) return;

        var answer = MessageBox.Show(
            $"Retirer les {Settings.WatchTargets.Count} cible(s) de la surveillance ciblee ?\n\n" +
            "La portee de capture et les exclusions ne sont pas modifiees.",
            "Watcher", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK) return;

        Settings.WatchTargets.Clear();
        PersistTargets();
        SetTargetsHint("Toutes les cibles ont ete retirees.");
    }

    /// <summary>Enregistre, republie l'ensemble des cibles et relance la capture si besoin.</summary>
    private void PersistTargets()
    {
        Store.Targets = new WatchTargetSet(Settings.WatchTargets);
        CurrentApp.ApplySettings();

        // La liste des exclusions affichee dans Parametres peut avoir change.
        _ignorePatterns.Clear();
        foreach (var p in Settings.IgnorePatterns) _ignorePatterns.Add(p);

        RefreshTargetRows();
        _activityView?.Refresh();
        _targetView?.Refresh();
    }

    private void SetTargetsHint(string text) => TargetsHint.Text = text;

    private void BuildTargetView()
    {
        _targetView = new ListCollectionView(Store.Files) { Filter = TargetFilter };
        _targetView.SortDescriptions.Add(new SortDescription(nameof(FileActivityEntry.Count),
            ListSortDirection.Descending));
        TargetGrid.ItemsSource = _targetView;
        TargetList.ItemsSource = _targetRows;
    }

    private bool TargetFilter(object item)
    {
        if (item is not FileActivityEntry f) return false;

        // Aucune cible selectionnee : on montre l'activite de toutes les cibles.
        if (TargetList.SelectedItem is TargetRow row)
            return row.Target.IsProcess
                ? f.UsedBy(row.Target.Path)
                : row.Target.CoversPath(f.Path);

        return f.IsTargeted;
    }

    private void TargetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TargetActivityTitle.Text = TargetList.SelectedItem is TargetRow row
            ? $"Activite de « {row.Name} »"
            : "Activite des cibles";
        _targetView?.Refresh();
    }

    /// <summary>Recalcule les compteurs des cibles. Appele seulement quand l'onglet est visible.</summary>
    private void RefreshTargetRows()
    {
        var targets = Settings.WatchTargets;
        var stats = Store.ComputeTargetStats(targets);

        var selectedPath = (TargetList.SelectedItem as TargetRow)?.Path;

        _targetRows.Clear();
        for (var i = 0; i < targets.Count; i++)
            _targetRows.Add(new TargetRow(targets[i], stats[i]));

        if (selectedPath is not null)
            TargetList.SelectedItem = _targetRows.FirstOrDefault(r =>
                string.Equals(r.Path, selectedPath, StringComparison.OrdinalIgnoreCase));

        TargetsSubtitle.Text = targets.Count == 0
            ? "Aucune cible. Ajoutez un dossier ici, ou faites un clic droit sur une ligne de l'onglet « Activite des fichiers » puis « Surveiller »."
            : $"{targets.Count} cible(s) epinglee(s). Ajouter une cible garantit qu'elle entre dans la portee de capture.";
    }

    private void ExportTargets_Click(object sender, RoutedEventArgs e)
        => ExportView(_targetView, "cibles");

    private void RevealTarget_Click(object sender, RoutedEventArgs e)
        => RevealEntry(TargetGrid.SelectedItem as FileActivityEntry);

    private void CopyTargetPath_Click(object sender, RoutedEventArgs e)
    {
        if (TargetGrid.SelectedItem is not FileActivityEntry f) return;
        try { Clipboard.SetText(f.Path); }
        catch (Exception ex) { AppLogger.Warn($"Copie impossible : {ex.Message}"); }
    }

    /// <summary>Ligne de la liste des cibles : la cible et ses compteurs, prets a afficher.</summary>
    public sealed class TargetRow
    {
        public TargetRow(WatchTarget target, WatchTargetStats stats)
        {
            Target = target;
            Stats = stats;
        }

        public WatchTarget Target { get; }
        public WatchTargetStats Stats { get; }

        public string Path => Target.Path;
        public string Name => Target.DisplayName;
        public string Kind => Target.KindLabel;
        public string Accesses => Stats.AccessLabel;
        public string Files => Stats.FileLabel;
        public string LastSeen => Stats.LastSeenLabel;
        public string TopProcess => Stats.TopProcess == "—" ? "accedant inconnu" : $"surtout {Stats.TopProcess}";
    }

    // ==================================================================
    //  Journal
    // ==================================================================

    private void BuildLogView()
    {
        _logView = new ListCollectionView(_logLines)
        {
            Filter = o => o is LogLine l && l.Level >= _minLogLevel
        };
        LogView.ItemsSource = _logView;

        foreach (var label in new[] { "Tout (debug inclus)", "Information et plus", "Avertissements et erreurs", "Erreurs uniquement" })
            LogLevelFilter.Items.Add(label);
        LogLevelFilter.SelectedIndex = 1;
    }

    private void OnLogged(LogLine line)
    {
        // AppLogger publie depuis le thread qui journalise : on repasse par l'IHM.
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => OnLogged(line));
            return;
        }

        _logLines.Add(line);
        while (_logLines.Count > 5_000) _logLines.RemoveAt(0);

        if (LogAutoScroll.IsChecked == true && Pages.SelectedIndex == 4 && _logView is not null)
        {
            var last = _logView.Cast<LogLine>().LastOrDefault();
            if (last is not null) LogView.ScrollIntoView(last);
        }
    }

    private void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        _minLogLevel = LogLevelFilter.SelectedIndex switch
        {
            0 => LogLevel.Debug,
            1 => LogLevel.Info,
            2 => LogLevel.Warn,
            _ => LogLevel.Error
        };
        _logView?.Refresh();
    }

    private void ClearLogView_Click(object sender, RoutedEventArgs e) => _logLines.Clear();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e)
    {
        var file = AppLogger.CurrentLogFile;
        if (!File.Exists(file))
        {
            SetHint("Aucun fichier journal pour aujourd'hui.");
            OpenPath(AppPaths.LogDirectory);
            return;
        }
        OpenPath(file);
    }

    private static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { AppLogger.Warn($"Ouverture de « {path} » impossible : {ex.Message}"); }
    }

    // ==================================================================
    //  Etat de la surveillance
    // ==================================================================

    private void OnStatusChanged(MonitorStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnStatusChanged(status));
            return;
        }
        RefreshStatusUi(status);
    }

    private void RefreshStatusUi(MonitorStatus status)
    {
        MonitorSwitch.IsChecked = status.Running;
        ToggleLabel.Text = status.Running ? "Active" : "Inactive";
        TitleStatus.Text = status.Message;

        EngineLabel.Text = status.Engine switch
        {
            MonitorEngine.Etw => "Moteur : ETW noyau — lectures et processus visibles",
            MonitorEngine.FileSystemWatcher => "Moteur : FileSystemWatcher — modifications seules",
            _ => "Moteur : aucun"
        };
    }

    private void MonitorSwitch_Click(object sender, RoutedEventArgs e)
    {
        // L'etat reel vient du service : on laisse OnStatusChanged repositionner l'interrupteur.
        CurrentApp.ToggleMonitoring();
    }

    private void ApplyElevationState()
    {
        var elevated = Elevation.IsElevated;
        ElevationBadge.Visibility = elevated ? Visibility.Visible : Visibility.Collapsed;
        ElevateButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
        ElevationCard.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "Watcher va se relancer avec les droits administrateur pour activer le moteur ETW " +
            "(lectures de fichiers et processus responsable).\n\n" +
            "L'activite deja collectee sera perdue. Continuer ?",
            "Watcher", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        if (Elevation.RestartElevated())
            CurrentApp.ExitApplication();
        else
            SetHint("Elevation refusee : le moteur ETW reste indisponible.");
    }

    // ==================================================================
    //  Chrome de la fenetre
    // ==================================================================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (Pages is null) return;
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index)) return;

        // Quitter l'onglet Parametres sans avoir applique : on le signale plutot que
        // de laisser croire que les changements sont pris en compte.
        if (Pages.SelectedIndex == 3 && index != 3 && _settingsDirty)
            AppLogger.Warn("Onglet Parametres quitte avec des modifications non appliquees");

        Pages.SelectedIndex = index;

        // Rafraichissement immediat a l'arrivee sur un onglet. Sans cela, les compteurs
        // resteraient ceux du dernier passage du minuteur — jusqu'a deux secondes de
        // valeurs perimees, et « 0 acces » a la premiere ouverture.
        switch (index)
        {
            case 1:
                _activityView?.Refresh();
                break;
            case 2:
                RefreshTargetRows();
                _targetView?.Refresh();
                break;
        }
    }

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Maximize_Click(sender, e);
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { /* bouton relache pendant le glissement */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        // Le carre du glyphe rapetisse quand la fenetre est agrandie : c'est le
        // repere visuel habituel pour « restaurer ».
        var maximized = WindowState == WindowState.Maximized;
        MaxGlyph.Width = MaxGlyph.Height = maximized ? 7 : 9;
        MaxButton.ToolTip = maximized ? "Restaurer" : "Agrandir";
    }

    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
}
