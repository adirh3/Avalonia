using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Metadata;
using Avalonia.Platform.Storage;

namespace Avalonia.Input.Platform
{
    /// <summary>
    /// Represents the system clipboard.
    /// </summary>
    [NotClientImplementable]
    public interface IClipboard
    {
        [Obsolete($"Use {nameof(ClipboardExtensions)}.{nameof(ClipboardExtensions.TryGetTextAsync)} instead.")]
        Task<string?> GetTextAsync()
            => ClipboardExtensions.TryGetTextAsync(this);

        Task SetTextAsync(string? text)
            => ClipboardExtensions.SetTextAsync(this, text);

        /// <summary>
        /// Clears any data from the system clipboard.
        /// </summary>
        Task ClearAsync();

        [Obsolete($"Use {nameof(SetDataAsync)} instead.")]
        Task SetDataObjectAsync(IDataObject data)
            => SetDataAsync(new DataObjectToDataTransferWrapper(data));

        /// <summary>
        /// Places a data object on the clipboard.
        /// The data object is responsible for providing supported formats and data upon request.
        /// </summary>
        /// <param name="dataTransfer">The data object to set on the clipboard.</param>
        /// <remarks>
        /// <para>
        /// If <paramref name="dataTransfer"/> is null, nothing will get placed on the clipboard and this method
        /// will be equivalent to <see cref="ClearAsync"/>.
        /// </para>
        /// <para>
        /// The <see cref="IAsyncDataTransfer"/> must NOT be disposed by the caller after this call.
        /// The clipboard will dispose of it automatically when it becomes unused.
        /// </para>
        /// </remarks>
        Task SetDataAsync(IAsyncDataTransfer? dataTransfer);

        /// <summary>
        /// Permanently adds the data that is on the Clipboard so that it is available after the data's original application closes.
        /// </summary>
        /// <returns></returns>
        /// <remarks>This method is only supported on the Windows platform. This method will do nothing on other platforms.</remarks>
        Task FlushAsync();

        [Obsolete($"Use {nameof(ClipboardExtensions.GetDataFormatsAsync)} instead.")]
        async Task<string[]> GetFormatsAsync()
        {
            using var dataTransfer = await TryGetDataAsync();
            return dataTransfer is null ? [] : dataTransfer.Formats.Select(DataFormats.ToString).ToArray();
        }

        [Obsolete($"Use {nameof(TryGetDataAsync)} instead.")]
        async Task<object?> GetDataAsync(string format)
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

        /// <summary>
        /// Retrieves data from the clipboard.
        /// </summary>
        /// <remarks>
        /// <para>The returned <see cref="IAsyncDataTransfer"/> MUST be disposed by the caller.</para>
        /// <para>
        /// Avoid storing the returned <see cref="IAsyncDataTransfer"/> instance for a long time:
        /// use it, then dispose it as soon as possible.
        /// </para>
        /// </remarks>
        Task<IAsyncDataTransfer?> TryGetDataAsync();

        [Obsolete($"Use {nameof(TryGetInProcessDataAsync)} instead.")]
        async Task<IDataObject?> TryGetInProcessDataObjectAsync()
        {
            var dataTransfer = await TryGetInProcessDataAsync().ConfigureAwait(false);
            return (dataTransfer as DataObjectToDataTransferWrapper)?.DataObject;
        }

        /// <summary>
        /// Retrieves the exact instance of a <see cref="IAsyncDataTransfer"/> previously placed on the clipboard
        /// by <see cref="SetDataAsync"/>, if any.
        /// </summary>
        /// <returns>The data transfer object if present, null otherwise.</returns>
        /// <remarks>
        /// <para>This method cannot be used to retrieve a <see cref="IAsyncDataTransfer"/> set by another process.</para>
        /// <para>This method is only supported on Windows, macOS and X11 platforms. Other platforms will always return null.</para>
        /// <para>
        /// Contrary to <see cref="TryGetDataAsync"/>, the returned <see cref="IAsyncDataTransfer"/> must NOT be disposed
        /// by the caller since it's still owned by the clipboard.
        /// </para>
        /// </remarks>
        Task<IAsyncDataTransfer?> TryGetInProcessDataAsync();
    }
}
