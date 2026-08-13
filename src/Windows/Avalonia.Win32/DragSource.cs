using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32
{
    internal sealed class DragSource : IPlatformDragSource
    {
        public Task<DragDropEffects> DoDragDropAsync(
            PointerPressedEventArgs triggerEvent,
            IDataTransfer dataTransfer,
            DragDropEffects allowedEffects)
        {
            Dispatcher.UIThread.VerifyAccess();

            triggerEvent.Pointer.Capture(null);
            
            using var dataObject = new DataTransferToOleDataObjectWrapper(dataTransfer);
            dataObject.SetAsyncMode(dataTransfer.Contains(DataFormat.File));
            using var src = new OleDragSource();
            var allowed = OleDropTarget.ConvertDropEffect(allowedEffects);
            
            var objPtr = dataObject.GetNativeIntPtr<Win32Com.IDataObject>();
            var srcPtr = src.GetNativeIntPtr<Win32Com.IDropSource>();

            UnmanagedMethods.DoDragDrop(objPtr, srcPtr, (int)allowed, out var finalEffect);
            
            // Async shell targets keep using the data object after DoDragDrop returns.
            dataObject.ReleaseDataTransferIfNotInOperation();

            return Task.FromResult(OleDropTarget.ConvertDropEffect((Win32Com.DropEffect)finalEffect));
        }
    }
}
