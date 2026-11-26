using System.Collections.Concurrent;
using ParentalControl.Core.Data;
using ParentalControl.Core.Logging;
using ParentalControl.Core.Platform;
using Serilog;
using ILogger = Serilog.ILogger;
using Serilog.Core;
using Serilog.Events;

namespace ParentalControl.Service;

/// <summary>
/// The start gate of the service (require-admin-sign-in, CB1, CB2, S5). The host starts at once, so the service reports
/// "running" to the Service Control Manager before any long work. The data folder step then runs in
/// <see cref="StartupWorker"/>. Each worker that uses the database waits for <see cref="WaitReadyAsync"/>: the gate opens
/// only after the data folder step, the open and migration of the database, and the log file all succeed.
/// </summary>
public sealed class ServiceStartup
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DeferredSink _sink = new();

    public ServiceStartup()
    {
        Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(_sink).CreateLogger();
    }

    /// <summary>
    /// The logger of all services. It keeps the first events in memory until the log file in the secured data folder
    /// exists. In a lockdown, it has no file: the service writes to the event log only.
    /// </summary>
    public ILogger Logger { get; }

    public bool IsReady => _ready.Task.IsCompleted;

    public Task WaitReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    /// <summary>
    /// Opens and migrates the database in the folder of the data folder step, and creates the log file (CB2). Returns
    /// null after a success, else the error. An error leaves the gate closed: the caller runs the lockdown.
    /// </summary>
    public string? TryOpen(DataFolderPreparation prep, Func<string, ILogger>? createFileLogger = null)
    {
        try
        {
            DatabaseManager.InitializeSecured(prep.Folder!, prep.Result, secured: prep.Warning is null);
            var fileLogger = (createFileLogger ?? (dir => LoggingConfig.CreateLogger("Service", dir)))(DatabaseManager.LogsDirectory);
            _sink.Attach(fileLogger);
            return null;
        }
        catch (Exception ex)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>Opens the gate. The workers start to enforce.</summary>
    public void Open() => _ready.TrySetResult();

    /// <summary>
    /// A Serilog sink that keeps up to <see cref="MaxBuffered"/> events in memory until <see cref="Attach"/> gives the
    /// real logger, then forwards each event.
    /// </summary>
    private sealed class DeferredSink : ILogEventSink
    {
        public const int MaxBuffered = 2000;
        private readonly ConcurrentQueue<LogEvent> _buffer = new();
        private readonly object _gate = new();
        private ILogger? _target;

        public void Emit(LogEvent logEvent)
        {
            var target = Volatile.Read(ref _target);
            if (target is not null)
            {
                target.Write(logEvent);
                return;
            }
            lock (_gate)
            {
                if (_target is not null)
                {
                    _target.Write(logEvent);
                    return;
                }
                _buffer.Enqueue(logEvent);
                while (_buffer.Count > MaxBuffered && _buffer.TryDequeue(out _)) { }
            }
        }

        public void Attach(ILogger target)
        {
            lock (_gate)
            {
                while (_buffer.TryDequeue(out var e))
                    target.Write(e);
                Volatile.Write(ref _target, target);
            }
        }
    }
}
