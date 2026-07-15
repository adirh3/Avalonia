using System;
using System.Collections.Generic;

namespace Avalonia.Input;

/// <summary>
/// Interface to access information about the data of a drag-and-drop operation.
/// </summary>
[Obsolete($"Use {nameof(IDataTransfer)} or {nameof(IAsyncDataTransfer)} instead")]
public interface IDataObject
{
    /// <summary>
    /// Lists all formats which are present in the data object.
    /// </summary>
    IEnumerable<string> GetDataFormats();

    /// <summary>
    /// Checks whether a given data format is present in this object.
    /// </summary>
    bool Contains(string dataFormat);

    /// <summary>
    /// Tries to get the data of the given data format.
    /// </summary>
    object? Get(string dataFormat);
}
