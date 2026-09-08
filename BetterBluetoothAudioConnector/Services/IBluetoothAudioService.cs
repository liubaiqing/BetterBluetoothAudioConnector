using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterBluetoothAudioConnector.Models;

namespace BetterBluetoothAudioConnector.Services
{
    public interface IBluetoothAudioService : IDisposable
    {
        event EventHandler<DevicesChangedEventArgs> DevicesChanged;

        event EventHandler<ConnectionChangedEventArgs> ConnectionChanged;

        event EventHandler<WatcherStateChangedEventArgs> WatcherStateChanged;

        IReadOnlyList<BluetoothDeviceSnapshot> Devices { get; }

        ConnectionSnapshot CurrentConnection { get; }

        void StartWatching();

        Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default);

        void CancelCurrentOperation();

        Task DisconnectAsync(CancellationToken cancellationToken = default);

        Task ReconnectAsync(string deviceId, CancellationToken cancellationToken = default);
    }
}
