using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Logging;
using Avalonia.Rendering;
using Avalonia.Win32.Interop;
using Microsoft.Win32.SafeHandles;

namespace Avalonia.Win32.WinRT.Composition;

/// <summary>
/// Drives WinUI rendering from the Windows compositor clock with a bounded,
/// phase-aligned fallback for environments where that clock is unavailable.
/// </summary>
internal sealed class WinUiRenderTimer : IRenderTimer, IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint Synchronize = 0x00100000;
    private const uint TimerModifyState = 0x00000002;
    private const uint StatusTimeout = 0x00000102;
    private const uint StatusGraphicsPresentOccluded = 0xC01E0006;
    private const int ClockWaitHandleCount = 1;
    private const int ClockRetryDelayMaxSeconds = 30;
    internal const int ClockOccludedRetryMilliseconds = 1000;
    private const int TimingPollMilliseconds = 500;
    private const int TimingCorrectionMilliseconds = 1000;
    private const int PhaseErrorSampleCount = 5;

    private readonly AutoResetEvent _wakeEvent = new(false);
    private readonly object _lifecycleLock = new();
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly Action<Action> _queueTick;
    private readonly Action _dispatchTick;
    private int _displayFps;
    private volatile bool _stopped = true;
    private volatile bool _shutdownRequested;
    private bool _disposed;
    private int _activationPending;
    private int _clockResetGeneration;
    private int _tickGeneration;
    private int _pendingTickGeneration;
    private long _pendingTickTime;
    private Thread? _thread;
    private Action<TimeSpan>? _tick;

    public WinUiRenderTimer(int displayFps, Action<Action>? queueTick = null)
    {
        _displayFps = Math.Max(1, displayFps);
        _queueTick = queueTick ?? (action => action());
        _dispatchTick = DispatchTick;
    }

    internal int DisplayFps
    {
        get => Volatile.Read(ref _displayFps);
        set
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                    return;

                int normalizedValue = Math.Max(1, value);
                if (normalizedValue == Volatile.Read(ref _displayFps))
                    return;

                Volatile.Write(ref _displayFps, normalizedValue);
                RequestClockRefresh();
            }
        }
    }

    public Action<TimeSpan>? Tick
    {
        get => Volatile.Read(ref _tick);
        set
        {
            lock (_lifecycleLock)
            {
                if (_disposed)
                    return;

                bool wasStopped = _stopped;
                Interlocked.Increment(ref _tickGeneration);
                Volatile.Write(ref _tick, value);
                _stopped = value == null;
                if (value == null)
                    Interlocked.Exchange(ref _activationPending, 0);
                else if (wasStopped)
                    Interlocked.Exchange(ref _activationPending, 1);

                if (value != null && _thread == null)
                {
                    _thread = new Thread(RunLoop)
                    {
                        IsBackground = true,
                        Name = "WinUIRenderTimerLoop"
                    };
                    _thread.SetApartmentState(ApartmentState.MTA);
                    _thread.Start();
                }
                else
                {
                    _wakeEvent.Set();
                }
            }
        }
    }

    public bool RunsInBackground => true;

    internal void RequestClockRefresh()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            Interlocked.Increment(ref _clockResetGeneration);
            _wakeEvent.Set();
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr timerAttributes, string? timerName, uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(IntPtr timer, ref long dueTime, int period,
        IntPtr completionRoutine, IntPtr completionArgument, IntPtr wakeContext, uint tolerableDelay);

    [DllImport("dcomp.dll")]
    private static extern uint DCompositionWaitForCompositorClock(
        uint count,
        IntPtr[] handles,
        uint timeoutInMs);

    [DllImport("dcomp.dll")]
    private static extern int DCompositionGetFrameId(
        CompositionFrameIdType frameIdType,
        out ulong frameId);

    [DllImport("dcomp.dll")]
    private static extern int DCompositionGetStatistics(
        ulong frameId,
        out CompositionFrameStats frameStats,
        uint targetIdCount,
        IntPtr targetIds,
        IntPtr actualTargetIdCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetCompositionTimingInfo(
        IntPtr window,
        ref DwmTimingInfo timingInfo);

    private void RunLoop()
    {
        using SafeWaitHandle? pacingTimer = TryCreatePacingTimer();
        var wakeSafeHandle = _wakeEvent.SafeWaitHandle;
        bool wakeHandleRef = false;
        bool pacingTimerHandleRef = false;

        try
        {
            wakeSafeHandle.DangerousAddRef(ref wakeHandleRef);
            pacingTimer?.DangerousAddRef(ref pacingTimerHandleRef);
            IntPtr wakeHandle = wakeSafeHandle.DangerousGetHandle();
            IntPtr[] timerWaitHandles =
            [
                wakeHandle,
                pacingTimerHandleRef ? pacingTimer!.DangerousGetHandle() : IntPtr.Zero
            ];
            IntPtr[] compositorClockHandles = [wakeHandle];

            var phaseGrid = new PhaseGrid();
            int observedResetGeneration = Volatile.Read(ref _clockResetGeneration);
            int consecutiveClockFailures = 0;
            int consecutiveSuspiciousTicks = 0;
            long retryCompositorClockAt = 0;
            long nextTimingPollAt = 0;
            long nextTimingCorrectionAt = 0;
            long nominalNextTick = Stopwatch.GetTimestamp();
            bool compositorClockSupported = IsCompositorClockCandidate();

            if (!_stopped)
                InvokeActivationTick();

            while (!_shutdownRequested)
            {
                if (_stopped)
                {
                    _wakeEvent.WaitOne();
                    if (_shutdownRequested)
                        break;

                    int wakeGeneration = Volatile.Read(ref _clockResetGeneration);
                    bool resetFailures = wakeGeneration != observedResetGeneration;
                    ResetClockState(
                        phaseGrid,
                        resetFailures,
                        wakeGeneration,
                        ref observedResetGeneration,
                        ref consecutiveClockFailures,
                        ref consecutiveSuspiciousTicks,
                        ref retryCompositorClockAt,
                        ref nextTimingPollAt,
                        ref nextTimingCorrectionAt,
                        ref nominalNextTick);
                    if (resetFailures)
                        compositorClockSupported = IsCompositorClockCandidate();
                    if (!_stopped)
                        InvokeActivationTick();
                    continue;
                }

                int resetGeneration = Volatile.Read(ref _clockResetGeneration);
                if (resetGeneration != observedResetGeneration)
                {
                    ResetClockState(
                        phaseGrid,
                        resetFailures: true,
                        resetGeneration,
                        ref observedResetGeneration,
                        ref consecutiveClockFailures,
                        ref consecutiveSuspiciousTicks,
                        ref retryCompositorClockAt,
                        ref nextTimingPollAt,
                        ref nextTimingCorrectionAt,
                        ref nominalNextTick);
                    compositorClockSupported = IsCompositorClockCandidate();
                }

                long now = Stopwatch.GetTimestamp();
                if (now >= nextTimingPollAt)
                {
                    if (TryGetCompositionTiming(out long samplePhase, out long sampleInterval))
                    {
                        phaseGrid.Update(
                            samplePhase,
                            sampleInterval,
                            now >= nextTimingCorrectionAt);
                    }

                    nextTimingPollAt = now + MillisecondsToTimestamp(TimingPollMilliseconds);
                    if (now >= nextTimingCorrectionAt)
                        nextTimingCorrectionAt = now + MillisecondsToTimestamp(TimingCorrectionMilliseconds);
                }

                long interval = phaseGrid.IsInitialized
                    ? phaseGrid.Interval
                    : CalculateIntervalTicks(Volatile.Read(ref _displayFps));

                if (compositorClockSupported && now >= retryCompositorClockAt)
                {
                    uint timeoutMs = CalculateClockTimeoutMilliseconds(interval);
                    bool hadFrameId = TryGetFrameId(
                        CompositionFrameIdType.Created,
                        out ulong frameIdBeforeWait);
                    long waitStarted = Stopwatch.GetTimestamp();
                    uint waitResult;
                    try
                    {
                        waitResult = DCompositionWaitForCompositorClock(
                            ClockWaitHandleCount,
                            compositorClockHandles,
                            timeoutMs);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        compositorClockSupported = false;
                        waitResult = uint.MaxValue;
                    }

                    var resultKind = ClassifyCompositorWaitResult(waitResult, ClockWaitHandleCount);
                    if (resultKind == CompositorWaitResult.Wake)
                    {
                        int wakeGeneration = Volatile.Read(ref _clockResetGeneration);
                        bool resetFailures = wakeGeneration != observedResetGeneration;
                        ResetClockState(
                            phaseGrid,
                            resetFailures,
                            wakeGeneration,
                            ref observedResetGeneration,
                            ref consecutiveClockFailures,
                            ref consecutiveSuspiciousTicks,
                            ref retryCompositorClockAt,
                            ref nextTimingPollAt,
                            ref nextTimingCorrectionAt,
                            ref nominalNextTick);
                        if (resetFailures)
                            compositorClockSupported = IsCompositorClockCandidate();
                        if (!_stopped)
                            InvokeActivationTick();
                        continue;
                    }

                    long waitElapsed = Stopwatch.GetTimestamp() - waitStarted;
                    if (resultKind == CompositorWaitResult.Tick)
                    {
                        long suspiciousThreshold = Math.Max(1, interval / 4);
                        bool frameAdvanced =
                            !hadFrameId ||
                            (TryGetFrameId(
                                CompositionFrameIdType.Created,
                                out ulong frameIdAfterWait) &&
                             frameIdAfterWait != frameIdBeforeWait);
                        if (waitElapsed < suspiciousThreshold && !frameAdvanced)
                            consecutiveSuspiciousTicks++;
                        else
                            consecutiveSuspiciousTicks = 0;

                        if (consecutiveSuspiciousTicks < 3)
                        {
                            consecutiveClockFailures = 0;
                            retryCompositorClockAt = 0;
                            InvokeTick();
                            continue;
                        }

                        resultKind = CompositorWaitResult.Failure;
                    }

                    if (resultKind == CompositorWaitResult.Occluded)
                    {
                        consecutiveClockFailures = 0;
                        retryCompositorClockAt =
                            Stopwatch.GetTimestamp() +
                            MillisecondsToTimestamp(ClockOccludedRetryMilliseconds);
                        bool wakeSignaled = _wakeEvent.WaitOne(ClockOccludedRetryMilliseconds);
                        if (ShouldTickAfterOccludedWait(wakeSignaled, _stopped))
                            InvokeTick();
                        continue;
                    }

                    consecutiveClockFailures++;
                    retryCompositorClockAt =
                        Stopwatch.GetTimestamp() +
                        GetRetryDelayTicks(consecutiveClockFailures, resultKind);

                    if (waitElapsed >= interval)
                    {
                        InvokeTick();
                        continue;
                    }
                }

                now = Stopwatch.GetTimestamp();
                long nextTick;
                if (phaseGrid.IsInitialized)
                {
                    nextTick = phaseGrid.GetNextTick(now);
                }
                else
                {
                    nominalNextTick += interval;
                    if (nominalNextTick <= now - interval)
                        nominalNextTick = now + interval;
                    nextTick = nominalNextTick;
                }

                long remaining = nextTick - now;
                if (remaining > 0)
                {
                    try
                    {
                        int waitResult = WaitForPacing(timerWaitHandles, pacingTimerHandleRef, remaining);
                        if (waitResult == 0)
                        {
                            int wakeGeneration = Volatile.Read(ref _clockResetGeneration);
                            bool resetFailures = wakeGeneration != observedResetGeneration;
                            ResetClockState(
                                phaseGrid,
                                resetFailures,
                                wakeGeneration,
                                ref observedResetGeneration,
                                ref consecutiveClockFailures,
                                ref consecutiveSuspiciousTicks,
                                ref retryCompositorClockAt,
                                ref nextTimingPollAt,
                                ref nextTimingCorrectionAt,
                                ref nominalNextTick);
                            if (resetFailures)
                                compositorClockSupported = IsCompositorClockCandidate();
                            if (!_stopped)
                                InvokeActivationTick();
                            continue;
                        }
                    }
                    catch (Exception e)
                    {
                        Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                            ?.Log(this, "WinUI render timer wait failed: {0}", e);
                        nominalNextTick = Stopwatch.GetTimestamp();
                    }
                }

                InvokeTick();
            }
        }
        finally
        {
            if (pacingTimerHandleRef)
                pacingTimer!.DangerousRelease();
            if (wakeHandleRef)
                wakeSafeHandle.DangerousRelease();
        }
    }

    private void InvokeTick()
    {
        if (Volatile.Read(ref _tick) == null)
            return;

        Interlocked.Exchange(ref _pendingTickTime, _stopwatch.Elapsed.Ticks);
        Volatile.Write(ref _pendingTickGeneration, Volatile.Read(ref _tickGeneration));
        _queueTick(_dispatchTick);
    }

    private void DispatchTick()
    {
        int generation = Volatile.Read(ref _pendingTickGeneration);
        if (generation != Volatile.Read(ref _tickGeneration) ||
            Volatile.Read(ref _tick) is not { } tick)
        {
            return;
        }

        tick(TimeSpan.FromTicks(Interlocked.Read(ref _pendingTickTime)));
    }

    private void InvokeActivationTick()
    {
        if (Interlocked.Exchange(ref _activationPending, 0) != 0)
            InvokeTick();
    }

    private void ResetClockState(
        PhaseGrid phaseGrid,
        bool resetFailures,
        int resetGeneration,
        ref int observedResetGeneration,
        ref int consecutiveClockFailures,
        ref int consecutiveSuspiciousTicks,
        ref long retryCompositorClockAt,
        ref long nextTimingPollAt,
        ref long nextTimingCorrectionAt,
        ref long nominalNextTick)
    {
        phaseGrid.Reset();
        observedResetGeneration = resetGeneration;
        consecutiveSuspiciousTicks = 0;
        if (resetFailures)
        {
            consecutiveClockFailures = 0;
            retryCompositorClockAt = 0;
        }
        nextTimingPollAt = 0;
        nextTimingCorrectionAt = 0;
        nominalNextTick = Stopwatch.GetTimestamp();
    }

    private static bool TryGetFrameId(CompositionFrameIdType frameIdType, out ulong frameId)
    {
        frameId = 0;
        if (Win32Platform.WindowsVersion.Build < 22000)
            return false;

        try
        {
            return DCompositionGetFrameId(frameIdType, out frameId) >= 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetCompositionTiming(out long phase, out long interval)
    {
        if (TryGetDCompTiming(out phase, out interval))
            return true;

        return TryGetDwmTiming(out phase, out interval);
    }

    private static bool TryGetDCompTiming(out long phase, out long interval)
    {
        phase = 0;
        interval = 0;
        if (Win32Platform.WindowsVersion.Build < 22000)
            return false;

        try
        {
            if (DCompositionGetFrameId(CompositionFrameIdType.Completed, out ulong frameId) < 0)
                return false;

            if (DCompositionGetStatistics(
                    frameId,
                    out CompositionFrameStats frameStats,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero) < 0)
            {
                return false;
            }

            return TryNormalizeTiming(frameStats.StartTime, frameStats.FramePeriod, out phase, out interval);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetDwmTiming(out long phase, out long interval)
    {
        phase = 0;
        interval = 0;
        var timing = new DwmTimingInfo
        {
            Size = (uint)Marshal.SizeOf<DwmTimingInfo>()
        };

        if (DwmGetCompositionTimingInfo(IntPtr.Zero, ref timing) < 0)
            return false;

        return TryNormalizeTiming(timing.QpcVBlank, timing.QpcRefreshPeriod, out phase, out interval);
    }

    internal static bool TryNormalizeTiming(
        ulong samplePhase,
        ulong sampleInterval,
        out long phase,
        out long interval)
    {
        phase = 0;
        interval = 0;
        if (samplePhase > long.MaxValue ||
            sampleInterval > long.MaxValue ||
            sampleInterval < (ulong)(Stopwatch.Frequency / 1000) ||
            sampleInterval > (ulong)(Stopwatch.Frequency / 5))
        {
            return false;
        }

        phase = (long)samplePhase;
        interval = (long)sampleInterval;
        return true;
    }

    internal static CompositorWaitResult ClassifyCompositorWaitResult(
        uint result,
        uint handleCount)
    {
        if (result < handleCount)
            return CompositorWaitResult.Wake;
        if (result == handleCount)
            return CompositorWaitResult.Tick;
        if (result == StatusTimeout)
            return CompositorWaitResult.Timeout;
        if (result == StatusGraphicsPresentOccluded)
            return CompositorWaitResult.Occluded;
        return CompositorWaitResult.Failure;
    }

    internal static long CalculateNextTick(long now, long phase, long interval)
    {
        if (interval <= 0)
            throw new ArgumentOutOfRangeException(nameof(interval));

        long elapsed = Math.Max(0, now - phase);
        return phase + (elapsed / interval + 1) * interval;
    }

    internal static bool ShouldTickAfterOccludedWait(bool wakeSignaled, bool stopped)
        => !wakeSignaled && !stopped;

    internal static uint CalculateClockTimeoutMilliseconds(long interval) => 100;

    internal static long GetRetryDelayTicks(
        int failureCount,
        CompositorWaitResult resultKind)
    {
        int delaySeconds =
            Math.Min(1 << Math.Min(failureCount - 1, 5), ClockRetryDelayMaxSeconds);
        return delaySeconds * Stopwatch.Frequency;
    }

    private static bool IsCompositorClockCandidate()
        => Win32Platform.WindowsVersion.Build >= 22000 &&
           UnmanagedMethods.GetSystemMetrics(UnmanagedMethods.SystemMetric.SM_REMOTESESSION) == 0;

    private int WaitForPacing(IntPtr[] waitHandles, bool pacingTimerAvailable, long remainingTicks)
    {
        if (pacingTimerAvailable)
        {
            long dueTime = -Math.Max(1,
                (long)Math.Ceiling((double)remainingTicks / Stopwatch.Frequency * TimeSpan.TicksPerSecond));
            if (SetWaitableTimerEx(
                    waitHandles[1],
                    ref dueTime,
                    0,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    0))
            {
                return UnmanagedMethods.WaitForMultipleObjectsEx(
                    waitHandles.Length,
                    waitHandles,
                    false,
                    Timeout.Infinite,
                    false);
            }
        }

        return _wakeEvent.WaitOne(
            Math.Max(1, (int)Math.Ceiling((double)remainingTicks / Stopwatch.Frequency * 1000)))
            ? 0
            : 1;
    }

    private static SafeWaitHandle? TryCreatePacingTimer()
    {
        const uint desiredAccess = Synchronize | TimerModifyState;
        IntPtr timer = CreateWaitableTimerEx(
            IntPtr.Zero, null, CreateWaitableTimerHighResolution, desiredAccess);
        if (timer == IntPtr.Zero)
            timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, desiredAccess);

        return timer == IntPtr.Zero ? null : new SafeWaitHandle(timer, ownsHandle: true);
    }

    internal static long CalculateIntervalTicks(int displayFps) =>
        Math.Max(1, Stopwatch.Frequency / Math.Max(1, displayFps));

    private static long MillisecondsToTimestamp(int milliseconds)
        => (long)Math.Ceiling(milliseconds / 1000d * Stopwatch.Frequency);

    public void Dispose()
    {
        Thread? thread;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;

            _disposed = true;
            _shutdownRequested = true;
            _stopped = true;
            Interlocked.Exchange(ref _activationPending, 0);
            Volatile.Write(ref _tick, null);
            thread = _thread;
            _wakeEvent.Set();
        }

        if (thread == Thread.CurrentThread)
            return;

        thread?.Join();
        _wakeEvent.Dispose();
    }

    internal enum CompositorWaitResult
    {
        Wake,
        Tick,
        Timeout,
        Occluded,
        Failure
    }

    private enum CompositionFrameIdType
    {
        Created,
        Confirmed,
        Completed
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositionFrameStats
    {
        public ulong StartTime;
        public ulong TargetTime;
        public ulong FramePeriod;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnsignedRatio
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmTimingInfo
    {
        public uint Size;
        public UnsignedRatio RateRefresh;
        public ulong QpcRefreshPeriod;
        public UnsignedRatio RateCompose;
        public ulong QpcVBlank;
        public ulong Refresh;
        public uint DxRefresh;
        public ulong QpcCompose;
        public ulong Frame;
        public uint DxPresent;
        public ulong RefreshFrame;
        public ulong FrameSubmitted;
        public uint DxPresentSubmitted;
        public ulong FrameConfirmed;
        public uint DxPresentConfirmed;
        public ulong RefreshConfirmed;
        public uint DxRefreshConfirmed;
        public ulong FramesLate;
        public uint FramesOutstanding;
        public ulong FrameDisplayed;
        public ulong QpcFrameDisplayed;
        public ulong RefreshFrameDisplayed;
        public ulong FrameComplete;
        public ulong QpcFrameComplete;
        public ulong FramePending;
        public ulong QpcFramePending;
        public ulong FramesDisplayed;
        public ulong FramesComplete;
        public ulong FramesPending;
        public ulong FramesAvailable;
        public ulong FramesDropped;
        public ulong FramesMissed;
        public ulong RefreshNextDisplayed;
        public ulong RefreshNextPresented;
        public ulong RefreshesDisplayed;
        public ulong RefreshesPresented;
        public ulong RefreshStarted;
        public ulong PixelsReceived;
        public ulong PixelsDrawn;
        public ulong BuffersEmpty;
    }

    internal sealed class PhaseGrid
    {
        private readonly Queue<long> _phaseErrors = new();
        private long _phase;

        public bool IsInitialized { get; private set; }
        public long Interval { get; private set; }
        internal long Phase => _phase;

        public void Reset()
        {
            _phaseErrors.Clear();
            _phase = 0;
            Interval = 0;
            IsInitialized = false;
        }

        public void Update(
            long samplePhase,
            long sampleInterval,
            bool allowCorrection)
        {
            if (!IsInitialized ||
                Math.Abs(sampleInterval - Interval) > Math.Max(1, Interval / 200))
            {
                _phase = samplePhase;
                Interval = sampleInterval;
                _phaseErrors.Clear();
                IsInitialized = true;
                return;
            }

            if (!allowCorrection)
                return;

            long nearestGridPoint = GetNearestGridPoint(samplePhase);
            long phaseError = samplePhase - nearestGridPoint;
            _phaseErrors.Enqueue(phaseError);
            while (_phaseErrors.Count > PhaseErrorSampleCount)
                _phaseErrors.Dequeue();

            if (_phaseErrors.Count < PhaseErrorSampleCount)
                return;

            long[] errors = _phaseErrors.ToArray();
            Array.Sort(errors);
            long medianError = errors[errors.Length / 2];
            long maximumSlew = Math.Max(1, Stopwatch.Frequency / 2000);
            _phase += Math.Clamp(medianError, -maximumSlew, maximumSlew);
            _phaseErrors.Clear();
        }

        public long GetNextTick(long now)
            => CalculateNextTick(now, _phase, Interval);

        private long GetNearestGridPoint(long timestamp)
        {
            double periods = (timestamp - _phase) / (double)Interval;
            return _phase + (long)Math.Round(periods) * Interval;
        }
    }
}
