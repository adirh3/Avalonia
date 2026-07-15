using System;
using System.Collections.Generic;

namespace Avalonia.Input;

/// <summary>
/// Specific and mutable implementation of the legacy <see cref="IDataObject"/> interface.
/// </summary>
[Obsolete($"Use {nameof(DataTransfer)} instead")]
public class DataObject : IDataObject
{
    private readonly Dictionary<string, object> _items = new();

    /// <inheritdoc />
    public bool Contains(string dataFormat)
        => _items.ContainsKey(dataFormat);

    /// <inheritdoc />
    public object? Get(string dataFormat)
        => _items.TryGetValue(dataFormat, out var item) ? item : null;

    /// <inheritdoc />
    public IEnumerable<string> GetDataFormats()
        => _items.Keys;

    /// <summary>
    /// Sets a value in the internal store using a <see cref="DataFormats"/> key.
    /// </summary>
    public void Set(string dataFormat, object value)
        => _items[dataFormat] = value;
}
