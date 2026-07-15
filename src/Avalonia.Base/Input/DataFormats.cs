using System;
using System.ComponentModel;

namespace Avalonia.Input;

/// <summary>
/// Legacy string-based data-format identifiers.
/// </summary>
public static class DataFormats
{
    /// <summary>
    /// Data format for plain text.
    /// </summary>
    [Obsolete($"Use {nameof(DataFormat)}.{nameof(DataFormat.Text)} instead.")]
    public static readonly string Text = nameof(Text);

    /// <summary>
    /// Data format for one or more files.
    /// </summary>
    [Obsolete($"Use {nameof(DataFormat)}.{nameof(DataFormat.File)} instead.")]
    public static readonly string Files = nameof(Files);

    /// <summary>
    /// Data format for one or more file names.
    /// </summary>
    [Obsolete($"Use {nameof(DataFormat)}.{nameof(DataFormat.File)} instead."), EditorBrowsable(EditorBrowsableState.Never)]
    public static readonly string FileNames = nameof(FileNames);

#pragma warning disable CS0618

    internal static DataFormat ToDataFormat(string format)
    {
        if (format == Text)
            return DataFormat.Text;

        if (format == Files || format == FileNames)
            return DataFormat.File;

        return DataFormat.CreateBytesPlatformFormat(format);
    }

    internal static string ToString(DataFormat format)
    {
        if (DataFormat.Text.Equals(format))
            return Text;

        if (DataFormat.File.Equals(format))
            return Files;

        return format.Identifier;
    }

#pragma warning restore CS0618
}
