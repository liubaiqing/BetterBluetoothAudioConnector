using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bluetooth_Audio_Reveicer
{
    /// <summary>
    /// Main page: lists nearby Bluetooth audio devices and lets the user open/close
    /// an AudioPlaybackConnection so the PC can play audio coming from a phone.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private DeviceWatcher deviceWatcher;

        private readonly Dictionary<string, AudioPlaybackConnection> audioPlaybackConnections =
            new Dictionary<string, AudioPlaybackConnection>();

        private readonly ObservableCollection<DeviceInformation> audioPlaybackDevices =
            new ObservableCollection<DeviceInformation>();

        private string selectedDeviceId;
        private AudioPlaybackConnection selectedConnection;

        public MainPage()
        {
            InitializeComponent();

            DeviceListView.ItemsSource = audioPlaybackDevices;
        }

        private void MainGrid_Loaded(object sender, RoutedEventArgs e)
        {
            StartWatchingDevices();
        }

        private void StartWatchingDevices()
        {
            if (deviceWatcher != null)
            {
                return;
            }

            // AudioPlaybackConnection exposes devices that support the
            // "PC as Bluetooth audio receiver" scenario.
            string selector = AudioPlaybackConnection.GetDeviceSelector();

            deviceWatcher = DeviceInformation.CreateWatcher(selector);
            deviceWatcher.Added += DeviceWatcher_Added;
            deviceWatcher.Removed += DeviceWatcher_Removed;
            deviceWatcher.Start();
        }

        private async void DeviceWatcher_Added(DeviceWatcher sender, DeviceInformation deviceInfo)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (audioPlaybackDevices.Any(d => d.Id == deviceInfo.Id))
                {
                    return;
                }

                audioPlaybackDevices.Add(deviceInfo);
            });
        }

        private async void DeviceWatcher_Removed(DeviceWatcher sender, DeviceInformationUpdate deviceInfoUpdate)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                DeviceInformation device = audioPlaybackDevices.FirstOrDefault(d => d.Id == deviceInfoUpdate.Id);
                if (device != null)
                {
                    audioPlaybackDevices.Remove(device);
                }

                if (audioPlaybackConnections.TryGetValue(deviceInfoUpdate.Id, out AudioPlaybackConnection connection))
                {
                    connection.StateChanged -= AudioPlaybackConnection_ConnectionStateChanged;
                    connection.Dispose();
                    audioPlaybackConnections.Remove(deviceInfoUpdate.Id);
                }

                if (selectedDeviceId == deviceInfoUpdate.Id)
                {
                    selectedDeviceId = null;
                    selectedConnection = null;
                    UpdateButtons();
                }
            });
        }

        private void DeviceListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            selectedDeviceId = (DeviceListView.SelectedItem as DeviceInformation)?.Id;

            if (selectedDeviceId != null &&
                audioPlaybackConnections.TryGetValue(selectedDeviceId, out AudioPlaybackConnection existing))
            {
                selectedConnection = existing;
            }
            else
            {
                selectedConnection = null;
            }

            UpdateButtons();
        }

        private async void OpenAudioPlaybackConnectionButtonButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(selectedDeviceId))
            {
                return;
            }

            SetStateConnecting();

            try
            {
                AudioPlaybackConnection connection = AudioPlaybackConnection.TryCreateFromId(selectedDeviceId);
                if (connection == null)
                {
                    SetError("device not supported");
                    return;
                }

                AudioPlaybackConnectionOpenResult openResult = await connection.OpenAsync();
                if (openResult.Status != AudioPlaybackConnectionOpenResultStatus.Success)
                {
                    SetError(GetOpenResultErrorMessage(openResult));
                    return;
                }

                connection.StateChanged += AudioPlaybackConnection_ConnectionStateChanged;
                audioPlaybackConnections[selectedDeviceId] = connection;
                selectedConnection = connection;

                connection.Start();

                UpdateButtons();

                // If the state event did not fire before Start() returns, reflect Opened now.
                if (connection.State == AudioPlaybackConnectionState.Opened)
                {
                    SetStateConnected();
                }
            }
            catch (Exception ex)
            {
                SetError("attempt failed: " + ex.Message);
            }
        }

        private void CloseAudioPlaybackConnectionButtonButton_Click(object sender, RoutedEventArgs e)
        {
            if (selectedConnection == null)
            {
                return;
            }

            selectedConnection.StateChanged -= AudioPlaybackConnection_ConnectionStateChanged;
            selectedConnection.Dispose();

            if (selectedDeviceId != null)
            {
                audioPlaybackConnections.Remove(selectedDeviceId);
            }

            selectedConnection = null;

            SetStateDisconnected();
            UpdateButtons();
        }

        private async void AudioPlaybackConnection_ConnectionStateChanged(AudioPlaybackConnection sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (sender.State == AudioPlaybackConnectionState.Opened)
                {
                    SetStateConnected();
                }
                else
                {
                    SetStateDisconnected();
                }
            });
        }

        private void UpdateButtons()
        {
            bool hasDevice = selectedDeviceId != null;
            bool hasOpenConnection = selectedConnection != null;

            OpenAudioPlaybackConnectionButtonButton.IsEnabled = hasDevice && !hasOpenConnection;
            CloseAudioPlaybackConnectionButtonButton.IsEnabled = hasDevice && hasOpenConnection;
        }

        private void SetStateConnecting()
        {
            ConnectionState.Text = "Connecting...";
        }

        private void SetStateConnected()
        {
            ConnectionState.Text = "Connected";
        }

        private void SetStateDisconnected()
        {
            ConnectionState.Text = "Disconnected";
        }

        private void SetError(string message)
        {
            ConnectionState.Text = "Error: " + message;
        }

        private static string GetOpenResultErrorMessage(AudioPlaybackConnectionOpenResult openResult)
        {
            switch (openResult.Status)
            {
                case AudioPlaybackConnectionOpenResultStatus.DeniedBySystem:
                    return "denied by system";
                case AudioPlaybackConnectionOpenResultStatus.RequestTimedOut:
                    return "request timed out";
                case AudioPlaybackConnectionOpenResultStatus.UnknownFailure:
                    return "attempt failed";
                default:
                    return "attempt failed";
            }
        }
    }
}
