using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.Win32;
using Avalonia.Win32.WinRT;
using Avalonia.Win32.WinRT.Composition;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class WinUiCompositorConnectionTests
{
    [Fact]
    [SuppressMessage("Usage", "xUnit1031", Justification = "Reproduce the bounded UI-thread batch wait from WM_PAINT.")]
    public void Watchdog_Completes_Pending_Batches_When_Native_Commits_Never_Complete()
    {
        Dispatcher.UIThread.VerifyAccess();
        var resultChecks = 0;
        var commitRequests = 0;
        var compositor5 = CreateProxy<ICompositor5>(method =>
        {
            if (method.Name != nameof(ICompositor5.RequestCommitAsync))
                throw new NotSupportedException(method.Name);

            Interlocked.Increment(ref commitRequests);
            return CreateProxy<IAsyncAction>(actionMethod =>
            {
                if (actionMethod.Name == nameof(IAsyncAction.GetResults))
                {
                    Interlocked.Increment(ref resultChecks);
                    throw new COMException("The test commit is still pending.", unchecked((int)0x8000000E));
                }

                if (actionMethod.Name is nameof(IAsyncAction.SetCompleted) or nameof(IDisposable.Dispose))
                    return null;

                throw new NotSupportedException(actionMethod.Name);
            });
        });
        var shared = (WinUiCompositionShared)RuntimeHelpers.GetUninitializedObject(typeof(WinUiCompositionShared));
        SetField(shared, "<SyncRoot>k__BackingField", new object());
        SetField(shared, "<Compositor5>k__BackingField", compositor5);
        using var wake = new AutoResetEvent(false);
        var connection =
            (WinUiCompositorConnection)RuntimeHelpers.GetUninitializedObject(typeof(WinUiCompositorConnection));
        SetField(connection, "_shared", shared);
        SetField(connection, "_wakeEvent", wake);

        using var scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(connection));
        var compositor = new Compositor(null);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uint nativeThreadId = 0;
        var thread = new Thread(() =>
        {
            Volatile.Write(ref nativeThreadId, GetCurrentThreadId());
            try
            {
                typeof(WinUiCompositorConnection)
                    .GetMethod("RunLoop", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(connection, null);
                completion.SetResult();
            }
            catch (Exception e)
            {
                completion.SetException(e);
            }
        })
        {
            IsBackground = true,
            Name = "WinUI watchdog regression"
        };
        thread.SetApartmentState(ApartmentState.STA);
        var started = false;

        try
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var batch = compositor.RequestCompositionBatchCommitAsync();
                Dispatcher.UIThread.RunJobs();
                Assert.False(batch.Rendered.IsCompleted);

                if (!started)
                {
                    thread.Start();
                    started = true;
                }

                Assert.True(batch.Rendered.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken),
                    $"Watchdog did not complete the UI's pending render batch in cycle {cycle}.");
                Assert.True(batch.Processed.IsCompletedSuccessfully);
                Assert.True(batch.Rendered.IsCompletedSuccessfully);
                Assert.True(Volatile.Read(ref resultChecks) >= cycle + 1);
            }

            Assert.True(Volatile.Read(ref commitRequests) >= 2);
        }
        finally
        {
            if (started)
            {
                if (thread.IsAlive)
                {
                    connection.Tick = null;
                    Assert.True(PostThreadMessage(Volatile.Read(ref nativeThreadId), 0x0012, IntPtr.Zero, IntPtr.Zero));
                    wake.Set();
                    Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "The native render loop did not exit.");
                }

                completion.Task.GetAwaiter().GetResult();
            }

            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, NativeCallProxy>();
        Assert.IsAssignableFrom<NativeCallProxy>(proxy).Handler = handler;
        return proxy;
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    public class NativeCallProxy : DispatchProxy
    {
        public Func<MethodInfo, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
}
