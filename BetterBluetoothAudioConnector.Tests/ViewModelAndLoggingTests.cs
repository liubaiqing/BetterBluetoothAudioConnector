using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterBluetoothAudioConnector.Diagnostics;
using BetterBluetoothAudioConnector.Models;
using BetterBluetoothAudioConnector.Services;
using BetterBluetoothAudioConnector.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BetterBluetoothAudioConnector.Tests
{
    [TestClass]
    public sealed class ViewModelAndLoggingTests
    {
        [TestMethod]
        public void ConnectingStateTurnsDisconnectIntoCancel()
        {
            FakeBluetoothService service = new FakeBluetoothService();
            using MainViewModel viewModel = new MainViewModel(
                service,
                new FakeLogger(),
                new InlineDispatcher());
            service.SetDevices(new BluetoothDeviceSnapshot(
                "device-1",
                Guid.NewGuid(),
                "Test phone",
                DeviceAvailability.Nearby,
                false,
                false));
            viewModel.SelectedDevice = viewModel.Devices[0];

            service.SetConnection(new ConnectionSnapshot(
                AudioConnectionState.Connecting,
                "device-1",
                "Open",
                1,
                2,
                "DEVICE000001",
                null,
                "TEST0001"));

            Assert.AreEqual("Cancel", viewModel.DisconnectButtonText);
            Assert.IsTrue(viewModel.CanDisconnect);
            Assert.IsFalse(viewModel.CanConnect);
            Assert.IsFalse(viewModel.CanSelectDevice);
        }

        [TestMethod]
        public void OfflineDeviceCannotConnectButUnknownDeviceCan()
        {
            FakeBluetoothService service = new FakeBluetoothService();
            using MainViewModel viewModel = new MainViewModel(
                service,
                new FakeLogger(),
                new InlineDispatcher());

            service.SetDevices(new BluetoothDeviceSnapshot(
                "device-1",
                Guid.NewGuid(),
                "Test phone",
                DeviceAvailability.Offline,
                false,
                false));
            viewModel.SelectedDevice = viewModel.Devices[0];
            Assert.IsFalse(viewModel.CanConnect);
            Assert.AreEqual("Offline", viewModel.Devices[0].StatusText);

            service.SetDevices(new BluetoothDeviceSnapshot(
                "device-1",
                Guid.NewGuid(),
                "Test phone",
                DeviceAvailability.Unknown,
                false,
                false));
            Assert.IsTrue(viewModel.CanConnect);
            Assert.AreEqual("Status unknown", viewModel.Devices[0].StatusText);
        }

        [TestMethod]
        public void WatcherRestartStateIsShownToTheUser()
        {
            FakeBluetoothService service = new FakeBluetoothService();
            using MainViewModel viewModel = new MainViewModel(
                service,
                new FakeLogger(),
                new InlineDispatcher());

            service.SetWatcherState(DeviceWatcherState.Retrying, "Bluetooth watcher stopped; retrying in 2 seconds");

            Assert.AreEqual(
                "Bluetooth watcher stopped; retrying in 2 seconds",
                viewModel.WatcherStatusText);
        }

        [TestMethod]
        public void FileLoggerUsesStableAnonymousIdsAndRotates()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "BetterBluetoothAudioConnectorTests-" + Guid.NewGuid().ToString("N"));

            try
            {
                string firstId;
                using (FileDiagnosticLogger logger = new FileDiagnosticLogger(
                    directory,
                    250,
                    3,
                    7))
                {
                    firstId = logger.GetAnonymousDeviceId("raw-device-id-with-address");
                    for (int index = 0; index < 30; index++)
                    {
                        logger.Log(
                            DiagnosticLevel.Information,
                            "Test.Entry",
                            fields: new[]
                            {
                                ("index", (object)index),
                                ("device", firstId)
                            });
                    }
                }

                using (FileDiagnosticLogger logger = new FileDiagnosticLogger(
                    directory,
                    250,
                    3,
                    7))
                {
                    Assert.AreEqual(
                        firstId,
                        logger.GetAnonymousDeviceId("raw-device-id-with-address"));
                    Assert.AreEqual(12, firstId.Length);
                }

                string[] logs = Directory.GetFiles(
                    Path.Combine(directory, "Logs"),
                    "*.log");
                Assert.IsTrue(logs.Length >= 2);
                Assert.IsTrue(logs.Length <= 3);
                Assert.IsFalse(File.ReadAllText(logs[0]).Contains(
                    "raw-device-id-with-address",
                    StringComparison.Ordinal));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }
    }

    internal sealed class InlineDispatcher : IUiDispatcher
    {
        public bool HasThreadAccess => true;

        public bool TryEnqueue(Action action)
        {
            action();
            return true;
        }
    }

    internal sealed class FakeBluetoothService : IBluetoothAudioService
    {
        private IReadOnlyList<BluetoothDeviceSnapshot> devices =
            Array.Empty<BluetoothDeviceSnapshot>();

        public event EventHandler<DevicesChangedEventArgs> DevicesChanged;

        public event EventHandler<ConnectionChangedEventArgs> ConnectionChanged;

        public event EventHandler<WatcherStateChangedEventArgs> WatcherStateChanged;

        public IReadOnlyList<BluetoothDeviceSnapshot> Devices => devices;

        public ConnectionSnapshot CurrentConnection { get; private set; } =
            ConnectionSnapshot.Idle;

        public void SetDevices(params BluetoothDeviceSnapshot[] snapshots)
        {
            devices = snapshots;
            DevicesChanged?.Invoke(this, new DevicesChangedEventArgs(devices));
        }

        public void SetConnection(ConnectionSnapshot snapshot)
        {
            CurrentConnection = snapshot;
            ConnectionChanged?.Invoke(this, new ConnectionChangedEventArgs(snapshot));
        }

        public void SetWatcherState(DeviceWatcherState state, string message)
        {
            WatcherStateChanged?.Invoke(
                this,
                new WatcherStateChangedEventArgs(state, message));
        }

        public void StartWatching()
        {
        }

        public Task ConnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void CancelCurrentOperation()
        {
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task ReconnectAsync(string deviceId, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
