using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BetterBluetoothAudioConnector.Diagnostics;
using BetterBluetoothAudioConnector.Models;
using BetterBluetoothAudioConnector.Services;
using Microsoft.UI.Dispatching;

namespace BetterBluetoothAudioConnector.ViewModels
{
    public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IBluetoothAudioService service;
        private readonly IDiagnosticLogger logger;
        private readonly IUiDispatcher dispatcher;
        private DeviceItemViewModel selectedDevice;
        private ConnectionSnapshot connection = ConnectionSnapshot.Idle;
        private string watcherStatusText = "Preparing device monitoring...";
        private bool disposed;

        public MainViewModel(
            IBluetoothAudioService service,
            IDiagnosticLogger logger,
            DispatcherQueue dispatcherQueue)
            : this(service, logger, new DispatcherQueueAdapter(dispatcherQueue))
        {
        }

        internal MainViewModel(
            IBluetoothAudioService service,
            IDiagnosticLogger logger,
            IUiDispatcher dispatcher)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

            Devices = new ObservableCollection<DeviceItemViewModel>();
            service.DevicesChanged += Service_DevicesChanged;
            service.ConnectionChanged += Service_ConnectionChanged;
            service.WatcherStateChanged += Service_WatcherStateChanged;
            ApplyDevices(service.Devices);
            ApplyConnection(service.CurrentConnection);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public ObservableCollection<DeviceItemViewModel> Devices { get; }

        public DeviceItemViewModel SelectedDevice
        {
            get => selectedDevice;
            set
            {
                if (selectedDevice == value)
                {
                    return;
                }

                selectedDevice = value;
                OnPropertyChanged();
                RaiseCommandProperties();
            }
        }

        public AudioConnectionState ConnectionState => connection.State;

        public string ConnectionStatusText => connection.State switch
        {
            AudioConnectionState.Idle => "Idle",
            AudioConnectionState.Connecting => BuildConnectingText(),
            AudioConnectionState.Connected => "Connected",
            AudioConnectionState.Canceling => "Canceling...",
            AudioConnectionState.Disconnecting => "Disconnecting...",
            AudioConnectionState.Disconnected => "Disconnected",
            AudioConnectionState.TimedOut => BuildErrorText("Connection timed out"),
            AudioConnectionState.Failed => BuildErrorText(
                connection.ErrorSummary ?? "Connection failed"),
            _ => "Idle"
        };

        public string WatcherStatusText
        {
            get => watcherStatusText;
            private set
            {
                if (watcherStatusText == value)
                {
                    return;
                }

                watcherStatusText = value;
                OnPropertyChanged();
            }
        }

        public bool CanConnect =>
            SelectedDevice != null &&
            SelectedDevice.Availability != DeviceAvailability.Offline &&
            connection.State != AudioConnectionState.Connecting &&
            connection.State != AudioConnectionState.Canceling &&
            connection.State != AudioConnectionState.Disconnecting &&
            connection.State != AudioConnectionState.Connected;

        public bool CanDisconnect =>
            connection.State == AudioConnectionState.Connecting ||
            connection.State == AudioConnectionState.Connected;

        public bool CanReconnect =>
            SelectedDevice != null &&
            connection.State == AudioConnectionState.Connected &&
            connection.DeviceId == SelectedDevice.Id;

        public bool CanSelectDevice =>
            connection.State != AudioConnectionState.Connecting &&
            connection.State != AudioConnectionState.Canceling &&
            connection.State != AudioConnectionState.Disconnecting;

        public string DisconnectButtonText =>
            connection.State == AudioConnectionState.Connecting ||
            connection.State == AudioConnectionState.Canceling
                ? "Cancel"
                : "Disconnect";

        public void Start()
        {
            service.StartWatching();
        }

        public async Task ConnectSelectedAsync()
        {
            DeviceItemViewModel device = SelectedDevice;
            if (device != null && CanConnect)
            {
                await service.ConnectAsync(device.Id);
            }
        }

        public async Task DisconnectOrCancelAsync()
        {
            if (connection.State == AudioConnectionState.Connecting)
            {
                service.CancelCurrentOperation();
            }
            else if (connection.State == AudioConnectionState.Connected)
            {
                await service.DisconnectAsync();
            }
        }

        public async Task ReconnectSelectedAsync()
        {
            DeviceItemViewModel device = SelectedDevice;
            if (device != null && CanReconnect)
            {
                await service.ReconnectAsync(device.Id);
            }
        }

        public void OpenLogDirectory()
        {
            try
            {
                System.IO.Directory.CreateDirectory(logger.LogDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = logger.LogDirectory,
                    UseShellExecute = true
                });
                logger.Log(DiagnosticLevel.Information, "Diagnostics.LogDirectoryOpened");
            }
            catch (Exception ex)
            {
                logger.Log(
                    DiagnosticLevel.Warning,
                    "Diagnostics.OpenLogDirectoryFailed",
                    exception: ex);
                WatcherStatusText = "Could not open the diagnostic log folder";
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            service.DevicesChanged -= Service_DevicesChanged;
            service.ConnectionChanged -= Service_ConnectionChanged;
            service.WatcherStateChanged -= Service_WatcherStateChanged;
        }

        private void Service_DevicesChanged(object sender, DevicesChangedEventArgs args)
        {
            Enqueue(() => ApplyDevices(args.Devices));
        }

        private void Service_ConnectionChanged(object sender, ConnectionChangedEventArgs args)
        {
            Enqueue(() => ApplyConnection(args.Connection));
        }

        private void Service_WatcherStateChanged(
            object sender,
            WatcherStateChangedEventArgs args)
        {
            Enqueue(() => WatcherStatusText = args.Message);
        }

        private void ApplyDevices(IReadOnlyList<BluetoothDeviceSnapshot> snapshots)
        {
            string selectedId = SelectedDevice?.Id;
            Dictionary<string, DeviceItemViewModel> existing = Devices
                .ToDictionary(device => device.Id, StringComparer.Ordinal);

            for (int index = 0; index < snapshots.Count; index++)
            {
                BluetoothDeviceSnapshot snapshot = snapshots[index];
                if (existing.TryGetValue(snapshot.Id, out DeviceItemViewModel item))
                {
                    item.Update(snapshot);
                    existing.Remove(snapshot.Id);

                    int currentIndex = Devices.IndexOf(item);
                    if (currentIndex != index)
                    {
                        Devices.Move(currentIndex, index);
                    }
                }
                else
                {
                    Devices.Insert(index, new DeviceItemViewModel(snapshot));
                }
            }

            foreach (DeviceItemViewModel removed in existing.Values)
            {
                Devices.Remove(removed);
            }

            if (selectedId != null)
            {
                SelectedDevice = Devices.FirstOrDefault(device => device.Id == selectedId);
            }

            RaiseCommandProperties();
        }

        private void ApplyConnection(ConnectionSnapshot snapshot)
        {
            connection = snapshot ?? ConnectionSnapshot.Idle;
            OnPropertyChanged(nameof(ConnectionState));
            OnPropertyChanged(nameof(ConnectionStatusText));
            RaiseCommandProperties();
        }

        private string BuildConnectingText()
        {
            string stage = connection.Stage == "Open" ? "opening" : "preparing";
            string attempt = connection.Attempt > 1
                ? " (attempt " + connection.Attempt + "/" +
                    connection.MaximumAttempts + ")"
                : string.Empty;
            return "Connecting — " + stage + attempt;
        }

        private string BuildErrorText(string message)
        {
            return string.IsNullOrWhiteSpace(connection.CorrelationId)
                ? "Error: " + message
                : "Error: " + message + " (ID " + connection.CorrelationId + ")";
        }

        private void RaiseCommandProperties()
        {
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanDisconnect));
            OnPropertyChanged(nameof(CanReconnect));
            OnPropertyChanged(nameof(CanSelectDevice));
            OnPropertyChanged(nameof(DisconnectButtonText));
        }

        private void Enqueue(Action action)
        {
            if (disposed)
            {
                return;
            }

            if (dispatcher.HasThreadAccess)
            {
                action();
                return;
            }

            if (!dispatcher.TryEnqueue(() =>
                {
                    if (!disposed)
                    {
                        action();
                    }
                }))
            {
                logger.Log(
                    DiagnosticLevel.Warning,
                    "ViewModel.DispatchFailed",
                    "The UI dispatcher rejected a state update");
            }
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class DeviceItemViewModel : INotifyPropertyChanged
    {
        private BluetoothDeviceSnapshot snapshot;

        public DeviceItemViewModel(BluetoothDeviceSnapshot snapshot)
        {
            this.snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public string Id => snapshot.Id;

        public string Name => snapshot.Name;

        public DeviceAvailability Availability => snapshot.Availability;

        public string StatusText
        {
            get
            {
                if (snapshot.IsAudioConnected)
                {
                    return "Connected";
                }

                if (snapshot.IsSystemConnected)
                {
                    return "Bluetooth connected";
                }

                return snapshot.Availability switch
                {
                    DeviceAvailability.Checking => "Checking...",
                    DeviceAvailability.Nearby => "Nearby",
                    DeviceAvailability.Offline => "Offline",
                    _ => "Status unknown"
                };
            }
        }

        public void Update(BluetoothDeviceSnapshot updatedSnapshot)
        {
            snapshot = updatedSnapshot ?? throw new ArgumentNullException(
                nameof(updatedSnapshot));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Availability)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        }
    }
}
