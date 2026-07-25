using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

namespace Watcher.Core;

/// <summary>
/// Moteur complet : session ETW sur le fournisseur noyau FileIO. C'est la seule voie
/// qui expose a la fois les lectures et le processus a l'origine de chaque acces.
/// Exige les droits administrateur (SeSystemProfilePrivilege).
/// </summary>
public sealed class EtwEventSource : IEventSource
{
    private readonly bool _reads, _writes, _deletes, _renames;
    private readonly int _selfPid = Environment.ProcessId;

    private TraceEventSession? _session;
    private Thread? _pump;
    private Action<FileEvent>? _onEvent;
    private volatile bool _disposed;

    public MonitorEngine Engine => MonitorEngine.Etw;

    /// <summary>Nombre d'evenements bruts vus par le moteur, filtrage inclus.</summary>
    public long RawEventCount;

    public EtwEventSource(AppSettings settings)
    {
        _reads = settings.TrackReads;
        _writes = settings.TrackWrites;
        _deletes = settings.TrackDeletes;
        _renames = settings.TrackRenames;
    }

    public void Start(IReadOnlyList<string> roots, Action<FileEvent> onEvent)
    {
        if (!Elevation.IsElevated)
            throw new UnauthorizedAccessException(
                "La session ETW noyau exige les droits administrateur.");

        _onEvent = onEvent;
        DevicePathResolver.Refresh();

        // Une session noyau survit au processus qui l'a creee : apres un arret brutal,
        // la notre est encore active et empecherait toute nouvelle capture. On la ferme.
        StopStaleSession();

        // Le fournisseur noyau impose ce nom de session : une seule peut exister a la fois.
        _session = new TraceEventSession(KernelTraceEventParser.KernelSessionName)
        {
            StopOnDispose = true,
            BufferSizeMB = 128
        };

        var keywords = KernelTraceEventParser.Keywords.FileIOInit
                       | KernelTraceEventParser.Keywords.FileIO
                       | KernelTraceEventParser.Keywords.Process;

        try
        {
            _session.EnableKernelProvider(keywords);
        }
        catch (Exception ex)
        {
            _session.Dispose();
            _session = null;
            throw new InvalidOperationException(
                "Ouverture de la session ETW noyau impossible. Une autre application de trace " +
                $"(Perfmon, WPR, Process Monitor...) la detient peut-etre deja. Detail : {ex.Message}", ex);
        }

        var k = _session.Source.Kernel;

        if (_reads) k.FileIORead += d => Emit(d, FileAction.Read);
        if (_writes)
        {
            k.FileIOWrite += d => Emit(d, FileAction.Write);
            k.FileIOCreate += d => Emit(d, FileAction.Create);
        }
        if (_deletes) k.FileIODelete += d => Emit(d, FileAction.Delete);
        if (_renames) k.FileIORename += d => Emit(d, FileAction.Rename);

        // Source.Process() est bloquant : il tourne pour toute la duree de la session.
        _pump = new Thread(Pump) { IsBackground = true, Name = "Watcher.Etw" };
        _pump.Start();

        AppLogger.Info("Moteur ETW noyau demarre (lectures et processus disponibles)");
    }

    private static void StopStaleSession()
    {
        try
        {
            var stale = TraceEventSession.GetActiveSession(KernelTraceEventParser.KernelSessionName);
            if (stale is null) return;

            AppLogger.Warn("Session ETW noyau residuelle detectee : fermeture avant redemarrage");
            stale.Stop();
            stale.Dispose();
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"Fermeture de la session residuelle sans effet : {ex.Message}");
        }
    }

    private void Pump()
    {
        try
        {
            _session?.Source.Process();
        }
        catch (Exception ex) when (!_disposed)
        {
            AppLogger.Error($"La session ETW s'est interrompue : {ex.Message}");
        }
    }

    private void Emit(TraceEvent data, FileAction action)
    {
        if (_disposed) return;

        // Filtre le moins couteux d'abord : nos propres acces ne doivent jamais
        // etre rapportes, sinon l'ecriture du journal s'auto-alimente.
        if (data.ProcessID == _selfPid) return;

        Interlocked.Increment(ref RawEventCount);

        var native = GetFileName(data);
        if (string.IsNullOrEmpty(native)) return;

        var path = DevicePathResolver.ToWin32Path(native);
        if (path is null) return; // pipe nomme, volume non monte, chemin non resolu

        var name = data.ProcessName;
        _onEvent?.Invoke(new FileEvent(
            path,
            action,
            data.TimeStamp,
            data.ProcessID,
            string.IsNullOrEmpty(name) ? null : name));
    }

    private static string? GetFileName(TraceEvent data) => data switch
    {
        FileIOReadWriteTraceData rw => rw.FileName,
        FileIOCreateTraceData c => c.FileName,
        FileIOInfoTraceData i => i.FileName,
        _ => null
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _session?.Source.StopProcessing(); } catch { }
        try { _session?.Stop(); } catch { }
        try { _session?.Dispose(); } catch { }
        _session = null;

        try { _pump?.Join(3_000); } catch { }
        _pump = null;

        AppLogger.Info("Moteur ETW noyau arrete");
    }
}
