using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using Avalonia.Win32.WinRT.Composition;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class WinUiRenderTimerTests
{
    [Theory]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(240)]
    public void Interval_Matches_Display_Frequency(int displayFps)
    {
        long expected = Math.Max(1, Stopwatch.Frequency / displayFps);

        Assert.Equal(expected, WinUiRenderTimer.CalculateIntervalTicks(displayFps));
    }

    [Fact]
    public void Starting_After_Stop_Produces_An_Immediate_Tick()
    {
        using var timer = new WinUiRenderTimer(60);
        using var firstTick = new ManualResetEventSlim();

        timer.Tick = _ => firstTick.Set();
        Assert.True(firstTick.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        timer.Tick = null;
        firstTick.Reset();
        var stopwatch = Stopwatch.StartNew();
        timer.Tick = _ => firstTick.Set();

        Assert.True(firstTick.Wait(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(50));
        timer.Tick = null;
    }

    [Fact]
    public void Stopped_Timer_Does_Not_Tick()
    {
        using var timer = new WinUiRenderTimer(240);
        using var firstTick = new ManualResetEventSlim();
        var tickCount = 0;

        timer.Tick = _ =>
        {
            Interlocked.Increment(ref tickCount);
            timer.Tick = null;
            firstTick.Set();
        };

        Assert.True(firstTick.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.False(
            TestContext.Current.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(1, Volatile.Read(ref tickCount));
    }

    [Fact]
    public void Timer_Thread_Uses_Mta()
    {
        using var timer = new WinUiRenderTimer(60);
        using var firstTick = new ManualResetEventSlim();
        var apartmentState = ApartmentState.Unknown;

        timer.Tick = _ =>
        {
            apartmentState = Thread.CurrentThread.GetApartmentState();
            timer.Tick = null;
            firstTick.Set();
        };

        Assert.True(firstTick.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Equal(ApartmentState.MTA, apartmentState);
    }

    [Fact]
    public void Display_Fps_Change_Recalculates_An_Active_Wait()
    {
        using var timer = new WinUiRenderTimer(1);
        using var firstTick = new ManualResetEventSlim();
        using var secondTick = new ManualResetEventSlim();
        var tickCount = 0;

        timer.Tick = _ =>
        {
            if (Interlocked.Increment(ref tickCount) == 1)
                firstTick.Set();
            else
                secondTick.Set();
        };

        Assert.True(firstTick.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        timer.DisplayFps = 240;
        Assert.True(secondTick.Wait(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        timer.Tick = null;
    }

    [Fact]
    public void Restart_During_A_Blocked_Tick_Is_Immediate()
    {
        using var timer = new WinUiRenderTimer(1);
        using var firstTickStarted = new ManualResetEventSlim();
        using var releaseFirstTick = new ManualResetEventSlim();
        using var restartedTick = new ManualResetEventSlim();

        timer.Tick = _ =>
        {
            firstTickStarted.Set();
            releaseFirstTick.Wait(TestContext.Current.CancellationToken);
        };

        Assert.True(firstTickStarted.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        timer.Tick = null;
        timer.Tick = _ => restartedTick.Set();

        var stopwatch = Stopwatch.StartNew();
        releaseFirstTick.Set();

        Assert.True(restartedTick.Wait(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100));
        timer.Tick = null;
    }

    [Theory]
    [InlineData(0u, 1u, 0)]
    [InlineData(1u, 1u, 1)]
    [InlineData(0x00000102u, 1u, 2)]
    [InlineData(0xC01E0006u, 1u, 3)]
    [InlineData(0xC0000001u, 1u, 4)]
    public void Compositor_Wait_Result_Is_Classified(
        uint result,
        uint handleCount,
        int expected)
    {
        Assert.Equal(
            expected,
            (int)WinUiRenderTimer.ClassifyCompositorWaitResult(result, handleCount));
    }

    [Fact]
    public void Next_Tick_Uses_The_Following_Phase_Boundary()
    {
        Assert.Equal(140, WinUiRenderTimer.CalculateNextTick(125, 100, 20));
        Assert.Equal(120, WinUiRenderTimer.CalculateNextTick(100, 100, 20));
    }

    [Fact]
    public void Composition_Timing_Rejects_Implausible_Periods()
    {
        ulong validPeriod = (ulong)(Stopwatch.Frequency / 60);

        Assert.True(WinUiRenderTimer.TryNormalizeTiming(
            100,
            validPeriod,
            out long phase,
            out long interval));
        Assert.Equal(100, phase);
        Assert.Equal((long)validPeriod, interval);

        Assert.False(WinUiRenderTimer.TryNormalizeTiming(
            100,
            (ulong)(Stopwatch.Frequency / 2000),
            out _,
            out _));
        Assert.False(WinUiRenderTimer.TryNormalizeTiming(
            100,
            (ulong)Stopwatch.Frequency,
            out _,
            out _));
    }

    [Fact]
    public void Compositor_Clock_Timeout_Is_Bounded_Independently_Of_Refresh_Rate()
    {
        Assert.Equal(
            100u,
            WinUiRenderTimer.CalculateClockTimeoutMilliseconds(
                WinUiRenderTimer.CalculateIntervalTicks(60)));
        Assert.Equal(
            100u,
            WinUiRenderTimer.CalculateClockTimeoutMilliseconds(
                WinUiRenderTimer.CalculateIntervalTicks(240)));
    }

    [Fact]
    public void Timeout_Retry_Uses_Progressive_Backoff()
    {
        long frequency = Stopwatch.Frequency;

        Assert.Equal(
            frequency,
            WinUiRenderTimer.GetRetryDelayTicks(
                1,
                WinUiRenderTimer.CompositorWaitResult.Timeout));
        Assert.Equal(
            frequency * 2,
            WinUiRenderTimer.GetRetryDelayTicks(
                2,
                WinUiRenderTimer.CompositorWaitResult.Timeout));
        Assert.Equal(
            frequency * 30,
            WinUiRenderTimer.GetRetryDelayTicks(
                10,
                WinUiRenderTimer.CompositorWaitResult.Timeout));
    }

    [Fact]
    public void Occluded_Clock_Continues_Draining_At_A_Low_Rate()
    {
        Assert.True(WinUiRenderTimer.ShouldTickAfterOccludedWait(
            wakeSignaled: false,
            stopped: false));
        Assert.False(WinUiRenderTimer.ShouldTickAfterOccludedWait(
            wakeSignaled: true,
            stopped: false));
        Assert.False(WinUiRenderTimer.ShouldTickAfterOccludedWait(
            wakeSignaled: false,
            stopped: true));
    }

    [Fact]
    public void Clock_Refresh_After_Dispose_Is_Ignored()
    {
        var timer = new WinUiRenderTimer(60);
        timer.Dispose();

        timer.RequestClockRefresh();
    }

    [Fact]
    public void Phase_Grid_Reseeds_On_Period_Change_And_Slews_Phase()
    {
        var grid = new WinUiRenderTimer.PhaseGrid();
        grid.Update(100, 20, allowCorrection: false);

        Assert.True(grid.IsInitialized);
        Assert.Equal(20, grid.Interval);
        Assert.Equal(140, grid.GetNextTick(125));

        grid.Update(110, 25, allowCorrection: false);
        Assert.Equal(25, grid.Interval);
        Assert.Equal(135, grid.GetNextTick(125));

        long initialPhase = grid.Phase;
        for (var index = 0; index < 5; index++)
            grid.Update(initialPhase + 1, 25, allowCorrection: true);

        Assert.Equal(initialPhase + 1, grid.Phase);
    }

    [Fact]
    public void Tick_Can_Be_Dispatched_To_The_Composition_Thread()
    {
        using var queuedActions = new BlockingCollection<Action>();
        using var callbackInvoked = new ManualResetEventSlim();
        using var timer = new WinUiRenderTimer(60, queuedActions.Add);
        int callbackThread = 0;
        int dispatchThread = Environment.CurrentManagedThreadId;

        timer.Tick = _ =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            callbackInvoked.Set();
        };

        Assert.True(queuedActions.TryTake(
            out Action? dispatch,
            2000,
            TestContext.Current.CancellationToken));
        Assert.False(callbackInvoked.IsSet);

        dispatch!();

        Assert.True(callbackInvoked.Wait(
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken));
        Assert.Equal(dispatchThread, callbackThread);
        timer.Tick = null;
    }
}
