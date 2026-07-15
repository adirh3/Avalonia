using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Avalonia.Platform.Storage;
using Avalonia.Platform.Storage.FileIO;

namespace Avalonia.Input.Platform;

#pragma warning disable CS0618

/// <summary>
/// Wraps a legacy <see cref="IDataObject"/> into a <see cref="IDataTransfer"/>.
/// </summary>
[Obsolete]
internal sealed class DataObjectToDataTransferWrapper : PlatformDataTransfer
{
    private static readonly DataFormat<string> LegacyDataObjectIdFormat =
        DataFormat.CreateStringApplicationFormat("Avalonia.LegacyDataObjectId");
    private static readonly ConcurrentDictionary<string, IDataObject> LegacyDataObjects = new();

    private readonly string _legacyDataObjectId = Guid.NewGuid().ToString("N");
    private int _disposed;

    public DataObjectToDataTransferWrapper(IDataObject dataObject)
    {
        ArgumentNullException.ThrowIfNull(dataObject);
        DataObject = dataObject;
        LegacyDataObjects[_legacyDataObjectId] = dataObject;
    }

    public IDataObject DataObject { get; }

    protected override DataFormat[] ProvideFormats()
        => DataObject
            .GetDataFormats()
            .Select(DataFormats.ToDataFormat)
            .Append(LegacyDataObjectIdFormat)
            .Distinct()
            .ToArray();

    protected override PlatformDataTransferItem[] ProvideItems()
    {
        var items = new List<PlatformDataTransferItem>();
        var nonFileFormats = new List<DataFormat>();
        var nonFileFormatStrings = new List<string>();
        var hasFiles = false;

        foreach (var formatString in DataObject.GetDataFormats())
        {
            var format = DataFormats.ToDataFormat(formatString);

            if (formatString == DataFormats.Files)
            {
                if (hasFiles)
                    continue;

                if (DataObject.Get(formatString) is IEnumerable<IStorageItem> storageItems)
                {
                    hasFiles = true;

                    foreach (var storageItem in storageItems)
                        items.Add(PlatformDataTransferItem.Create(DataFormat.File, storageItem));
                }
            }
            else if (formatString == DataFormats.FileNames)
            {
                if (hasFiles)
                    continue;

                if (DataObject.Get(formatString) is IEnumerable<string> fileNames)
                {
                    hasFiles = true;

                    foreach (var fileName in fileNames)
                    {
                        if (StorageProviderHelpers.TryCreateBclStorageItem(fileName) is { } storageItem)
                            items.Add(PlatformDataTransferItem.Create(DataFormat.File, storageItem));
                    }
                }
            }
            else
            {
                nonFileFormats.Add(format);
                nonFileFormatStrings.Add(formatString);
            }
        }

        if (nonFileFormats.Count > 0)
        {
            Debug.Assert(nonFileFormats.Count == nonFileFormatStrings.Count);
            items.Add(new DataObjectToDataTransferItemWrapper(
                DataObject,
                nonFileFormats.ToArray(),
                nonFileFormatStrings.ToArray()));
        }

        items.Add(PlatformDataTransferItem.Create(LegacyDataObjectIdFormat, _legacyDataObjectId));
        return items.ToArray();
    }

    internal static bool TryGetDataObject(IDataTransfer dataTransfer, out IDataObject dataObject)
    {
        if (dataTransfer is DataObjectToDataTransferWrapper wrapper)
        {
            dataObject = wrapper.DataObject;
            return true;
        }

        if (dataTransfer.TryGetValue(LegacyDataObjectIdFormat) is { } dataObjectId &&
            LegacyDataObjects.TryGetValue(dataObjectId, out var storedDataObject))
        {
            dataObject = storedDataObject;
            return true;
        }

        dataObject = null!;
        return false;
    }

    [SuppressMessage(
        "ReSharper",
        "SuspiciousTypeConversion.Global",
        Justification = "IDisposable may be implemented externally by the IDataObject instance.")]
    public override void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        LegacyDataObjects.TryRemove(_legacyDataObjectId, out _);
        (DataObject as IDisposable)?.Dispose();
    }
}

#pragma warning restore CS0618
