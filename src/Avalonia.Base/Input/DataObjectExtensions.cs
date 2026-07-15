using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace Avalonia.Input;

#pragma warning disable CS0618

/// <summary>
/// Compatibility helpers for the legacy <see cref="IDataObject"/> API.
/// </summary>
public static class DataObjectExtensions
{
    /// <summary>
    /// Returns storage items when the data object contains files.
    /// </summary>
    public static IEnumerable<IStorageItem>? GetFiles(this IDataObject dataObject)
        => dataObject.Get(DataFormats.Files) as IEnumerable<IStorageItem>;

    /// <summary>
    /// Returns local file names when the data object contains files or file names.
    /// </summary>
    [Obsolete("Use GetFiles; this method is supported only on desktop platforms."),
     EditorBrowsable(EditorBrowsableState.Never)]
    public static IEnumerable<string>? GetFileNames(this IDataObject dataObject)
        => (dataObject.Get(DataFormats.FileNames) as IEnumerable<string>)
           ?? dataObject.GetFiles()?
               .Select(static file => file.TryGetLocalPath())
               .Where(static path => !string.IsNullOrEmpty(path))
               .OfType<string>();

    /// <summary>
    /// Returns dragged text when available.
    /// </summary>
    public static string? GetText(this IDataObject dataObject)
        => dataObject.Get(DataFormats.Text) as string;

    /// <summary>
    /// Wraps a legacy data object for use with the current drag-and-drop API.
    /// </summary>
    [Obsolete($"Use {nameof(DataTransfer)} directly.")]
    public static IDataTransfer ToDataTransfer(this IDataObject dataObject)
    {
        ArgumentNullException.ThrowIfNull(dataObject);
        return new DataObjectToDataTransferWrapper(dataObject);
    }
}

#pragma warning restore CS0618
