using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Watcher.Core;

namespace Watcher;

/// <summary>Icone et menu de la zone de notification. Le clic gauche ouvre la fenetre.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _statusItem;
    private readonly Forms.ToolStripMenuItem _toggleItem;
    private readonly Drawing.Icon? _appIcon;

    public event Action? OpenRequested;
    public event Action? ToggleRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _appIcon = LoadIcon();

        _statusItem = new Forms.ToolStripMenuItem("Surveillance arretee") { Enabled = false };
        _toggleItem = new Forms.ToolStripMenuItem("Activer la surveillance");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();

        // Entree par defaut du menu : mise en gras comme le veut la convention Windows.
        var open = new Forms.ToolStripMenuItem("Ouvrir Watcher");
        open.Click += (_, _) => OpenRequested?.Invoke();
        try
        {
            var baseFont = Drawing.SystemFonts.MenuFont ?? Drawing.SystemFonts.DefaultFont;
            if (baseFont is not null)
                open.Font = new Drawing.Font(baseFont, Drawing.FontStyle.Bold);
        }
        catch { /* police systeme indisponible : l'entree reste en style normal */ }

        var logs = new Forms.ToolStripMenuItem("Ouvrir le dossier des journaux");
        logs.Click += (_, _) => OpenLogFolder();

        var exit = new Forms.ToolStripMenuItem("Quitter");
        exit.Click += (_, _) => ExitRequested?.Invoke();

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            _statusItem,
            new Forms.ToolStripSeparator(),
            open,
            _toggleItem,
            new Forms.ToolStripSeparator(),
            logs,
            new Forms.ToolStripSeparator(),
            exit
        });

        _icon = new Forms.NotifyIcon
        {
            Icon = _appIcon ?? Drawing.SystemIcons.Application,
            Text = "Watcher — surveillance d'acces disque",
            Visible = true,
            ContextMenuStrip = menu
        };

        // Un seul abonnement au clic gauche. Ajouter DoubleClick en plus ferait remonter
        // trois demandes d'ouverture pour un double-clic (MouseClick, MouseClick, DoubleClick).
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
    }

    private static Drawing.Icon? LoadIcon()
    {
        try
        {
            // L'icone est embarquee comme ressource WPF : on la lit depuis le pack URI.
            var uri = new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute);
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) return new Drawing.Icon(stream);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Icone embarquee illisible : {ex.Message}");
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) return Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch { }

        return null;
    }

    public void UpdateStatus(MonitorStatus status)
    {
        var engine = status.Engine switch
        {
            MonitorEngine.Etw => "ETW noyau",
            MonitorEngine.FileSystemWatcher => "FileSystemWatcher",
            _ => "—"
        };

        _statusItem.Text = status.Running
            ? $"Surveillance active ({engine})"
            : "Surveillance arretee";

        _toggleItem.Text = status.Running ? "Arreter la surveillance" : "Activer la surveillance";

        // L'info-bulle systeme est limitee a 63 caracteres.
        var tip = status.Running ? $"Watcher — active ({engine})" : "Watcher — en veille";
        _icon.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    public void Notify(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(3_000);
        }
        catch { }
    }

    private static void OpenLogFolder()
    {
        try
        {
            AppPaths.EnsureCreated();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.LogDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Ouverture du dossier des journaux impossible : {ex.Message}");
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon?.Dispose();
    }
}
