using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Surfaces;
using Avalonia.Logging;
using Avalonia.MicroCom;
using Avalonia.OpenGL.Egl;
using Avalonia.Rendering;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT.Composition;

internal class WinUiCompositorConnection : IRenderTimer, IRenderTimerWithImmediateTick, Win32.IWindowsSurfaceFactory
{
    private const uint ImmediateTickMessage = (uint)UnmanagedMethods.WindowsMessage.WM_APP + 1;
    private static readonly TimeSpan MinimumImmediateTickInterval = TimeSpan.FromMilliseconds(8);
    private readonly WinUiCompositionShared _shared;
    private readonly AutoResetEvent _wakeEvent = new(false);
    private IntPtr _messageWindow;
    // A normal commit callback and the private wake message race to consume the same restart generation.
    // This prevents a late private message from injecting an extra unpaced tick after DWM already woke the loop.
    private int _immediateTickGeneration;
    private int _pendingImmediateTickGeneration;
    private int _queuedImmediateTickGeneration;
    private int _delayedImmediateTickScheduled;
    private long _lastServicedTickTimestamp;
    private volatile bool _stopped = true;
    private TickRegistration? _tickRegistration;

    private sealed class TickRegistration
    {
        public TickRegistration(Action<TimeSpan> tick, int generation)
        {
            Tick = tick;
            Generation = generation;
        }

        public Action<TimeSpan> Tick { get; }
        public int Generation { get; }
    }

    public bool RunsInBackground => true;

    public Action<TimeSpan>? Tick
    {
        get => GetRunningTickRegistration()?.Tick;
        set
        {
            if (value != null)
            {
                if (!_stopped)
                {
                    var current = Volatile.Read(ref _tickRegistration);
                    if (current != null)
                        Volatile.Write(ref _tickRegistration, new TickRegistration(value, current.Generation));
                    return;
                }

                int generation;
                do
                {
                    generation = Interlocked.Increment(ref _immediateTickGeneration);
                } while (generation == 0);
                Volatile.Write(ref _pendingImmediateTickGeneration, generation);
                Volatile.Write(ref _tickRegistration, new TickRegistration(value, generation));
                _stopped = false;
                _wakeEvent.Set();
                PostPendingImmediateTick();
            }
            else
            {
                _stopped = true;
                Volatile.Write(ref _tickRegistration, null);
                Interlocked.Exchange(ref _pendingImmediateTickGeneration, 0);
            }
        }
    }

    public WinUiCompositorConnection()
    {
        using var compositor = NativeWinRTMethods.CreateInstance<ICompositor>("Windows.UI.Composition.Compositor");
        /*
        var levels = new[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1 };
        DirectXUnmanagedMethods.D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero, 0, levels, (uint)levels.Length, 7, out var pD3dDevice, out var level, null);

        var d3dDevice = MicroComRuntime.CreateProxyFor<IUnknown>(pD3dDevice, true);

        var compositionDevice = compositor.QueryInterface<ICompositorInterop>().CreateGraphicsDevice(d3dDevice);
        var surf = compositionDevice.CreateDrawingSurface(new UnmanagedMethods.SIZE_F { X = 100, Y = 100 },
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        var surfInterop = surf.QueryInterface<ICompositionDrawingSurfaceInterop>();
        var IID_ID3D11Texture2D = Guid.Parse("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        void* texture = null;
        surfInterop.BeginDraw(null, &IID_ID3D11Texture2D, &texture);
        */

        _shared = new WinUiCompositionShared(compositor);
    }

    private static bool TryCreateAndRegisterCore()
    {
        var tcs = new TaskCompletionSource<bool>();
        var th = new Thread(() =>
        {
            WinUiCompositorConnection connect;
            try
            {
                NativeWinRTMethods.CreateDispatcherQueueController(new NativeWinRTMethods.DispatcherQueueOptions
                {
                    apartmentType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE,
                    dwSize = Marshal.SizeOf<NativeWinRTMethods.DispatcherQueueOptions>(),
                    threadType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT
                });
                connect = new WinUiCompositorConnection();
                AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant(connect);
                AvaloniaLocator.CurrentMutable.Bind<IRenderTimer>().ToConstant(connect);
                AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(connect));
                tcs.SetResult(true);

            }
            catch (Exception e)
            {
                tcs.SetException(e);
                return;
            }

            connect.RunLoop();
        })
        {
            IsBackground = true,
            Name = "DwmRenderTimerLoop"
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task.Result;
    }

    private class RunLoopHandler : CallbackBase, IAsyncActionCompletedHandler
    {
        private readonly WinUiCompositorConnection _parent;
        private readonly Stopwatch _st = Stopwatch.StartNew();
        private TimeSpan? _commitDueAt;
        private IAsyncAction? _currentCommit;
        private bool _commitCompleted;

        public RunLoopHandler(WinUiCompositorConnection parent)
        {
            _parent = parent;
        }

        public void Invoke(IAsyncAction? asyncInfo, AsyncStatus asyncStatus)
        {
            lock (_parent._shared.SyncRoot)
            {
                if (_currentCommit == null || _currentCommit.GetNativeIntPtr() != asyncInfo.GetNativeIntPtr())
                    return;
                OnCommitCompleted();
            }
        }

        private void OnCommitCompleted()
        {
            Debug.Assert(Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should be held");

            _currentCommit?.Dispose();
            _currentCommit = null;
            if (_parent.GetRunningTickRegistration() is { } registration)
            {
                _parent.TryConsumePendingImmediateTick(registration.Generation);
                _parent.MarkTickServiced();
                registration.Tick(_st.Elapsed);
            }
            ScheduleNextCommit();
            _commitCompleted = true;
        }

        // This method should be called outside the shared lock, as it might wait for a long time.
        public void OnAfterMessageWithoutLock()
        {
            Debug.Assert(!Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should NOT be held");

            if (!_commitCompleted)
                return;

            _commitCompleted = false;

            if (_parent._stopped)
            {
                _parent._wakeEvent.WaitOne();
                // Reset the expected commit callback time since we've paused
                // the render loop due to app being idle
                _commitDueAt = _st.Elapsed + TimeSpan.FromSeconds(1);
            }
        }

        private void ScheduleNextCommit()
        {
            Debug.Assert(Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should be held");

            _commitDueAt = _st.Elapsed + TimeSpan.FromSeconds(1);
            _currentCommit = _parent._shared.Compositor5.RequestCommitAsync();
            _currentCommit.SetCompleted(this);
        }

        public void WatchDog()
        {
            lock (_parent._shared.SyncRoot)
            {
                // This is a workaround for a nasty WinUI composition API bug that prevents
                // RequestCommitAsync to ever complete after D3D device loss event with some systems
                // (A notable example is after pause/resume in Parallels Desktop)
                // We check if we haven't got a commit completion callback for a second
                // And forcefully trigger the next one, which makes the entire thing to unstuck

                if (_st.Elapsed > _commitDueAt && _currentCommit != null)
                {
                    Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this,
                        "windows::UI::Composition::ICompositor5.RequestCommitAsync timed out, force-triggering next tick");
                    try
                    {
                        _currentCommit?.GetResults();
                    }
                    catch (Exception e)
                    {
                        Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this,
                            "ICompositor5::RequestCommitAsync failed: {HR}, {ERR}", e.HResult, e.ToString());
                    }

                    OnCommitCompleted();
                }
            }
        }

        public void Start()
        {
            lock (_parent._shared.SyncRoot)
                ScheduleNextCommit();
        }

        public void ImmediateTick(int generation)
        {
            lock (_parent._shared.SyncRoot)
            {
                var registration = _parent.GetRunningTickRegistration();
                if (registration?.Generation == generation &&
                    _parent.GetRemainingImmediateTickDelay() == TimeSpan.Zero &&
                    _parent.TryConsumePendingImmediateTick(generation))
                {
                    _parent.MarkTickServiced();
                    registration.Tick(_st.Elapsed);
                }
            }
        }
    }

    private void RunLoop()
    {
        var cts = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            cts.Cancel();

        var handler = new RunLoopHandler(this);
        handler.Start();

        const int watchDogIntervalInMs = 1000;

        using var dw = new SimpleWindow((hwnd, msg, w, l) =>
        {
            if (msg == ImmediateTickMessage)
            {
                var generation = unchecked((int)w.ToInt64());
                handler.ImmediateTick(generation);
                CompleteImmediateTickMessage(generation);
                return IntPtr.Zero;
            }

            if (msg == (uint)UnmanagedMethods.WindowsMessage.WM_TIMER)
            {
                handler.WatchDog();
                UnmanagedMethods.SetTimer(hwnd, IntPtr.Zero, watchDogIntervalInMs, null);
            }
            return UnmanagedMethods.DefWindowProc(hwnd, msg, w, l);
        });
        Interlocked.Exchange(ref _messageWindow, dw.Handle);
        PostPendingImmediateTick();
        UnmanagedMethods.SetTimer(dw.Handle, IntPtr.Zero, watchDogIntervalInMs, null);

        // Warning: the completion callback (RunLoopHandler.Invoke) from ICompositor5.RequestCommitAsync()
        // is called in DispatchMessage() on Windows 10, but in GetMessage() on Windows 11!
        // Be careful when changing the scope of the shared lock.

        var result = 0;
        while (!cts.IsCancellationRequested
               && (result = UnmanagedMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0)) > 0)
        {
            UnmanagedMethods.DispatchMessage(ref msg);
            handler.OnAfterMessageWithoutLock();
        }

        if (result < 0)
        {
            Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                ?.Log(this, "Unmanaged error in {0}. Error Code: {1}", nameof(RunLoop), Marshal.GetLastWin32Error());
        }

        Interlocked.Exchange(ref _messageWindow, IntPtr.Zero);
        Interlocked.Exchange(ref _pendingImmediateTickGeneration, 0);
        Interlocked.Exchange(ref _queuedImmediateTickGeneration, 0);
        Interlocked.Exchange(ref _delayedImmediateTickScheduled, 0);
    }

    private TickRegistration? GetRunningTickRegistration()
    {
        if (_stopped)
            return null;

        return Volatile.Read(ref _tickRegistration);
    }

    private bool TryConsumePendingImmediateTick(int generation)
    {
        return generation != 0 &&
               Interlocked.CompareExchange(ref _pendingImmediateTickGeneration, 0, generation) == generation;
    }

    private void MarkTickServiced()
    {
        Interlocked.Exchange(ref _lastServicedTickTimestamp, Stopwatch.GetTimestamp());
    }

    private TimeSpan GetRemainingImmediateTickDelay()
    {
        var lastTickTimestamp = Interlocked.Read(ref _lastServicedTickTimestamp);
        if (lastTickTimestamp == 0)
            return TimeSpan.Zero;

        var elapsed = Stopwatch.GetElapsedTime(lastTickTimestamp);
        return elapsed < MinimumImmediateTickInterval
            ? MinimumImmediateTickInterval - elapsed
            : TimeSpan.Zero;
    }

    private void CompleteImmediateTickMessage(int generation)
    {
        Interlocked.CompareExchange(ref _queuedImmediateTickGeneration, 0, generation);

        if (GetRunningTickRegistration() == null)
        {
            TryConsumePendingImmediateTick(generation);
            return;
        }

        PostPendingImmediateTick();
    }

    public void RequestImmediateTick()
    {
        var registration = GetRunningTickRegistration();
        if (registration == null)
            return;

        Interlocked.CompareExchange(
            ref _pendingImmediateTickGeneration,
            registration.Generation,
            comparand: 0);

        if (GetRunningTickRegistration()?.Generation != registration.Generation)
        {
            TryConsumePendingImmediateTick(registration.Generation);
            return;
        }

        PostPendingImmediateTick();
    }

    private void PostPendingImmediateTick()
    {
        var generation = Volatile.Read(ref _pendingImmediateTickGeneration);
        if (generation == 0)
            return;

        var delay = GetRemainingImmediateTickDelay();
        if (delay > TimeSpan.Zero)
        {
            ScheduleDelayedImmediateTick(delay);
            return;
        }

        var window = Interlocked.CompareExchange(ref _messageWindow, IntPtr.Zero, IntPtr.Zero);
        if (window == IntPtr.Zero ||
            Interlocked.CompareExchange(ref _queuedImmediateTickGeneration, generation, 0) != 0)
            return;

        if (!UnmanagedMethods.PostMessage(window, ImmediateTickMessage, new IntPtr(generation), IntPtr.Zero))
            Interlocked.CompareExchange(ref _queuedImmediateTickGeneration, 0, generation);
    }

    private void ScheduleDelayedImmediateTick(TimeSpan delay)
    {
        if (Interlocked.CompareExchange(ref _delayedImmediateTickScheduled, 1, 0) != 0)
            return;

        var delayMilliseconds = Math.Max(1, (int)Math.Ceiling(delay.TotalMilliseconds));
        _ = DelayAndPostPendingImmediateTickAsync(delayMilliseconds);
    }

    private async Task DelayAndPostPendingImmediateTickAsync(int delayMilliseconds)
    {
        await Task.Delay(delayMilliseconds).ConfigureAwait(false);
        Interlocked.Exchange(ref _delayedImmediateTickScheduled, 0);
        PostPendingImmediateTick();
    }

    public static bool IsSupported()
    {
        return Win32Platform.WindowsVersion >= WinUiCompositionShared.MinWinCompositionVersion;
    }

    public static bool TryCreateAndRegister()
    {
        if (IsSupported())
        {
            try
            {
                TryCreateAndRegisterCore();
                return true;
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                    ?.Log(null, "Unable to initialize WinUI compositor: {0}", e);
            }
        }
        else
        {
            var osVersionNotice =
                $"Windows {WinUiCompositionShared.MinWinCompositionVersion} is required. Your machine has Windows {Win32Platform.WindowsVersion} installed.";

            Logger.TryGet(LogEventLevel.Warning, "WinUIComposition")?.Log(null,
                $"Unable to initialize WinUI compositor: {osVersionNotice}");
        }

        return false;
    }

    public bool RequiresNoRedirectionBitmap => true;

    public IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info)
    {
        if (info is not IWinUiCompositionWindowInfo compositionInfo)
            throw new InvalidOperationException($"{nameof(WinUiCompositedWindowSurface)} requires {nameof(IWinUiCompositionWindowInfo)}.");

        return new WinUiCompositedWindowSurface(_shared, info, compositionInfo);
    }
}
