using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Win32;
using Avalonia.Win32.Win32Com;
using MicroCom.Runtime;
using Xunit;
using OleDataObject = Avalonia.Win32.Win32Com.IDataObject;

namespace Avalonia.IntegrationTests.Win32;

public class DataTransferToOleDataObjectWrapperTests
{
    [Fact]
    public unsafe void AsyncOperation_KeepsDataTransferAliveUntilEndOperation()
    {
        var dataTransfer = new TrackingDataTransfer();
        using var wrapper = new DataTransferToOleDataObjectWrapper(dataTransfer);
        var asyncCapability = Assert.IsAssignableFrom<IDataObjectAsyncCapability>(wrapper);

        asyncCapability.SetAsyncMode(1);
        Assert.Equal(1, asyncCapability.AsyncMode);

        asyncCapability.StartOperation(null);
        wrapper.ReleaseDataTransferIfNotInOperation();

        Assert.Equal(1, asyncCapability.InOperation());
        Assert.Equal(0, dataTransfer.DisposeCount);

        asyncCapability.EndOperation(0, null, 0);

        Assert.Equal(0, asyncCapability.InOperation());
        Assert.Equal(1, dataTransfer.DisposeCount);
    }

    [Fact]
    public void SynchronousOperation_ReleasesDataTransferImmediately()
    {
        var dataTransfer = new TrackingDataTransfer();
        using var wrapper = new DataTransferToOleDataObjectWrapper(dataTransfer);

        wrapper.ReleaseDataTransferIfNotInOperation();
        wrapper.ReleaseDataTransferIfNotInOperation();

        Assert.Equal(1, dataTransfer.DisposeCount);
    }

    [Fact]
    public unsafe void NativeAsyncReference_KeepsWrapperAliveAfterManagedOwnerIsDisposed()
    {
        var dataTransfer = new TrackingDataTransfer();
        var wrapper = new DataTransferToOleDataObjectWrapper(dataTransfer);
        var dataObjectPointer = MicroComRuntime.GetNativeIntPtr<OleDataObject>(wrapper, owned: true);
        {
            using var dataObject = MicroComRuntime.CreateProxyFor<OleDataObject>(dataObjectPointer, ownsHandle: true);
            using var asyncCapability = MicroComRuntime.QueryInterface<IDataObjectAsyncCapability>(dataObject);

            asyncCapability.SetAsyncMode(1);
            asyncCapability.StartOperation(null);
            wrapper.Dispose();

            Assert.Equal(0, dataTransfer.DisposeCount);

            asyncCapability.EndOperation(0, null, 0);

            Assert.Equal(1, dataTransfer.DisposeCount);
        }

        Assert.Equal(1, dataTransfer.DisposeCount);
    }

    private sealed class TrackingDataTransfer : IDataTransfer
    {
        public IReadOnlyList<DataFormat> Formats { get; } = Array.Empty<DataFormat>();

        public IReadOnlyList<IDataTransferItem> Items { get; } = Array.Empty<IDataTransferItem>();

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
