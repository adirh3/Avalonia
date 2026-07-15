using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Platform.Storage;

namespace Avalonia.Input.Platform;

/// <summary>
/// Wraps a <see cref="IDataTransfer"/> into a legacy <see cref="IDataObject"/>.
/// </summary>
[Obsolete]
internal sealed class DataTransferToDataObjectWrapper(IDataTransfer dataTransfer) : IDataObject
{
    public IDataTransfer DataTransfer { get; } = dataTransfer;

    public IEnumerable<string> GetDataFormats()
        => DataTransfer.Formats.Select(DataFormats.ToString);

    public bool Contains(string dataFormat)
        => DataTransfer.Contains(DataFormats.ToDataFormat(dataFormat));

    public object? Get(string dataFormat)
    {
#pragma warning disable CS0618
        if (dataFormat == DataFormats.Text)
            return DataTransfer.TryGetText();

        if (dataFormat == DataFormats.Files)
            return DataTransfer.TryGetFiles();

        if (dataFormat == DataFormats.FileNames)
        {
            return DataTransfer
                .TryGetFiles()
                ?.Select(static file => file.TryGetLocalPath())
                .Where(static path => path is not null)
                .ToArray();
        }
#pragma warning restore CS0618

        return DataTransfer.TryGetValue(DataFormat.CreateBytesPlatformFormat(dataFormat));
    }
}
