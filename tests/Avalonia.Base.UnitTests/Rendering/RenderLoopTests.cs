using System;
using System.Collections.Generic;
using Avalonia.Rendering;
using Xunit;

namespace Avalonia.Base.UnitTests.Rendering;

public class RenderLoopTests
{
    [Fact]
    public void Wakeup_While_Running_Requests_Expedited_Tick_Without_Restarting_Timer()
    {
        var timer = new ImmediateRenderTimer();
        var loop = RenderLoop.FromTimer(timer);

        loop.Add(new RenderLoopTask());
        loop.Wakeup();

        Assert.Equal(1, timer.TimerStarts);
        Assert.Equal(1, timer.ExpeditedTickRequests);
    }

    [Fact]
    public void Wakeup_After_Timer_Stops_Restarts_Event_Driven_Timer()
    {
        var timer = new ImmediateRenderTimer();
        var loop = RenderLoop.FromTimer(timer);
        var task = new RenderLoopTask();

        loop.Add(task);
        timer.TriggerTick();
        Assert.Null(timer.Tick);

        loop.Wakeup();

        Assert.NotNull(timer.Tick);
        Assert.Equal(2, timer.TimerStarts);
        Assert.Equal(0, timer.ExpeditedTickRequests);
    }

    [Fact]
    public void Wakeup_During_Tick_Keeps_Timer_Running_For_Next_Paced_Tick()
    {
        var timer = new ImmediateRenderTimer();
        var loop = RenderLoop.FromTimer(timer);
        var task = new RenderLoopTask(() => loop.Wakeup());

        loop.Add(task);
        timer.TriggerTick();

        Assert.NotNull(timer.Tick);
        Assert.Equal(1, timer.TimerStarts);
        Assert.Equal(0, timer.ExpeditedTickRequests);

        timer.TriggerTick();
        Assert.Null(timer.Tick);
    }

    [Fact]
    public void Tick_From_Previous_Run_Is_Ignored_After_Restart()
    {
        var timer = new ImmediateRenderTimer();
        var loop = RenderLoop.FromTimer(timer);
        var task = new RenderLoopTask();

        loop.Add(task);
        timer.TriggerTick();
        Assert.Equal(1, task.RenderCount);

        loop.Wakeup();
        timer.TriggerTick(0);
        Assert.Equal(1, task.RenderCount);

        timer.TriggerTick();
        Assert.Equal(2, task.RenderCount);
    }

    [Fact]
    public void Restart_During_Tick_Is_Not_Stopped_By_Previous_Run()
    {
        var timer = new ImmediateRenderTimer();
        var loop = RenderLoop.FromTimer(timer);
        RenderLoopTask? task = null;
        task = new RenderLoopTask(() =>
        {
            loop.Remove(task!);
            loop.Add(task!);
        });

        loop.Add(task);
        timer.TriggerTick();

        Assert.NotNull(timer.Tick);
        Assert.Equal(2, timer.TimerStarts);
        Assert.Equal(0, timer.ExpeditedTickRequests);

        timer.TriggerTick();
        Assert.Equal(2, task.RenderCount);
        Assert.Null(timer.Tick);
    }

    private sealed class ImmediateRenderTimer : IRenderTimer, IRenderTimerWithImmediateTick
    {
        private Action<TimeSpan>? _tick;
        private readonly List<Action<TimeSpan>> _ticks = new();

        public Action<TimeSpan>? Tick
        {
            get => _tick;
            set
            {
                _tick = value;
                if (value != null)
                {
                    _ticks.Add(value);
                    TimerStarts++;
                }
            }
        }

        public bool RunsInBackground => true;

        public int TimerStarts { get; private set; }
        public int ExpeditedTickRequests { get; private set; }

        public void RequestImmediateTick() => ExpeditedTickRequests++;

        public void TriggerTick() => _tick?.Invoke(TimeSpan.Zero);

        public void TriggerTick(int index) => _ticks[index].Invoke(TimeSpan.Zero);
    }

    private sealed class RenderLoopTask : IRenderLoopTask
    {
        private Action? _onFirstRender;

        public RenderLoopTask(Action? onFirstRender = null)
        {
            _onFirstRender = onFirstRender;
        }

        public bool Render()
        {
            RenderCount++;
            var callback = _onFirstRender;
            _onFirstRender = null;
            callback?.Invoke();
            return false;
        }

        public int RenderCount { get; private set; }
    }
}
