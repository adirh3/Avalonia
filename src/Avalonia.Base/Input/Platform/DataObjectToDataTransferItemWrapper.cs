using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Avalonia.Input.Platform;

/// <summary>
/// Wraps a legacy <see cref="IDataObject"/> into a <see cref="IDataTransferItem"/>.
/// </summary>
[Obsolete]
internal sealed class DataObjectToDataTransferItemWrapper(
    IDataObject dataObject,
    DataFormat[] formats,
    string[] formatStrings)
    : PlatformDataTransferItem
{
    private readonly IDataObject _dataObject = dataObject;
    private readonly DataFormat[] _formats = formats;
    private readonly string[] _formatStrings = formatStrings;

    protected override DataFormat[] ProvideFormats()
        => _formats;

    protected override object? TryGetRawCore(DataFormat format)
    {
        var index = Array.IndexOf(Formats, format);
        if (index < 0)
            return null;

        Debug.Assert(!DataFormat.File.Equals(format));

        var data = _dataObject.Get(_formatStrings[index]);

        if (DataFormat.Text.Equals(format))
            return Convert.ToString(data) ?? string.Empty;

        if (format is DataFormat<string>)
            return Convert.ToString(data);

        if (format is DataFormat<byte[]>)
            return ConvertLegacyDataToBytes(data);

        return null;
    }

    private static byte[]? ConvertLegacyDataToBytes(object? data)
    {
        switch (data)
        {
            case null:
                return null;
            case byte[] bytes:
                return bytes;
            case string text:
                return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsIOS()
                    ? Encoding.Unicode.GetBytes(text)
                    : Encoding.UTF8.GetBytes(text);
            case Stream stream:
                var length = checked((int)(stream.Length - stream.Position));
                var buffer = new byte[length];
                stream.ReadExactly(buffer);
                return buffer;
            default:
                return null;
        }
    }
}
