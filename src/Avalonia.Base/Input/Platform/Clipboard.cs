using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace Avalonia.Input.Platform;

/// <summary>
/// Implementation of <see cref="IClipboard"/>
/// </summary>
internal sealed class Clipboard(IClipboardImpl clipboardImpl) : IClipboard
{
    private readonly IClipboardImpl _clipboardImpl = clipboardImpl;
    private IAsyncDataTransfer? _lastDataTransfer;

    Task<string?> IClipboard.GetTextAsync()
        => this.TryGetTextAsync();

    Task IClipboard.SetTextAsync(string? text)
        => this.SetValueAsync(DataFormat.Text, text);

    public Task ClearAsync()
    {
        _lastDataTransfer?.Dispose();
        _lastDataTransfer = null;

        return _clipboardImpl.ClearAsync();
    }

#pragma warning disable CS0612, CS0618
    Task IClipboard.SetDataObjectAsync(IDataObject data)
        => SetDataAsync(new DataObjectToDataTransferWrapper(data));
#pragma warning restore CS0612, CS0618

    public Task SetDataAsync(IAsyncDataTransfer? dataTransfer)
    {
        if (dataTransfer is null)
            return ClearAsync();

        if (_clipboardImpl is IOwnedClipboardImpl)
            _lastDataTransfer = dataTransfer;

        return _clipboardImpl.SetDataAsync(dataTransfer);
    }

    public Task FlushAsync()
        => _clipboardImpl is IFlushableClipboardImpl flushable ? flushable.FlushAsync() : Task.CompletedTask;

    async Task<string[]> IClipboard.GetFormatsAsync()
    {
        var dataTransfer = await TryGetDataAsync();
        return dataTransfer is null ? [] : dataTransfer.Formats.Select(DataFormats.ToString).ToArray();
    }

    async Task<object?> IClipboard.GetDataAsync(string format)
    {
        using var dataTransfer = await TryGetDataAsync();
        if (dataTransfer is null)
            return null;

#pragma warning disable CS0618
        if (format == DataFormats.Text)
            return await dataTransfer.TryGetTextAsync().ConfigureAwait(false);

        if (format == DataFormats.Files)
            return await dataTransfer.TryGetFilesAsync().ConfigureAwait(false);

        if (format == DataFormats.FileNames)
        {
            return (await dataTransfer.TryGetFilesAsync().ConfigureAwait(false))
                ?.Select(static file => file.TryGetLocalPath())
                .Where(static path => path is not null)
                .ToArray();
        }
#pragma warning restore CS0618

        return await dataTransfer
            .TryGetValueAsync(DataFormat.CreateBytesPlatformFormat(format))
            .ConfigureAwait(false);
    }

    public Task<IAsyncDataTransfer?> TryGetDataAsync()
        => _clipboardImpl.TryGetDataAsync();

#pragma warning disable CS0612, CS0618
    async Task<IDataObject?> IClipboard.TryGetInProcessDataObjectAsync()
    {
        var dataTransfer = await TryGetInProcessDataAsync().ConfigureAwait(false);
        return (dataTransfer as DataObjectToDataTransferWrapper)?.DataObject;
    }
#pragma warning restore CS0612, CS0618

    public async Task<IAsyncDataTransfer?> TryGetInProcessDataAsync()
    {
        if (_lastDataTransfer is null || _clipboardImpl is not IOwnedClipboardImpl ownedClipboardImpl)
            return null;

        if (!await ownedClipboardImpl.IsCurrentOwnerAsync())
            _lastDataTransfer = null;

        return _lastDataTransfer;
    }
}
