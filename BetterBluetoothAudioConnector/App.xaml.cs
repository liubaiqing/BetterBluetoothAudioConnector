using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BetterBluetoothAudioConnector.Diagnostics;
using BetterBluetoothAudioConnector.Services;
using BetterBluetoothAudioConnector.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BetterBluetoothAudioConnector
{
    /// <summary>
    /// Composition root for the unpackaged desktop application.
    /// </summary>
    public sealed partial class App : Application
    {
        private FileDiagnosticLogger logger;
        private BluetoothAudioService bluetoothService;
        private MainViewModel mainViewModel;
        private bool shuttingDown;

        public static MainPage MainWindow { get; private set; }

        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            logger = new FileDiagnosticLogger();
            RegisterUnhandledExceptionLogging();

            logger.Log(
                DiagnosticLevel.Information,
                "Application.Started",
                fields: new[]
                {
                    ("version", (object)(Assembly.GetExecutingAssembly()
                        .GetName().Version?.ToString() ?? "unknown")),
                    ("windows", Environment.OSVersion.VersionString),
                    ("architecture", RuntimeInformation.ProcessArchitecture),
                    ("packaging", "unpackaged")
                });

            bluetoothService = new BluetoothAudioService(logger);
            mainViewModel = new MainViewModel(
                bluetoothService,
                logger,
                DispatcherQueue.GetForCurrentThread());
            MainWindow = new MainPage(mainViewModel);
            MainWindow.Closed += MainWindow_Closed;
            MainWindow.Activate();
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            if (shuttingDown)
            {
                return;
            }

            shuttingDown = true;
            MainWindow.Closed -= MainWindow_Closed;
            mainViewModel?.Dispose();
            bluetoothService?.Dispose();
            logger?.Log(DiagnosticLevel.Information, "Application.Stopped");
            UnregisterUnhandledExceptionLogging();
            logger?.Dispose();

            mainViewModel = null;
            bluetoothService = null;
            logger = null;
            MainWindow = null;
        }

        private void RegisterUnhandledExceptionLogging()
        {
            UnhandledException += App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException +=
                CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException +=
                TaskScheduler_UnobservedTaskException;
        }

        private void UnregisterUnhandledExceptionLogging()
        {
            UnhandledException -= App_UnhandledException;
            AppDomain.CurrentDomain.UnhandledException -=
                CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException -=
                TaskScheduler_UnobservedTaskException;
        }

        private void App_UnhandledException(
            object sender,
            Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            logger?.Log(
                DiagnosticLevel.Critical,
                "Application.WinUiUnhandledException",
                args.Message,
                args.Exception);
            // Do not set Handled: fatal failures retain the platform's default behavior.
        }

        private void CurrentDomain_UnhandledException(
            object sender,
            System.UnhandledExceptionEventArgs args)
        {
            logger?.Log(
                DiagnosticLevel.Critical,
                "Application.DomainUnhandledException",
                fields: new[]
                {
                    ("terminating", (object)args.IsTerminating),
                    ("exception", args.ExceptionObject?.ToString())
                });
        }

        private void TaskScheduler_UnobservedTaskException(
            object sender,
            UnobservedTaskExceptionEventArgs args)
        {
            logger?.Log(
                DiagnosticLevel.Error,
                "Application.UnobservedTaskException",
                exception: args.Exception);
            // Do not call SetObserved; retain the runtime's configured behavior.
        }
    }
}
