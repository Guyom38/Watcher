using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Watcher.Core;

namespace Watcher;

public partial class App : Application
{
    private const string SingleInstanceMutex = @"Local\Watcher.SingleInstance";
    private const string ShowWindowEvent = @"Local\Watcher.ShowWindow";

    private Mutex? _instanceLock;
    private EventWaitHandle? _showSignal;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private CancelEventHandler? _hideOnClose;
    private bool _openingWindow;
    private int _windowBuilds;

    public static MonitorService Monitor { get; } = new();
    public static ActivityStore Store { get; } = new();
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Instance unique : une seconde execution reveille la fenetre de la premiere et sort.
        _instanceLock = new Mutex(initiallyOwned: true, SingleInstanceMutex, out var isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ShowWindowEvent).Set(); } catch { }
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        AppLogger.Start();
        AppLogger.Purge();

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLogger.Error($"Exception non geree : {args.ExceptionObject}");

        var firstRun = !File.Exists(AppPaths.SettingsFile);
        Settings = SettingsStore.Load();
        // Premier lancement : on materialise la configuration par defaut sur le disque
        // pour qu'elle soit visible et modifiable sans passer par l'interface.
        if (firstRun) SettingsStore.Save(Settings);

        AppLogger.FileEnabled = Settings.LogToFile;
        Store.MaxEntries = Settings.MaxActivityEntries;
        Store.LiveCapacity = Settings.LiveFeedSize;
        Store.SessionStart = DateTime.Now;

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowMainWindow;
        _tray.ToggleRequested += ToggleMonitoring;
        _tray.ExitRequested += ExitApplication;
        Monitor.StatusChanged += s => _tray?.UpdateStatus(s);
        _tray.UpdateStatus(Monitor.Status);

        ListenForShowSignal();

        if (Settings.MonitoringEnabled)
            Monitor.Start(Settings);

        var startHidden = Settings.StartMinimized || e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);
        if (!startHidden) ShowMainWindow();
        else _tray.Notify("Watcher", "Actif dans la zone de notification.");
    }

    /// <summary>Reveille la fenetre quand une seconde instance est lancee (double-clic sur l'exe).</summary>
    private void ListenForShowSignal()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEvent);
        var handle = _showSignal;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    handle.WaitOne();
                    // ShowMainWindow se replie lui-meme sur le thread d'interface.
                    ShowMainWindow();
                }
                catch { return; }
            }
        }) { IsBackground = true, Name = "Watcher.ShowSignal" };
        thread.Start();
    }

    /// <summary>
    /// Demande l'ouverture de la fenetre. Le travail reel est toujours differe via le
    /// Dispatcher : les evenements de la zone de notification arrivent a l'interieur d'un
    /// rappel Win32 (NotifyIcon.WmMouseDown). Executer Window.Show() a cet endroit le
    /// rendait reentrant, car Show() pompe des messages pendant qu'il cree sa fenetre :
    /// un clic en attente relancait Show() sur la meme fenetre et WPF echouait avec
    /// « Le Visual racine d'un VisualTarget ne peut pas avoir de parent », laissant une
    /// fenetre Win32 sans contenu attache — donc entierement noire.
    /// </summary>
    public void ShowMainWindow()
        => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(ShowMainWindowCore));

    private void ShowMainWindowCore()
    {
        // Deuxieme rempart : meme differe, deux demandes rapprochees ne doivent pas
        // se chevaucher.
        if (_openingWindow)
        {
            AppLogger.Debug("Demande d'ouverture ignoree : ouverture deja en cours");
            return;
        }

        _openingWindow = true;
        try
        {
            if (_window is null)
            {
                var window = new MainWindow();
                _hideOnClose = (_, args) =>
                {
                    // La croix replie dans la zone de notification : l'app reste un service de fond.
                    args.Cancel = true;
                    window.Hide();
                };
                window.Closing += _hideOnClose;
                _window = window;

                _windowBuilds++;
                AppLogger.Info($"Fenetre principale creee (creation n°{_windowBuilds}) — " +
                               $"style={window.WindowStyle}, {window.Width}x{window.Height}");
            }

            // Show() ne doit etre appele que sur une fenetre qui n'est pas deja affichee.
            if (!_window.IsVisible)
                _window.Show();

            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;

            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();
        }
        catch (Exception ex)
        {
            // Une fenetre dont l'affichage a echoue est inutilisable : on la ferme pour
            // de bon plutot que de laisser un cadre vide a l'ecran, et on repart de zero
            // a la prochaine demande.
            AppLogger.Error($"Ouverture de la fenetre impossible : {ex}");
            DiscardBrokenWindow();
        }
        finally
        {
            _openingWindow = false;
        }
    }

    private void DiscardBrokenWindow()
    {
        var broken = _window;
        _window = null;
        if (broken is null) return;

        try
        {
            // Le gestionnaire Closing annule la fermeture : il faut le detacher pour
            // que Close() aboutisse et libere la fenetre Win32 restee vide.
            if (_hideOnClose is not null) broken.Closing -= _hideOnClose;
            _hideOnClose = null;
            broken.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Nettoyage de la fenetre defaillante partiel : {ex.Message}");
        }
    }

    public void ToggleMonitoring()
    {
        Settings.MonitoringEnabled = !Settings.MonitoringEnabled;
        if (Settings.MonitoringEnabled) Monitor.Start(Settings);
        else Monitor.Stop();
        SettingsStore.Save(Settings);
    }

    public void ApplySettings()
    {
        SettingsStore.Save(Settings);
        AppLogger.FileEnabled = Settings.LogToFile;
        Store.MaxEntries = Settings.MaxActivityEntries;
        Store.LiveCapacity = Settings.LiveFeedSize;

        if (Settings.MonitoringEnabled) Monitor.Start(Settings);
        else Monitor.Stop();
    }

    public void ExitApplication()
    {
        SettingsStore.Save(Settings);
        Monitor.Dispose();
        _tray?.Dispose();
        AppLogger.Shutdown();

        try { _instanceLock?.ReleaseMutex(); } catch { }
        _instanceLock?.Dispose();

        Shutdown();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.Error($"Erreur dans l'interface : {e.Exception}");
        MessageBox.Show(
            $"Une erreur inattendue est survenue :\n\n{e.Exception.Message}\n\n" +
            $"Le detail a ete ecrit dans :\n{AppLogger.CurrentLogFile}",
            "Watcher", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
