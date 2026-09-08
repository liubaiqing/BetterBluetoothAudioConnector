using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BetterBluetoothAudioConnector.Models;
using BetterBluetoothAudioConnector.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Media;

namespace BetterBluetoothAudioConnector
{
    /// <summary>
    /// Desktop UI adapter. Bluetooth state and connection ownership live in the
    /// service/view-model layers rather than in window event handlers.
    /// </summary>
    public sealed partial class MainPage : Window
    {
        private const int DefaultDpi = 96;
        private const int InitialClientWidthDips = 360;
        private const int InitialClientHeightDips = 320;
        private const int MinimumWindowWidthDips = 340;
        private const int MinimumWindowHeightDips = 280;

        private bool isLoaded;
        private bool isWindowConfigured;
        private SystemMediaTransportControls systemMediaTransportControls;

        public MainPage(MainViewModel viewModel)
        {
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            Activated += MainPage_Activated;
            Closed += MainPage_Closed;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        public MainViewModel ViewModel { get; }

        private void MainGrid_Loaded(object sender, RoutedEventArgs e)
        {
            if (isLoaded)
            {
                return;
            }

            isLoaded = true;
            InitializeSystemMediaTransportControls();
            ViewModel.Start();
        }

        private async void MainPage_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (isWindowConfigured ||
                args.WindowActivationState == WindowActivationState.Deactivated)
            {
                return;
            }

            isWindowConfigured = true;
            Activated -= MainPage_Activated;
            await Task.Delay(100);
            ConfigureWindow();
        }

        private void MainPage_Closed(object sender, WindowEventArgs args)
        {
            Activated -= MainPage_Activated;
            Closed -= MainPage_Closed;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ReleaseSystemMediaTransportControls();
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ConnectSelectedAsync();
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.DisconnectOrCancelAsync();
        }

        private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
        {
            await ViewModel.ReconnectSelectedAsync();
        }

        private void OpenLogsButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.OpenLogDirectory();
        }

        private void SystemMediaTransportControls_ButtonPressed(
            SystemMediaTransportControls sender,
            SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                if (args.Button == SystemMediaTransportControlsButton.Play &&
                    ViewModel.CanConnect)
                {
                    await ViewModel.ConnectSelectedAsync();
                }
                else if (args.Button == SystemMediaTransportControlsButton.Pause &&
                         ViewModel.ConnectionState == AudioConnectionState.Connected)
                {
                    await ViewModel.DisconnectOrCancelAsync();
                }
            });
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainViewModel.ConnectionState))
            {
                UpdateSystemMediaPlaybackStatus();
            }
        }

        private void InitializeSystemMediaTransportControls()
        {
            if (systemMediaTransportControls != null)
            {
                return;
            }

            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            systemMediaTransportControls =
                SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
            systemMediaTransportControls.IsEnabled = true;
            systemMediaTransportControls.IsPlayEnabled = true;
            systemMediaTransportControls.IsPauseEnabled = true;
            systemMediaTransportControls.ButtonPressed +=
                SystemMediaTransportControls_ButtonPressed;
            systemMediaTransportControls.DisplayUpdater.Type = MediaPlaybackType.Music;
            systemMediaTransportControls.DisplayUpdater.MusicProperties.Title =
                "Better Bluetooth Audio Connector";
            systemMediaTransportControls.DisplayUpdater.MusicProperties.Artist =
                "Remote Bluetooth audio";
            systemMediaTransportControls.DisplayUpdater.Update();
            UpdateSystemMediaPlaybackStatus();
        }

        private void UpdateSystemMediaPlaybackStatus()
        {
            if (systemMediaTransportControls == null)
            {
                return;
            }

            systemMediaTransportControls.PlaybackStatus = ViewModel.ConnectionState switch
            {
                AudioConnectionState.Connected => MediaPlaybackStatus.Playing,
                AudioConnectionState.Connecting or
                AudioConnectionState.Canceling or
                AudioConnectionState.Disconnecting => MediaPlaybackStatus.Changing,
                _ => MediaPlaybackStatus.Stopped
            };
        }

        private void ReleaseSystemMediaTransportControls()
        {
            if (systemMediaTransportControls == null)
            {
                return;
            }

            systemMediaTransportControls.ButtonPressed -=
                SystemMediaTransportControls_ButtonPressed;
            systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Closed;
            systemMediaTransportControls.IsEnabled = false;
            systemMediaTransportControls = null;
        }

        private void ConfigureWindow()
        {
            IntPtr windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            string iconPath = Path.Combine(
                AppContext.BaseDirectory,
                "Assets",
                "BetterBluetoothAudioConnector.ico");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }

            uint windowDpi = GetDpiForWindow(windowHandle);
            double scale = windowDpi > 0 ? windowDpi / (double)DefaultDpi : 1.0;
            AppWindow.ResizeClient(new SizeInt32(
                ScaleToPixels(InitialClientWidthDips, scale),
                ScaleToPixels(InitialClientHeightDips, scale)));

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.PreferredMinimumWidth =
                    ScaleToPixels(MinimumWindowWidthDips, scale);
                presenter.PreferredMinimumHeight =
                    ScaleToPixels(MinimumWindowHeightDips, scale);
            }
        }

        private static int ScaleToPixels(int dips, double scale)
        {
            return (int)Math.Ceiling(dips * scale);
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr windowHandle);
    }
}
