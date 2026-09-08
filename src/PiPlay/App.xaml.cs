using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PiPlay.Models;
using PiPlay.Services;
using PiPlay.Theme;

namespace PiPlay;

/// <summary>
/// Application entry point. Handles native help before normal startup (REQ-APP-02). Normal
/// launches enforce single-instance ownership (REQ-APP-01) before application UI: a second
/// launch hands its URL to the running instance and exits rather than contending for the shared
/// WebView2 user-data folder. Owns the app-scoped shared WebView2 environment so the Source Window
/// and Popout Player share one session.
/// </summary>
public partial class App : Application
{
    // Per-session single-instance identity (the Local\ mutex namespace is scoped to the Windows logon
    // session), scoped per channel so a Stable copy and the dev app each stay single-instance without
    // colliding (the Default mutex keeps the original .v1 identity). The pipe adds the numeric session
    // id because named pipes use a machine-wide namespace while cross-session windows cannot activate
    // one another. This keeps the rendezvous boundary aligned with the existing primary-election
    // boundary; each elected primary still protects its channel's WebView2 user-data ownership.
    private static string IdentitySuffix =>
        AppChannel.Current == PiPlayChannel.Default ? "v1" : AppChannel.Name;
    private static string MutexName => $@"Local\PiPlay.SingleInstance.{IdentitySuffix}";
    private static readonly int SessionId = GetCurrentSessionId();
    private static string PipeName => SingleInstancePipePolicy.BuildPipeName(
        IdentitySuffix, SessionId);

    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCts;
    private Task? _pipeWorker;
    private readonly DispatcherFaultPolicy _dispatcherFaults = new();
    private bool _shuttingDown;
    private int _handoffDispatchGeneration;

    private static int GetCurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    /// <summary>Shared WebView2 environment, created lazily during the Source Window's browser init.</summary>
    public WebViewEnvironmentService WebViewEnvironment { get; } = new();

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        var request = StartupArgumentPolicy.Parse(e.Args);
        StartupDispatcher.Dispatch(
            request,
            ShowNativeHelp,
            Shutdown,
            launchUrl => StartNormal(e, launchUrl));
    }

    private static void ShowNativeHelp(string helpText)
    {
        MessageBox.Show(
            helpText,
            "PiPlay Help",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void StartNormal(StartupEventArgs e, string? launchUrl)
    {
        Log.Init();
        Log.Info("PiPlay starting.");

        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew && !TryHandOffOrBecomePrimary(launchUrl))
        {
            // The running instance owns the request (or refused it visibly). Skip base.OnStartup so
            // no window is created; just leave.
            return;
        }

        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        StartPipeServer();

        // Load once, then use the same sanitized object both for the resources parsed by the first
        // window and for MainWindow itself. A failure here must never block startup.
        AppSettings? bootSettings = null;
        try
        {
            bootSettings = new SettingsService().Load();
            ThemeResourceApplier.Apply(Resources, bootSettings.Theme, bootSettings.Player);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to apply theme resources at startup; using defaults.", ex);
        }

        var main = bootSettings is null ? new MainWindow() : new MainWindow(bootSettings);
        MainWindow = main;
        main.Show();

        if (!string.IsNullOrEmpty(launchUrl))
            main.NavigateTo(launchUrl);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shuttingDown = true;
        // PP-05 ownership order: stop taking hand-offs, let the pipe worker wind down (bounded; it
        // only ever waits on a cancelled dispatch), drain the log, and release the session mutex
        // LAST so a replacement instance never starts while this one is still writing. The mutex
        // does not prove WebView2 released the profile; it proves this process is done with it.
        SingleInstanceHandoffPolicy.RunShutdownSequence(
            stopAcceptingRequests: () => _pipeCts?.Cancel(),
            waitForWorker: () =>
            {
                var worker = _pipeWorker;
                if (worker is not null && !worker.Wait(SingleInstanceHandoffPolicy.WorkerShutdownWait))
                    Log.Warn("Single-instance pipe worker did not stop within the shutdown wait.");
            },
            drainLog: () =>
            {
                Log.Info("PiPlay exiting.");
                Log.Shutdown();   // drain the writer; queued entries are lost without this
            },
            releaseMutex: () =>
            {
                try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { /* not owned */ }
                _mutex?.Dispose();
            },
            onStepFailed: (step, ex) => Log.Error($"Shutdown step '{step}' failed.", ex));
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Recover cleanly (Q-6) without stacking modals on a repeating fault, and without claiming
        // recovery from one the process cannot survive. DispatcherFaultPolicy owns that decision.
        var decision = _dispatcherFaults.Evaluate(
            e.Exception, _shuttingDown || Dispatcher.HasShutdownStarted);
        Log.Error(
            $"Unhandled UI exception (handled={decision.Handled}, dialog={decision.ShowDialog}, " +
            $"signature={decision.Signature}).",
            e.Exception);

        e.Handled = decision.Handled;
        if (!decision.ShowDialog) return;

        try
        {
            // Modal, so the dispatcher keeps pumping: faults raised behind this dialog re-enter
            // this handler on the same thread and the policy coalesces them.
            MessageBox.Show(
                decision.Handled
                    ? "PiPlay hit an unexpected problem. The details were written to the log and the app will keep running."
                    : "PiPlay hit a problem it cannot recover from. The details were written to the log and the app will close.",
                "PiPlay", MessageBoxButton.OK,
                decision.Handled ? MessageBoxImage.Warning : MessageBoxImage.Error);
        }
        catch (Exception dialogFailure)
        {
            // An out-of-memory fault can take the dialog down with it; never mask the original.
            Log.Error("Failed to show the unhandled UI exception dialog.", dialogFailure);
        }
        finally
        {
            _dispatcherFaults.DialogClosed();
        }
    }

    /// <summary>
    /// Compatibility seam for command-line parsing tests. StartupArgumentPolicy owns help
    /// precedence and the shared supported-YouTube-target boundary.
    /// </summary>
    internal static string? ExtractUrlArg(string[] args) =>
        StartupArgumentPolicy.Parse(args).LaunchUrl;

    // --- Single-instance hand-off over a named pipe ---

    private void StartPipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        _pipeWorker = Task.Run(() => SingleInstancePipePolicy.RunAsync(
            attemptAsync: ServeOnePipeConnectionAsync,
            delayAsync: Task.Delay,
            onFirstFailure: ex => Log.Error(
                "Single-instance pipe server error; retries are delayed and repeats suppressed until recovery.",
                ex),
            onRecovery: failures => Log.Info(
                $"Single-instance pipe server recovered after {failures} failed attempt(s)."),
            token), token);
    }

    /// <summary>
    /// One hand-off exchange (spec 11, PP-05): the client writes its payload as one line, the
    /// Source Window applies it on the UI thread, and the answer goes back as one line. The
    /// payload read keeps the 2 s bound; the dispatch keeps its own so a wedged UI thread answers
    /// Unavailable instead of holding the sender forever.
    /// </summary>
    private Task ServeOnePipeConnectionAsync(CancellationToken token) =>
        SingleInstancePipeTransport.ServeOneAsync(
            PipeName,
            DispatchHandoffAsync,
            onAckUndeliverable: (ack, ex) =>
                // The sender gave up first; the request itself was already applied or refused.
                Log.Warn($"Hand-off acknowledgement ({ack}) could not be delivered: {ex.Message}"),
            token);

    private async Task<HandoffAck> DispatchHandoffAsync(string? url, CancellationToken token)
    {
        if (_shuttingDown || Dispatcher.HasShutdownStarted) return HandoffAck.Unavailable;
        var generation = SingleInstanceHandoffPolicy.BeginDispatch(ref _handoffDispatchGeneration);
        try
        {
            var operation = Dispatcher.InvokeAsync(() =>
            {
                // Re-checked on the UI thread: shutdown can begin between the pipe read and this
                // callback, and a closing Source must not be handed new work. A timed-out wait
                // expires the generation so a late pump cannot apply a request already refused.
                if (!SingleInstanceHandoffPolicy.IsCurrentDispatch(generation, _handoffDispatchGeneration))
                    return HandoffAck.Unavailable;
                if (_shuttingDown || Dispatcher.HasShutdownStarted) return HandoffAck.Unavailable;
                if (MainWindow is not MainWindow main) return HandoffAck.Unavailable;
                var decision = main.ActivateFromSecondInstance(url);
                return SingleInstanceHandoffPolicy.AckFor(decision, shuttingDown: false);
            }, DispatcherPriority.Normal, token);
            return await SingleInstanceHandoffPolicy.AwaitDispatchAsync(
                operation.Task,
                () =>
                {
                    SingleInstanceHandoffPolicy.ExpireDispatch(ref _handoffDispatchGeneration);
                    operation.Abort();
                },
                token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SingleInstanceHandoffPolicy.ExpireDispatch(ref _handoffDispatchGeneration);
            Log.Warn($"Hand-off could not be applied: {ex.GetType().Name}.");
            return HandoffAck.Unavailable;
        }
    }

    /// <summary>
    /// The second-launch path when the session mutex is already owned. Returns true only when this
    /// process should continue as the primary (it WON the mutex after nobody answered). Every other
    /// outcome ends here: a quiet exit after acceptance, or a visible message when the running
    /// instance refused the link or did not answer. The owned mutex is never bypassed.
    /// </summary>
    private bool TryHandOffOrBecomePrimary(string? launchUrl)
    {
        Log.Info("Another instance is already running; handing off.");
        HandoffOutcome outcome;
        try
        {
            // Run the exchange off the dispatcher thread: OnStartup has a synchronization context,
            // and blocking on continuations posted back to it would deadlock.
            outcome = Task.Run(() => SendToExistingInstanceAsync(launchUrl)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error("Hand-off to the existing instance failed.", ex);
            outcome = HandoffOutcome.Unreachable;
        }

        Log.Info($"Hand-off outcome: {outcome}.");
        switch (SingleInstanceHandoffPolicy.DecideSenderAction(outcome))
        {
            case HandoffSenderAction.Exit:
                Shutdown(0);
                return false;

            case HandoffSenderAction.ReportLinkRejected:
                MessageBox.Show(
                    "PiPlay is already running, but it couldn't open that link. Paste it into the running window instead.",
                    "PiPlay", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(0);
                return false;

            default:
                if (TryTakeOverSessionMutex())
                {
                    Log.Info("No running instance answered; this launch won the session mutex and continues as the primary.");
                    return true;
                }

                MessageBox.Show(
                    "PiPlay is already running but did not respond. Close it, or try again in a moment.",
                    "PiPlay", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return false;
        }
    }

    /// <summary>
    /// A replacement starts only after winning the channel/session mutex (REQ-APP-01). A primary
    /// that is closing releases it within its bounded shutdown; one that died abandons it, which
    /// still grants ownership. A live-but-wedged primary keeps it, and this returns false.
    /// </summary>
    private bool TryTakeOverSessionMutex()
    {
        if (_mutex is null) return false;
        try
        {
            return _mutex.WaitOne(SingleInstanceHandoffPolicy.ReplacementElectionTimeout);
        }
        catch (AbandonedMutexException)
        {
            Log.Warn("The previous PiPlay instance abandoned the session mutex; taking it over.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Waiting for the session mutex failed.", ex);
            return false;
        }
    }

    private static Task<HandoffOutcome> SendToExistingInstanceAsync(string? url) =>
        SingleInstanceHandoffPolicy.SendAsync(
            exchangeAsync: (_, token) => ExchangeWithExistingInstanceAsync(url, token),
            delayAsync: Task.Delay,
            onAttemptFailed: (attempt, ex) => Log.Warn($"Hand-off attempt {attempt} failed: {ex.GetType().Name}: {ex.Message}"),
            cancellationToken: CancellationToken.None);

    private static Task<string?> ExchangeWithExistingInstanceAsync(string? url, CancellationToken token) =>
        SingleInstancePipeTransport.ExchangeAsync(PipeName, url, token);
}
