using System.Management;
using System.Threading.Channels;

namespace ParentalControl.Service;

/// <summary>A process start from Windows. Only the ids are used; the process name of the event is untrusted and not read.</summary>
internal readonly record struct ProcessStartEvent(int Pid, int ParentPid, int SessionId);

/// <summary>
/// Detects process starts with the WMI event class <c>Win32_ProcessStartTrace</c> (needs administrator rights;
/// the service runs as LocalSystem). The event is a trigger only. The WMI thread drops an event of a session that
/// is not an active session, and writes the others into a bounded channel; it never waits for the database or a
/// WinAPI call. If the watcher stops with an error, <see cref="EnsureRunning"/> starts it again at the next tick.
/// While it does not run, the 60-second tick of the category enforcer finds the processes.
/// </summary>
internal sealed class ProcessStartWatcher : IDisposable
{
    private const string Query = "SELECT ProcessID, ParentProcessID, SessionID FROM Win32_ProcessStartTrace";

    private readonly Func<int, bool> _isWatchedSession;
    private readonly Serilog.ILogger _logger;
    private readonly Channel<ProcessStartEvent> _channel = Channel.CreateBounded<ProcessStartEvent>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly object _gate = new();
    private ManagementEventWatcher? _watcher;
    private volatile bool _running;
    private volatile bool _disposed;
    private int _readErrorLogged;

    /// <param name="isWatchedSession">True for a session id that can belong to a Restricted User. It runs on the WMI thread and must be fast.</param>
    public ProcessStartWatcher(Func<int, bool> isWatchedSession, Serilog.ILogger logger)
    {
        _isWatchedSession = isWatchedSession;
        _logger = logger;
    }

    public ChannelReader<ProcessStartEvent> Events => _channel.Reader;

    /// <summary>Starts the watcher if it does not run. Returns true if it runs after the call.</summary>
    public bool EnsureRunning()
    {
        lock (_gate)
        {
            if (_disposed)
                return false;
            if (_running)
                return true;

            try
            {
                _watcher?.Dispose();
                _watcher = new ManagementEventWatcher(new ManagementScope(@"\\.\root\cimv2"), new WqlEventQuery(Query));
                _watcher.EventArrived += OnEventArrived;
                _watcher.Stopped += OnStopped;
                _watcher.Start();
                _running = true;
                Interlocked.Exchange(ref _readErrorLogged, 0);
                _logger.Information("Process start watcher started (WMI Win32_ProcessStartTrace)");
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Process start watcher could not start; the 60-second check applies until the next try");
                _watcher?.Dispose();
                _watcher = null;
                _running = false;
            }
            return _running;
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            var ev = e.NewEvent;
            var sessionId = (int)Convert.ToInt64(ev["SessionID"]);
            if (!_isWatchedSession(sessionId))
                return;
            var pid = (int)Convert.ToInt64(ev["ProcessID"]);
            var parentPid = (int)Convert.ToInt64(ev["ParentProcessID"]);
            _channel.Writer.TryWrite(new ProcessStartEvent(pid, parentPid, sessionId));
        }
        catch (Exception ex)
        {
            // Log the first error of each run as a warning: an error here drops every event, and only the
            // 60-second check then finds the processes.
            if (Interlocked.Exchange(ref _readErrorLogged, 1) == 0)
                _logger.Warning(ex, "Could not read a process start event");
            else
                _logger.Debug(ex, "Could not read a process start event");
        }
    }

    // No lock here: Stop() in Dispose can raise this event while Dispose holds the lock.
    private void OnStopped(object sender, StoppedEventArgs e)
    {
        if (!_running)
            return;
        _running = false;
        if (!_disposed)
            _logger.Warning("Process start watcher stopped ({Status}); it starts again at the next tick", e.Status);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _running = false;
            try
            {
                _watcher?.Stop();
            }
            catch (Exception)
            {
                // The watcher can be stopped already.
            }
            _watcher?.Dispose();
            _watcher = null;
        }
        _channel.Writer.TryComplete();
    }
}
