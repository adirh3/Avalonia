using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Xunit;

namespace Avalonia.Base.UnitTests.Input;

#pragma warning disable CS0612, CS0618

public sealed class LegacyDataObjectTests
{
    [Fact]
    public void Legacy_DataObject_Converts_Text_And_FileNames_To_DataTransfer()
    {
        var path = Path.GetTempFileName();

        try
        {
            var dataObject = new DataObject();
            dataObject.Set(DataFormats.Text, "legacy text");
            dataObject.Set(DataFormats.FileNames, new[] { path });

            using var dataTransfer = dataObject.ToDataTransfer();

            Assert.Equal("legacy text", dataTransfer.TryGetText());
            Assert.Equal(path, dataTransfer.TryGetFiles()?.Single().TryGetLocalPath());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Legacy_DataObject_Preserves_Custom_InProcess_Data_Through_Forwarded_Transfer()
    {
        var expected = new object();
        var dataObject = new DataObject();
        dataObject.Set("Custom", expected);

        using var dataTransfer = dataObject.ToDataTransfer();
        var forwardedTransfer = new ForwardedDataTransfer(dataTransfer);

        Assert.Same(dataObject, forwardedTransfer.ToLegacyDataObject());
        Assert.Same(expected, forwardedTransfer.ToLegacyDataObject().Get("Custom"));

        dataTransfer.Dispose();
        Assert.NotSame(dataObject, forwardedTransfer.ToLegacyDataObject());
    }

    [Fact]
    public void DragEventArgs_Data_Returns_Original_Legacy_Object()
    {
        var dataObject = new DataObject();
        dataObject.Set(DataFormats.Text, "value");

        using var dataTransfer = dataObject.ToDataTransfer();
        var args = new DragEventArgs(null, dataTransfer, new Border(), default, KeyModifiers.None);

        Assert.Same(dataObject, args.Data);
    }

    [Fact]
    public void Clipboard_Interface_Preserves_Legacy_Members()
    {
        var clipboardType = typeof(Avalonia.Input.Platform.IClipboard);

        Assert.Equal(typeof(System.Threading.Tasks.Task), clipboardType.GetMethod("SetDataObjectAsync")?.ReturnType);
        Assert.Equal(typeof(System.Threading.Tasks.Task<IDataObject?>),
            clipboardType.GetMethod("TryGetInProcessDataObjectAsync")?.ReturnType);
    }

    [Fact]
    public void DragDrop_Preserves_Legacy_PointerEvent_Overloads()
    {
        Assert.NotNull(typeof(DragDrop).GetMethod(
            nameof(DragDrop.DoDragDrop),
            [typeof(PointerEventArgs), typeof(IDataObject), typeof(DragDropEffects)]));
        Assert.NotNull(typeof(DragDrop).GetMethod(
            nameof(DragDrop.DoDragDropAsync),
            [typeof(PointerEventArgs), typeof(IDataTransfer), typeof(DragDropEffects)]));
    }

    private sealed class ForwardedDataTransfer(IDataTransfer source) : IDataTransfer
    {
        public IReadOnlyList<DataFormat> Formats => source.Formats;

        public IReadOnlyList<IDataTransferItem> Items => source.Items;

        public void Dispose()
        {
        }
    }
}

#pragma warning restore CS0612, CS0618
