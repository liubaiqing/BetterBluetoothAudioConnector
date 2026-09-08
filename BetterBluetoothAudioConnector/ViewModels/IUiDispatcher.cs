using System;
using Microsoft.UI.Dispatching;

namespace BetterBluetoothAudioConnector.ViewModels
{
    internal interface IUiDispatcher
    {
        bool HasThreadAccess { get; }

        bool TryEnqueue(Action action);
    }

    internal sealed class DispatcherQueueAdapter : IUiDispatcher
    {
        private readonly DispatcherQueue dispatcherQueue;

        public DispatcherQueueAdapter(DispatcherQueue dispatcherQueue)
        {
            this.dispatcherQueue = dispatcherQueue ??
                throw new ArgumentNullException(nameof(dispatcherQueue));
        }

        public bool HasThreadAccess => dispatcherQueue.HasThreadAccess;

        public bool TryEnqueue(Action action)
        {
            return dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () => action());
        }
    }
}
