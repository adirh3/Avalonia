using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Transport;
using Avalonia.Threading;
using Avalonia.UnitTests;
using Xunit;

namespace Avalonia.Base.UnitTests.Media;

public class MediaContextTests
{
    [Fact]
    public void Animation_Frame_Advances_While_Composition_Batch_Is_Pending()
    {
        using var app = UnitTestApplication.Start(TestServices.MockPlatformRenderInterface);
        var timer = new CompositorTestServices.ManualRenderTimer();
        var compositor = new Compositor(RenderLoop.FromTimer(timer), null);
        var mediaContext = MediaContext.Instance;
        var commitCount = 0;
        var animationFrameCount = 0;
        CompositionBatch? nextBatch = null;

        compositor.AfterCommit += () => commitCount++;

        var pendingBatch = compositor.RequestCompositionBatchCommitAsync();
        Dispatcher.UIThread.RunJobs(null, TestContext.Current.CancellationToken);

        Assert.Equal(1, commitCount);
        Assert.False(pendingBatch.Processed.IsCompleted);

        mediaContext.RequestAnimationFrame(_ =>
        {
            animationFrameCount++;
            nextBatch = compositor.RequestCompositionBatchCommitAsync();
        });
        Dispatcher.UIThread.RunJobs(null, TestContext.Current.CancellationToken);

        Assert.Equal(1, animationFrameCount);
        Assert.Equal(1, commitCount);
        Assert.NotNull(nextBatch);
        Assert.False(nextBatch!.Processed.IsCompleted);

        timer.TriggerTick();
        Dispatcher.UIThread.RunJobs(null, TestContext.Current.CancellationToken);

        Assert.True(pendingBatch.Processed.IsCompleted);
        Assert.Equal(2, commitCount);
        Assert.False(nextBatch.Processed.IsCompleted);
    }
}
