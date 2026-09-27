using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterBluetoothAudioConnector.Diagnostics;
using BetterBluetoothAudioConnector.Models;
using Microsoft.Windows.System.Power;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Media.Audio;

namespace BetterBluetoothAudioConnector.Services
{
    public sealed class BluetoothAudioService : IBluetoothAudioService
    {
        private const string ContainerIdProperty = "System.Devices.ContainerId";
        private const string AepContainerIdProperty = "System.Devices.Aep.ContainerId";
        private const string AepIsPresentProperty = "System.Devices.Aep.IsPresent";
        private const string AepIsConnectedProperty = "System.Devices.Aep.IsConnected";
        private const string AepIsPairedProperty = "System.Devices.Aep.IsPaired";
        private static readonly TimeSpan OfflineConfirmationDelay = TimeSpan.FromSeconds(2);
        private static readonly int[] WatcherRestartDelaysSeconds = { 1, 2, 5, 10, 30 };

        private readonly object sync = new object();
        private readonly SemaphoreSlim operationGate = new SemaphoreSlim(1, 1);
        private readonly IDiagnosticLogger logger;
        private readonly IAudioConnectionFactory connectionFactory;
        private readonly ConnectionPolicy connectionPolicy;
        private readonly Func<string, BluetoothDeviceSnapshot> deviceResolver;
        private readonly Dictionary<string, DeviceInformation> playbackDevices =
            new Dictionary<string, DeviceInformation>(StringComparer.Ordinal);
        private readonly Dictionary<string, DeviceInformation> aepDevices =
            new Dictionary<string, DeviceInformation>(StringComparer.Ordinal);
        private readonly HashSet<string> currentPlaybackGeneration =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<Guid> removedPresenceContainers =
            new HashSet<Guid>();

        private DeviceWatcher playbackWatcher;
        private DeviceWatcher presenceWatcher;
        private Radio bluetoothRadio;
        private CancellationTokenSource watcherRestartCancellation;
        private CancellationTokenSource offlineConfirmationCancellation;
        private CancellationTokenSource activeSessionCancellation;
        private ActiveOperation activeOperation;
        private ConnectionHandle pendingConnection;
        private ConnectionHandle activeConnection;
        private ConnectionHandle enabledConnection;
        private Task transportDrain = Task.CompletedTask;
        private Guid activeSessionId;
        private string activeDeviceId;
        private string activeDeviceName;
        private bool playbackEnumerationCompleted;
        private bool presenceEnumerationCompleted;
        private bool watching;
        private bool stoppingWatchers;
        private bool disposed;
        private bool desiredConnection;
        private int watcherGeneration;
        private int automaticRecoveryCount;
        private CancellationTokenSource automaticRecoveryCancellation;
        private int softWatcherRefreshScheduled;
        private int watcherRestartAttempt;
        private ConnectionSnapshot currentConnection = ConnectionSnapshot.Idle;

        public BluetoothAudioService(IDiagnosticLogger logger)
            : this(
                logger,
                new WinRtAudioConnectionFactory(),
                ConnectionPolicy.Default,
                null)
        {
        }

        internal BluetoothAudioService(
            IDiagnosticLogger logger,
            IAudioConnectionFactory connectionFactory,
            ConnectionPolicy connectionPolicy,
            Func<string, BluetoothDeviceSnapshot> deviceResolver)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.connectionFactory = connectionFactory ??
                throw new ArgumentNullException(nameof(connectionFactory));
            this.connectionPolicy = connectionPolicy ??
                throw new ArgumentNullException(nameof(connectionPolicy));
            this.deviceResolver = deviceResolver;
            PowerManager.SystemSuspendStatusChanged += PowerManager_SystemSuspendStatusChanged;
        }

        public event EventHandler<DevicesChangedEventArgs> DevicesChanged;

        public event EventHandler<ConnectionChangedEventArgs> ConnectionChanged;

        public event EventHandler<WatcherStateChangedEventArgs> WatcherStateChanged;

        public IReadOnlyList<BluetoothDeviceSnapshot> Devices
        {
            get
            {
                lock (sync)
                {
                    return BuildDeviceSnapshotsLocked();
                }
            }
        }

        public ConnectionSnapshot CurrentConnection
        {
            get
            {
                lock (sync)
                {
                    return currentConnection;
                }
            }
        }

        public void StartWatching()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                if (watching)
                {
                    return;
                }

                watching = true;
                watcherRestartAttempt = 0;
            }

            StartWatchers();
            _ = InitializeRadioMonitoringAsync();
        }

        public async Task ConnectAsync(
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return;
            }

            await operationGate.WaitAsync(cancellationToken);

            Guid sessionId = Guid.NewGuid();
            string correlationId = logger.CreateCorrelationId();
            string anonymousDeviceId = logger.GetAnonymousDeviceId(deviceId);
            CancellationTokenSource sessionCancellation = null;
            ConnectionHandle connection = null;

            try
            {
                BluetoothDeviceSnapshot device = GetDevice(deviceId);
                if (device == null || !device.IsConnectable)
                {
                    PublishConnection(new ConnectionSnapshot(
                        AudioConnectionState.Failed,
                        deviceId,
                        "Validate",
                        0,
                        connectionPolicy.MaximumAttempts,
                        anonymousDeviceId,
                        device == null
                            ? "Device is no longer available"
                            : "Bluetooth endpoint is not ready",
                        correlationId));
                    return;
                }

                sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                lock (sync)
                {
                    ThrowIfDisposed();
                    automaticRecoveryCancellation?.Cancel();
                    activeSessionId = sessionId;
                    activeSessionCancellation = sessionCancellation;
                    activeDeviceId = deviceId;
                    activeDeviceName = device.Name;
                    desiredConnection = true;
                }

                logger.Log(
                    DiagnosticLevel.Information,
                    "Connection.SessionStarted",
                    fields: new[]
                    {
                        ("correlation", (object)correlationId),
                        ("device", anonymousDeviceId),
                        ("name", device.Name)
                    });

                connection = await GetOrCreateEnabledConnectionAsync(
                    deviceId,
                    anonymousDeviceId,
                    correlationId,
                    sessionId,
                    sessionCancellation.Token);
                if (connection == null)
                {
                    PublishConnection(new ConnectionSnapshot(
                        AudioConnectionState.Failed,
                        deviceId,
                        "Create",
                        0,
                        connectionPolicy.MaximumAttempts,
                        anonymousDeviceId,
                        "Device does not support Bluetooth audio playback",
                        correlationId));
                    return;
                }

                string lastError = "Connection attempt failed";
                bool finalFailureWasTimeout = false;

                for (int attempt = 1; attempt <= connectionPolicy.MaximumAttempts; attempt++)
                {
                    sessionCancellation.Token.ThrowIfCancellationRequested();
                    EnsureSessionIsCurrent(sessionId);

                    if (!await WaitForConnectionDrainAsync(
                        connection,
                        deviceId,
                        anonymousDeviceId,
                        correlationId,
                        attempt,
                        sessionCancellation.Token))
                    {
                        finalFailureWasTimeout = true;
                        lastError = "Previous Windows Bluetooth operation is still finishing";
                        break;
                    }

                    PublishConnection(new ConnectionSnapshot(
                        AudioConnectionState.Connecting,
                        deviceId,
                        "Open",
                        attempt,
                        connectionPolicy.MaximumAttempts,
                        anonymousDeviceId,
                        null,
                        correlationId));

                    try
                    {
                        Interlocked.Exchange(ref connection.NativeClosedDuringOpen, 0);
                        lock (sync)
                        {
                            EnsureSessionIsCurrentLocked(sessionId);
                            pendingConnection = connection;
                        }

                        AudioPlaybackConnectionOpenResultStatus resultStatus =
                            await RunOperationStageAsync(
                                connection.Connection.OpenAsync(),
                                connectionPolicy.OpenTimeout,
                                sessionId,
                                connection,
                                "Open",
                                attempt,
                                sessionCancellation.Token);

                        EnsureSessionIsCurrent(sessionId);

                        if (resultStatus == AudioPlaybackConnectionOpenResultStatus.Success)
                        {
                            await Task.Delay(
                                connectionPolicy.OpenStabilityDelay,
                                sessionCancellation.Token);
                            EnsureSessionIsCurrent(sessionId);
                            ClearPendingConnection(connection);

                            if (connection.Connection.State ==
                                    AudioPlaybackConnectionState.Opened &&
                                Volatile.Read(ref connection.NativeClosedDuringOpen) == 0)
                            {
                                lock (sync)
                                {
                                    EnsureSessionIsCurrentLocked(sessionId);
                                    activeConnection = connection;
                                    desiredConnection = true;
                                    automaticRecoveryCount = 0;
                                }

                                PublishConnection(new ConnectionSnapshot(
                                    AudioConnectionState.Connected,
                                    deviceId,
                                    "Opened",
                                    attempt,
                                    connectionPolicy.MaximumAttempts,
                                    anonymousDeviceId,
                                    null,
                                    correlationId));
                                PublishDevices();
                                logger.Log(
                                    DiagnosticLevel.Information,
                                    "Connection.Connected",
                                    fields: new[]
                                    {
                                        ("correlation", (object)correlationId),
                                        ("device", anonymousDeviceId),
                                        ("attempt", attempt)
                                    });
                                return;
                            }

                            resultStatus = AudioPlaybackConnectionOpenResultStatus.UnknownFailure;
                            lastError = "Bluetooth audio closed while opening";
                            logger.Log(
                                DiagnosticLevel.Warning,
                                "Connection.OpenUnstable",
                                lastError,
                                fields: new[]
                                {
                                    ("correlation", (object)correlationId),
                                    ("device", anonymousDeviceId),
                                    ("attempt", attempt),
                                    ("nativeState", connection.Connection.State)
                                });
                        }
                        else
                        {
                            ClearPendingConnection(connection);
                            lastError = GetOpenResultErrorMessage(resultStatus);
                        }

                        finalFailureWasTimeout =
                            resultStatus == AudioPlaybackConnectionOpenResultStatus.RequestTimedOut;
                        logger.Log(
                            DiagnosticLevel.Warning,
                            "Connection.OpenRejected",
                            lastError,
                            fields: new[]
                            {
                                ("correlation", (object)correlationId),
                                ("device", anonymousDeviceId),
                                ("attempt", attempt),
                                ("status", resultStatus)
                            });

                        if (!IsRetryable(resultStatus) ||
                            attempt == connectionPolicy.MaximumAttempts)
                        {
                            break;
                        }
                    }
                    catch (ConnectionStageTimeoutException ex)
                    {
                        finalFailureWasTimeout = true;
                        lastError = ex.Stage + " timed out";
                        ClearPendingConnection(connection);
                        logger.Log(
                            DiagnosticLevel.Warning,
                            "Connection.StageTimedOut",
                            lastError,
                            ex,
                            ("correlation", correlationId),
                            ("device", anonymousDeviceId),
                            ("attempt", attempt),
                            ("timeoutMs", (int)ex.Timeout.TotalMilliseconds));

                        if (attempt == connectionPolicy.MaximumAttempts)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        ClearPendingConnection(connection);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        finalFailureWasTimeout = false;
                        lastError = "Connection attempt failed";
                        ClearPendingConnection(connection);
                        logger.Log(
                            DiagnosticLevel.Error,
                            "Connection.AttemptFailed",
                            lastError,
                            ex,
                            ("correlation", correlationId),
                            ("device", anonymousDeviceId),
                            ("attempt", attempt));

                        if (attempt == connectionPolicy.MaximumAttempts)
                        {
                            break;
                        }
                    }

                    PublishConnection(new ConnectionSnapshot(
                        AudioConnectionState.Recovering,
                        deviceId,
                        "Backoff",
                        attempt,
                        connectionPolicy.MaximumAttempts,
                        anonymousDeviceId,
                        null,
                        correlationId));
                    await Task.Delay(
                        connectionPolicy.GetRetryDelay(attempt),
                        sessionCancellation.Token);
                }

                EnsureSessionIsCurrent(sessionId);
                lock (sync)
                {
                    desiredConnection = false;
                    activeConnection = null;
                }
                PublishConnection(new ConnectionSnapshot(
                    finalFailureWasTimeout
                        ? AudioConnectionState.TimedOut
                        : AudioConnectionState.Failed,
                    deviceId,
                    "Complete",
                    connectionPolicy.MaximumAttempts,
                    connectionPolicy.MaximumAttempts,
                    anonymousDeviceId,
                    lastError,
                    correlationId));
                ScheduleSoftWatcherRefresh();
            }
            catch (ConnectionStageTimeoutException ex)
            {
                lock (sync)
                {
                    desiredConnection = false;
                }
                PublishConnection(new ConnectionSnapshot(
                    AudioConnectionState.TimedOut,
                    deviceId,
                    ex.Stage,
                    0,
                    connectionPolicy.MaximumAttempts,
                    anonymousDeviceId,
                    ex.Stage + " timed out",
                    correlationId));
            }
            catch (OperationCanceledException)
            {
                lock (sync)
                {
                    desiredConnection = false;
                }
                if (IsSessionCurrent(sessionId))
                {
                    PublishConnection(new ConnectionSnapshot(
                        AudioConnectionState.Disconnected,
                        deviceId,
                        "Canceled",
                        0,
                        connectionPolicy.MaximumAttempts,
                        anonymousDeviceId,
                        "Canceled",
                        correlationId));
                }

                logger.Log(
                    DiagnosticLevel.Information,
                    "Connection.SessionCanceled",
                    fields: new[]
                    {
                        ("correlation", (object)correlationId),
                        ("device", anonymousDeviceId)
                    });
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    desiredConnection = false;
                    activeConnection = null;
                }
                logger.Log(
                    DiagnosticLevel.Error,
                    "Connection.SessionFailed",
                    "Connection session failed",
                    ex,
                    ("correlation", correlationId),
                    ("device", anonymousDeviceId));
                PublishConnection(new ConnectionSnapshot(
                    AudioConnectionState.Failed,
                    deviceId,
                    "Complete",
                    0,
                    connectionPolicy.MaximumAttempts,
                    anonymousDeviceId,
                    "Connection attempt failed",
                    correlationId));
            }
            finally
            {
                lock (sync)
                {
                    if (activeSessionId == sessionId)
                    {
                        activeOperation = null;
                        pendingConnection = null;
                        activeSessionCancellation = null;
                        activeSessionId = Guid.Empty;

                        if (activeConnection == null && !desiredConnection)
                        {
                            activeDeviceId = null;
                            activeDeviceName = null;
                        }
                    }
                }

                sessionCancellation?.Dispose();
                operationGate.Release();
            }
        }

        public void CancelCurrentOperation()
        {
            CancellationTokenSource cancellation;
            ActiveOperation operation;
            ConnectionSnapshot snapshot;

            lock (sync)
            {
                if (disposed || activeSessionCancellation == null)
                {
                    return;
                }

                cancellation = activeSessionCancellation;
                operation = activeOperation;
                pendingConnection = null;
                snapshot = currentConnection;
                desiredConnection = false;
                automaticRecoveryCancellation?.Cancel();
            }

            PublishConnection(new ConnectionSnapshot(
                AudioConnectionState.Canceling,
                snapshot.DeviceId,
                snapshot.Stage,
                snapshot.Attempt,
                snapshot.MaximumAttempts,
                snapshot.AnonymousDeviceId,
                null,
                snapshot.CorrelationId));

            logger.Log(
                DiagnosticLevel.Information,
                "Connection.CancelRequested",
                fields: new[]
                {
                    ("correlation", (object)snapshot.CorrelationId),
                    ("device", snapshot.AnonymousDeviceId)
                });

            TryCancelOperation(operation, "user or lifecycle cancellation");

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            // Cancel is cooperative. Keep the enabled connection alive and quarantine
            // it until the WinRT operation reaches a terminal state; disposing here is
            // what previously allowed a new Open to overlap the old one in Windows.
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            CancelCurrentOperation();
            await operationGate.WaitAsync(cancellationToken);

            try
            {
                ConnectionHandle connection;
                ConnectionSnapshot snapshot;

                lock (sync)
                {
                    desiredConnection = false;
                    automaticRecoveryCancellation?.Cancel();
                    connection = enabledConnection;
                    enabledConnection = null;
                    activeConnection = null;
                    snapshot = currentConnection;
                }

                if (connection == null)
                {
                    return;
                }

                PublishConnection(new ConnectionSnapshot(
                    AudioConnectionState.Disconnecting,
                    connection.DeviceId,
                    "Dispose",
                    snapshot.Attempt,
                    snapshot.MaximumAttempts,
                    connection.AnonymousDeviceId,
                    null,
                    connection.CorrelationId));

                Task drain = GetConnectionDrainTask(connection);
                bool drained = true;
                if (!drain.IsCompleted)
                {
                    PublishConnection(new ConnectionSnapshot(
                        AudioConnectionState.Recovering,
                        connection.DeviceId,
                        "Drain",
                        snapshot.Attempt,
                        snapshot.MaximumAttempts,
                        connection.AnonymousDeviceId,
                        null,
                        connection.CorrelationId));
                    drained = await WaitForTaskOrTimeoutAsync(
                        drain,
                        connectionPolicy.OperationDrainTimeout,
                        cancellationToken);
                }

                if (drained)
                {
                    await Task.Run(
                        () => SafeDisposeConnection(connection, "disconnect requested"),
                        CancellationToken.None);
                }
                else
                {
                    _ = DisposeAfterDrainAsync(
                        connection,
                        "disconnect completed after operation drain");
                }

                lock (sync)
                {
                    activeDeviceId = null;
                    activeDeviceName = null;
                }

                PublishConnection(new ConnectionSnapshot(
                    AudioConnectionState.Disconnected,
                    connection.DeviceId,
                    "Complete",
                    0,
                    connectionPolicy.MaximumAttempts,
                    connection.AnonymousDeviceId,
                    null,
                    connection.CorrelationId));
                PublishDevices();
            }
            finally
            {
                operationGate.Release();
            }
        }

        public async Task ReconnectAsync(
            string deviceId,
            CancellationToken cancellationToken = default)
        {
            await DisconnectAsync(cancellationToken);
            await Task.Delay(connectionPolicy.RetryDelay, cancellationToken);
            await ConnectAsync(deviceId, cancellationToken);
        }

        public void Dispose()
        {
            CancellationTokenSource sessionCancellation;
            CancellationTokenSource recoveryCancellation;
            ActiveOperation operation;
            ConnectionHandle pending;
            ConnectionHandle active;
            ConnectionHandle enabled;

            lock (sync)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                watching = false;
                sessionCancellation = activeSessionCancellation;
                operation = activeOperation;
                pending = pendingConnection;
                active = activeConnection;
                enabled = enabledConnection;
                recoveryCancellation = automaticRecoveryCancellation;
                activeSessionCancellation = null;
                activeOperation = null;
                pendingConnection = null;
                activeConnection = null;
                enabledConnection = null;
                automaticRecoveryCancellation = null;
            }

            PowerManager.SystemSuspendStatusChanged -= PowerManager_SystemSuspendStatusChanged;
            if (bluetoothRadio != null)
            {
                bluetoothRadio.StateChanged -= BluetoothRadio_StateChanged;
                bluetoothRadio = null;
            }
            watcherRestartCancellation?.Cancel();
            offlineConfirmationCancellation?.Cancel();
            recoveryCancellation?.Cancel();
            StopAndReleaseWatchers();

            TryCancelOperation(operation, "service disposed");
            try
            {
                sessionCancellation?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            SafeDisposeConnection(pending, "service disposed");
            SafeDisposeConnection(active, "service disposed");
            if (enabled != pending && enabled != active)
            {
                SafeDisposeConnection(enabled, "service disposed");
            }

            PublishWatcherState(DeviceWatcherState.Stopped, "Device monitoring stopped");
            logger.Log(DiagnosticLevel.Information, "BluetoothService.Disposed");
        }

        private void StartWatchers()
        {
            if (disposed)
            {
                return;
            }

            PublishWatcherState(DeviceWatcherState.Searching, "Scanning paired audio devices...");

            try
            {
                DeviceWatcher newPlaybackWatcher = DeviceInformation.CreateWatcher(
                    AudioPlaybackConnection.GetDeviceSelector(),
                    new[] { ContainerIdProperty });
                DeviceWatcher newPresenceWatcher = DeviceInformation.CreateWatcher(
                    BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                    new[]
                    {
                        AepContainerIdProperty,
                        AepIsPresentProperty,
                        AepIsConnectedProperty,
                        AepIsPairedProperty
                    },
                    DeviceInformationKind.AssociationEndpoint);

                AttachPlaybackWatcher(newPlaybackWatcher);
                AttachPresenceWatcher(newPresenceWatcher);

                lock (sync)
                {
                    if (disposed || !watching)
                    {
                        DetachWatcher(newPlaybackWatcher, true);
                        DetachWatcher(newPresenceWatcher, false);
                        return;
                    }

                    playbackWatcher = newPlaybackWatcher;
                    presenceWatcher = newPresenceWatcher;
                    playbackEnumerationCompleted = false;
                    presenceEnumerationCompleted = false;
                    watcherGeneration++;
                    currentPlaybackGeneration.Clear();
                    removedPresenceContainers.Clear();
                    aepDevices.Clear();
                }

                newPlaybackWatcher.Start();
                newPresenceWatcher.Start();

                logger.Log(
                    DiagnosticLevel.Information,
                    "DeviceWatchers.Started",
                    fields: new[] { ("generation", (object)watcherGeneration) });
                PublishDevices();
            }
            catch (Exception ex)
            {
                logger.Log(
                    DiagnosticLevel.Error,
                    "DeviceWatchers.StartFailed",
                    "Unable to start Bluetooth device monitoring",
                    ex);
                ScheduleWatcherRestart();
            }
        }

        private async Task InitializeRadioMonitoringAsync()
        {
            try
            {
                IReadOnlyList<Radio> radios = await Radio.GetRadiosAsync();
                Radio radio = radios.FirstOrDefault(item => item.Kind == RadioKind.Bluetooth);
                lock (sync)
                {
                    if (disposed)
                    {
                        return;
                    }

                    if (bluetoothRadio != null)
                    {
                        bluetoothRadio.StateChanged -= BluetoothRadio_StateChanged;
                    }
                    bluetoothRadio = radio;
                    if (bluetoothRadio != null)
                    {
                        bluetoothRadio.StateChanged += BluetoothRadio_StateChanged;
                    }
                }

                if (radio != null && radio.State != RadioState.On)
                {
                    HandleBluetoothRadioState(radio.State);
                }
            }
            catch (Exception ex)
            {
                logger.Log(
                    DiagnosticLevel.Warning,
                    "BluetoothRadio.MonitoringUnavailable",
                    exception: ex);
            }
        }

        private void BluetoothRadio_StateChanged(Radio sender, object args)
        {
            HandleBluetoothRadioState(sender.State);
        }

        private void HandleBluetoothRadioState(RadioState state)
        {
            logger.Log(
                DiagnosticLevel.Information,
                "BluetoothRadio.StateChanged",
                fields: new[] { ("state", (object)state) });

            if (state != RadioState.On)
            {
                bool hasEnabledConnection;
                lock (sync)
                {
                    playbackEnumerationCompleted = false;
                    presenceEnumerationCompleted = false;
                    currentPlaybackGeneration.Clear();
                    desiredConnection = false;
                    hasEnabledConnection = enabledConnection != null;
                }
                PublishWatcherState(
                    DeviceWatcherState.Unavailable,
                    "Bluetooth is turned off");
                PublishDevices();
                CancelCurrentOperation();
                if (hasEnabledConnection)
                {
                    _ = DisconnectAsync();
                }
                return;
            }

            lock (sync)
            {
                if (disposed || !watching)
                {
                    return;
                }
            }

            ScheduleSoftWatcherRefresh();
        }

        private void AttachPlaybackWatcher(DeviceWatcher watcher)
        {
            watcher.Added += PlaybackWatcher_Added;
            watcher.Updated += PlaybackWatcher_Updated;
            watcher.Removed += PlaybackWatcher_Removed;
            watcher.EnumerationCompleted += PlaybackWatcher_EnumerationCompleted;
            watcher.Stopped += DeviceWatcher_Stopped;
        }

        private void AttachPresenceWatcher(DeviceWatcher watcher)
        {
            watcher.Added += PresenceWatcher_Added;
            watcher.Updated += PresenceWatcher_Updated;
            watcher.Removed += PresenceWatcher_Removed;
            watcher.EnumerationCompleted += PresenceWatcher_EnumerationCompleted;
            watcher.Stopped += DeviceWatcher_Stopped;
        }

        private void PlaybackWatcher_Added(DeviceWatcher sender, DeviceInformation device)
        {
            lock (sync)
            {
                if (sender != playbackWatcher || disposed)
                {
                    return;
                }

                playbackDevices[device.Id] = device;
                currentPlaybackGeneration.Add(device.Id);
            }

            LogDeviceEvent("PlaybackDevice.Added", device);
            PublishDevicesAndEvaluateAvailability();
        }

        private void PlaybackWatcher_Updated(
            DeviceWatcher sender,
            DeviceInformationUpdate update)
        {
            DeviceInformation device;
            lock (sync)
            {
                if (sender != playbackWatcher ||
                    !playbackDevices.TryGetValue(update.Id, out device))
                {
                    return;
                }

                device.Update(update);
            }

            LogDeviceEvent("PlaybackDevice.Updated", device);
            PublishDevicesAndEvaluateAvailability();
        }

        private void PlaybackWatcher_Removed(
            DeviceWatcher sender,
            DeviceInformationUpdate update)
        {
            bool affectsActiveDevice;
            lock (sync)
            {
                if (sender != playbackWatcher)
                {
                    return;
                }

                currentPlaybackGeneration.Remove(update.Id);
                affectsActiveDevice = activeDeviceId == update.Id;
            }

            logger.Log(
                DiagnosticLevel.Information,
                "PlaybackDevice.Removed",
                fields: new[]
                {
                    ("device", (object)logger.GetAnonymousDeviceId(update.Id))
                });

            PublishDevices();

            if (affectsActiveDevice)
            {
                CancelOrDisconnectUnavailableDevice(update.Id);
            }
        }

        private void PlaybackWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            lock (sync)
            {
                if (sender != playbackWatcher)
                {
                    return;
                }

                playbackEnumerationCompleted = true;
            }

            logger.Log(DiagnosticLevel.Information, "PlaybackWatcher.EnumerationCompleted");
            UpdateReadyWatcherState();
            PublishDevicesAndEvaluateAvailability();
        }

        private void PresenceWatcher_Added(DeviceWatcher sender, DeviceInformation device)
        {
            lock (sync)
            {
                if (sender != presenceWatcher || disposed)
                {
                    return;
                }

                aepDevices[device.Id] = device;
                Guid? containerId = GetGuidProperty(device, AepContainerIdProperty);
                if (containerId.HasValue)
                {
                    removedPresenceContainers.Remove(containerId.Value);
                }
            }

            LogDeviceEvent("PresenceDevice.Added", device);
            PublishDevicesAndEvaluateAvailability();
        }

        private void PresenceWatcher_Updated(
            DeviceWatcher sender,
            DeviceInformationUpdate update)
        {
            DeviceInformation device;
            lock (sync)
            {
                if (sender != presenceWatcher ||
                    !aepDevices.TryGetValue(update.Id, out device))
                {
                    return;
                }

                device.Update(update);
            }

            LogDeviceEvent("PresenceDevice.Updated", device);
            PublishDevicesAndEvaluateAvailability();
        }

        private void PresenceWatcher_Removed(
            DeviceWatcher sender,
            DeviceInformationUpdate update)
        {
            Guid? removedContainer = null;
            lock (sync)
            {
                if (sender != presenceWatcher)
                {
                    return;
                }

                if (aepDevices.TryGetValue(update.Id, out DeviceInformation removed))
                {
                    removedContainer = GetGuidProperty(removed, AepContainerIdProperty);
                }
                aepDevices.Remove(update.Id);
                if (removedContainer.HasValue)
                {
                    removedPresenceContainers.Add(removedContainer.Value);
                }
            }

            logger.Log(
                DiagnosticLevel.Information,
                "PresenceDevice.Removed",
                fields: new[]
                {
                    ("aep", (object)logger.GetAnonymousDeviceId(update.Id))
                });
            PublishDevicesAndEvaluateAvailability();
        }

        private void PresenceWatcher_EnumerationCompleted(DeviceWatcher sender, object args)
        {
            lock (sync)
            {
                if (sender != presenceWatcher)
                {
                    return;
                }

                presenceEnumerationCompleted = true;
            }

            logger.Log(DiagnosticLevel.Information, "PresenceWatcher.EnumerationCompleted");
            UpdateReadyWatcherState();
            PublishDevicesAndEvaluateAvailability();
        }

        private void DeviceWatcher_Stopped(DeviceWatcher sender, object args)
        {
            bool shouldRestart;
            lock (sync)
            {
                shouldRestart = !disposed && watching && !stoppingWatchers &&
                    (sender == playbackWatcher || sender == presenceWatcher);
            }

            if (!shouldRestart)
            {
                return;
            }

            logger.Log(
                DiagnosticLevel.Warning,
                "DeviceWatcher.StoppedUnexpectedly",
                fields: new[] { ("status", (object)sender.Status) });
            ScheduleWatcherRestart();
        }

        private void ScheduleWatcherRestart()
        {
            CancellationTokenSource restartCancellation;
            int delaySeconds;
            int restartAttempt;

            lock (sync)
            {
                if (disposed || !watching || watcherRestartCancellation != null)
                {
                    return;
                }

                watcherRestartAttempt++;
                restartAttempt = watcherRestartAttempt;
                delaySeconds = WatcherRestartDelaysSeconds[
                    Math.Min(restartAttempt - 1, WatcherRestartDelaysSeconds.Length - 1)];
                watcherRestartCancellation = new CancellationTokenSource();
                restartCancellation = watcherRestartCancellation;
                playbackEnumerationCompleted = false;
                presenceEnumerationCompleted = false;
            }

            PublishWatcherState(
                DeviceWatcherState.Retrying,
                "Bluetooth monitoring unavailable; retrying in " + delaySeconds + "s");
            PublishDevices();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(delaySeconds),
                        restartCancellation.Token);
                    StopAndReleaseWatchers();

                    lock (sync)
                    {
                        if (watcherRestartCancellation == restartCancellation)
                        {
                            watcherRestartCancellation = null;
                        }
                    }

                    StartWatchers();
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    restartCancellation.Dispose();
                }
            });
        }

        private void StopAndReleaseWatchers()
        {
            DeviceWatcher oldPlaybackWatcher;
            DeviceWatcher oldPresenceWatcher;

            lock (sync)
            {
                stoppingWatchers = true;
                oldPlaybackWatcher = playbackWatcher;
                oldPresenceWatcher = presenceWatcher;
                playbackWatcher = null;
                presenceWatcher = null;
            }

            DetachWatcher(oldPlaybackWatcher, true);
            DetachWatcher(oldPresenceWatcher, false);

            lock (sync)
            {
                stoppingWatchers = false;
            }
        }

        private void DetachWatcher(DeviceWatcher watcher, bool isPlaybackWatcher)
        {
            if (watcher == null)
            {
                return;
            }

            if (isPlaybackWatcher)
            {
                watcher.Added -= PlaybackWatcher_Added;
                watcher.Updated -= PlaybackWatcher_Updated;
                watcher.Removed -= PlaybackWatcher_Removed;
                watcher.EnumerationCompleted -= PlaybackWatcher_EnumerationCompleted;
            }
            else
            {
                watcher.Added -= PresenceWatcher_Added;
                watcher.Updated -= PresenceWatcher_Updated;
                watcher.Removed -= PresenceWatcher_Removed;
                watcher.EnumerationCompleted -= PresenceWatcher_EnumerationCompleted;
            }

            watcher.Stopped -= DeviceWatcher_Stopped;

            try
            {
                if (watcher.Status == DeviceWatcherStatus.Started ||
                    watcher.Status == DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }
            catch (Exception ex)
            {
                logger.Log(
                    DiagnosticLevel.Warning,
                    "DeviceWatcher.StopFailed",
                    exception: ex);
            }
        }

        private async Task<ConnectionHandle> GetOrCreateEnabledConnectionAsync(
            string deviceId,
            string anonymousDeviceId,
            string correlationId,
            Guid sessionId,
            CancellationToken cancellationToken)
        {
            Task previousTransportDrain;
            lock (sync)
            {
                previousTransportDrain = transportDrain;
            }

            if (!await WaitForTaskOrTimeoutAsync(
                previousTransportDrain,
                connectionPolicy.OperationDrainTimeout,
                cancellationToken))
            {
                throw new ConnectionStageTimeoutException(
                    "Recovery",
                    connectionPolicy.OperationDrainTimeout);
            }

            ConnectionHandle existing;
            lock (sync)
            {
                existing = enabledConnection;
                if (existing != null &&
                    existing.DeviceId == deviceId &&
                    Volatile.Read(ref existing.Disposed) == 0)
                {
                    existing.BeginSession(sessionId, correlationId);
                    return existing;
                }

                enabledConnection = null;
                activeConnection = null;
            }

            if (existing != null)
            {
                await WaitForTaskOrTimeoutAsync(
                    GetConnectionDrainTask(existing),
                    connectionPolicy.OperationDrainTimeout,
                    cancellationToken);
                await Task.Run(
                    () => SafeDisposeConnection(existing, "switching device"),
                    CancellationToken.None);
            }

            IAudioConnection nativeConnection = connectionFactory.Create(deviceId);
            if (nativeConnection == null)
            {
                return null;
            }

            ConnectionHandle connection = new ConnectionHandle(
                nativeConnection,
                sessionId,
                deviceId,
                anonymousDeviceId,
                correlationId);
            nativeConnection.StateChanged += AudioConnection_StateChanged;

            lock (sync)
            {
                EnsureSessionIsCurrentLocked(sessionId);
                enabledConnection = connection;
                pendingConnection = connection;
            }

            PublishConnection(new ConnectionSnapshot(
                AudioConnectionState.Connecting,
                deviceId,
                "Start",
                0,
                connectionPolicy.MaximumAttempts,
                anonymousDeviceId,
                null,
                correlationId));

            try
            {
                await RunActionStageAsync(
                    nativeConnection.StartAsync(),
                    connectionPolicy.StartTimeout,
                    sessionId,
                    connection,
                    "Start",
                    0,
                    cancellationToken);
                connection.Started = true;
                ClearPendingConnection(connection);
                logger.Log(
                    DiagnosticLevel.Information,
                    "Connection.Enabled",
                    fields: new[]
                    {
                        ("correlation", (object)correlationId),
                        ("device", anonymousDeviceId)
                    });
                return connection;
            }
            catch
            {
                ClearPendingConnection(connection);
                lock (sync)
                {
                    if (enabledConnection == connection)
                    {
                        enabledConnection = null;
                    }
                }

                _ = DisposeAfterDrainAsync(connection, "enable failed");
                throw;
            }
        }

        private async Task<bool> WaitForConnectionDrainAsync(
            ConnectionHandle connection,
            string deviceId,
            string anonymousDeviceId,
            string correlationId,
            int attempt,
            CancellationToken cancellationToken)
        {
            Task drain = GetConnectionDrainTask(connection);
            if (drain.IsCompleted)
            {
                await drain;
                return true;
            }

            PublishConnection(new ConnectionSnapshot(
                AudioConnectionState.Recovering,
                deviceId,
                "Drain",
                attempt,
                connectionPolicy.MaximumAttempts,
                anonymousDeviceId,
                null,
                correlationId));
            logger.Log(
                DiagnosticLevel.Information,
                "Connection.WaitingForPreviousOperation",
                fields: new[]
                {
                    ("correlation", (object)correlationId),
                    ("device", anonymousDeviceId),
                    ("attempt", attempt)
                });

            return await WaitForTaskOrTimeoutAsync(
                drain,
                connectionPolicy.OperationDrainTimeout,
                cancellationToken);
        }

        private static Task GetConnectionDrainTask(ConnectionHandle connection)
        {
            lock (connection.LifecycleSync)
            {
                return connection.DrainTask ?? Task.CompletedTask;
            }
        }

        private static async Task<bool> WaitForTaskOrTimeoutAsync(
            Task task,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (task.IsCompleted)
            {
                await task;
                return true;
            }

            Task timeoutTask = Task.Delay(timeout, cancellationToken);
            Task completed = await Task.WhenAny(task, timeoutTask);
            if (completed == task)
            {
                await task;
                return true;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        private async Task DisposeAfterDrainAsync(
            ConnectionHandle connection,
            string reason)
        {
            try
            {
                await GetConnectionDrainTask(connection);
            }
            catch
            {
            }

            await Task.Run(
                () => SafeDisposeConnection(connection, reason),
                CancellationToken.None);
        }

        private void ScheduleSoftWatcherRefresh()
        {
            if (Interlocked.Exchange(ref softWatcherRefreshScheduled, 1) != 0)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    lock (sync)
                    {
                        if (disposed || !watching || activeConnection != null)
                        {
                            return;
                        }
                    }

                    CancellationTokenSource scheduledRestart;
                    lock (sync)
                    {
                        scheduledRestart = watcherRestartCancellation;
                        watcherRestartCancellation = null;
                    }
                    scheduledRestart?.Cancel();

                    logger.Log(
                        DiagnosticLevel.Information,
                        "DeviceWatchers.SoftRefreshStarted");
                    StopAndReleaseWatchers();
                    StartWatchers();
                }
                catch (Exception ex)
                {
                    logger.Log(
                        DiagnosticLevel.Warning,
                        "DeviceWatchers.SoftRefreshFailed",
                        exception: ex);
                }
                finally
                {
                    Interlocked.Exchange(ref softWatcherRefreshScheduled, 0);
                }
            });
        }

        private async Task RunActionStageAsync(
            ICancelableOperation operation,
            TimeSpan timeout,
            Guid sessionId,
            ConnectionHandle connection,
            string stage,
            int attempt,
            CancellationToken cancellationToken)
        {
            Task operationTask = operation.Completion;
            await RunStageCoreAsync(
                operation,
                operationTask,
                timeout,
                sessionId,
                connection,
                stage,
                attempt,
                cancellationToken);
        }

        private async Task<T> RunOperationStageAsync<T>(
            ICancelableOperation<T> operation,
            TimeSpan timeout,
            Guid sessionId,
            ConnectionHandle connection,
            string stage,
            int attempt,
            CancellationToken cancellationToken)
        {
            Task<T> operationTask = operation.Completion;
            await RunStageCoreAsync(
                operation,
                operationTask,
                timeout,
                sessionId,
                connection,
                stage,
                attempt,
                cancellationToken);
            return await operationTask;
        }

        private async Task RunStageCoreAsync(
            ICancelableOperation operation,
            Task operationTask,
            TimeSpan timeout,
            Guid sessionId,
            ConnectionHandle connection,
            string stage,
            int attempt,
            CancellationToken cancellationToken)
        {
            ActiveOperation operationContext = new ActiveOperation(
                operation,
                connection,
                sessionId,
                stage);
            Stopwatch stopwatch = Stopwatch.StartNew();

            lock (sync)
            {
                EnsureSessionIsCurrentLocked(sessionId);
                activeOperation = operationContext;
            }

            logger.Log(
                DiagnosticLevel.Information,
                "Connection.StageStarted",
                fields: new[]
                {
                    ("correlation", (object)connection.CorrelationId),
                    ("device", connection.AnonymousDeviceId),
                    ("stage", stage),
                    ("attempt", attempt),
                    ("timeoutMs", (int)timeout.TotalMilliseconds)
                });

            using CancellationTokenRegistration registration =
                cancellationToken.Register(() =>
                    TryCancelOperation(operationContext, "cancellation token"));

            Task cancellationTask = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            Task timeoutTask = Task.Delay(timeout);
            Task completedTask = await Task.WhenAny(
                operationTask,
                cancellationTask,
                timeoutTask);

            if (completedTask != operationTask)
            {
                TryCancelOperation(
                    operationContext,
                    cancellationToken.IsCancellationRequested ? "canceled" : "timed out");
                Task drain = ObserveLateOperationAsync(
                    operationTask,
                    operationContext,
                    connection.CorrelationId,
                    connection.AnonymousDeviceId);
                lock (connection.LifecycleSync)
                {
                    connection.DrainTask = drain;
                }
                lock (sync)
                {
                    transportDrain = drain;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new ConnectionStageTimeoutException(stage, timeout);
            }

            try
            {
                await operationTask;
                logger.Log(
                    DiagnosticLevel.Information,
                    "Connection.StageCompleted",
                    fields: new[]
                    {
                        ("correlation", (object)connection.CorrelationId),
                        ("device", connection.AnonymousDeviceId),
                        ("stage", stage),
                        ("attempt", attempt),
                        ("durationMs", stopwatch.ElapsedMilliseconds)
                    });
            }
            finally
            {
                ClearActiveOperation(operationContext);
                TryCloseOperation(operationContext);
            }
        }

        private async Task ObserveLateOperationAsync(
            Task operationTask,
            ActiveOperation operation,
            string correlationId,
            string anonymousDeviceId)
        {
            ClearActiveOperation(operation);
            Exception exception = null;
            object lateResult = null;
            try
            {
                await operationTask;
                if (operationTask is
                    Task<AudioPlaybackConnectionOpenResultStatus> openTask)
                {
                    lateResult = openTask.Result;
                }
            }
            catch (Exception ex)
            {
                exception = ex.GetBaseException();
            }
            finally
            {
                logger.Log(
                    exception == null
                        ? DiagnosticLevel.Information
                        : DiagnosticLevel.Warning,
                    "Connection.LateOperationCompleted",
                    fields: new[]
                    {
                        ("correlation", (object)correlationId),
                        ("device", anonymousDeviceId),
                        ("stage", operation.Stage),
                        ("taskStatus", operationTask.Status),
                        ("result", lateResult),
                        ("error", exception?.Message)
                    });
                TryCloseOperation(operation);

                bool releaseLateOpenedConnection;
                lock (sync)
                {
                    releaseLateOpenedConnection =
                        !desiredConnection &&
                        enabledConnection == operation.Connection &&
                        operation.Connection.Connection.State ==
                            AudioPlaybackConnectionState.Opened;
                    if (releaseLateOpenedConnection)
                    {
                        enabledConnection = null;
                        activeConnection = null;
                    }
                }

                if (releaseLateOpenedConnection)
                {
                    await Task.Run(
                        () => SafeDisposeConnection(
                            operation.Connection,
                            "late open completed after cancellation"),
                        CancellationToken.None);
                    PublishDevices();
                }
            }
        }

        private void AudioConnection_StateChanged(object sender, EventArgs args)
        {
            IAudioConnection changedConnection = sender as IAudioConnection;
            if (changedConnection == null)
            {
                return;
            }

            ConnectionHandle connection;
            bool isActive;
            bool isPending;

            lock (sync)
            {
                if (activeConnection?.Connection == changedConnection)
                {
                    connection = activeConnection;
                    isActive = true;
                    isPending = false;
                }
                else if (pendingConnection?.Connection == changedConnection)
                {
                    connection = pendingConnection;
                    isActive = false;
                    isPending = true;
                }
                else if (enabledConnection?.Connection == changedConnection)
                {
                    connection = enabledConnection;
                    isActive = false;
                    isPending = false;
                }
                else
                {
                    return;
                }
            }

            logger.Log(
                DiagnosticLevel.Information,
                "Connection.StateChanged",
                fields: new[]
                {
                    ("correlation", (object)connection.CorrelationId),
                    ("device", connection.AnonymousDeviceId),
                    ("state", changedConnection.State),
                    ("active", isActive),
                    ("pending", isPending)
                });

            if (changedConnection.State == AudioPlaybackConnectionState.Opened)
            {
                return;
            }

            if (isPending)
            {
                Interlocked.Exchange(ref connection.NativeClosedDuringOpen, 1);
                return;
            }

            if (!isActive)
            {
                return;
            }

            lock (sync)
            {
                if (activeConnection != connection)
                {
                    return;
                }

                activeConnection = null;
            }

            PublishConnection(new ConnectionSnapshot(
                AudioConnectionState.Disconnected,
                connection.DeviceId,
                "StateChanged",
                0,
                connectionPolicy.MaximumAttempts,
                connection.AnonymousDeviceId,
                null,
                connection.CorrelationId));
            PublishDevices();
            ScheduleAutomaticRecovery(connection.DeviceId);
        }

        private void ScheduleAutomaticRecovery(string deviceId)
        {
            CancellationTokenSource cancellation;
            int attempt;
            lock (sync)
            {
                if (disposed || !desiredConnection || automaticRecoveryCount >= 2)
                {
                    return;
                }

                automaticRecoveryCount++;
                attempt = automaticRecoveryCount;
                automaticRecoveryCancellation?.Cancel();
                cancellation = new CancellationTokenSource();
                automaticRecoveryCancellation = cancellation;
            }

            logger.Log(
                DiagnosticLevel.Information,
                "Connection.AutomaticRecoveryScheduled",
                fields: new[]
                {
                    ("device", (object)logger.GetAnonymousDeviceId(deviceId)),
                    ("attempt", attempt)
                });

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(attempt == 1 ? 1 : 3),
                        cancellation.Token);
                    BluetoothDeviceSnapshot device = GetDevice(deviceId);
                    lock (sync)
                    {
                        if (disposed || !desiredConnection ||
                            activeConnection != null || device?.IsConnectable != true)
                        {
                            return;
                        }
                    }

                    await ConnectAsync(deviceId);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    logger.Log(
                        DiagnosticLevel.Warning,
                        "Connection.AutomaticRecoveryFailed",
                        exception: ex,
                        fields: new[] { ("attempt", (object)attempt) });
                }
                finally
                {
                    lock (sync)
                    {
                        if (automaticRecoveryCancellation == cancellation)
                        {
                            automaticRecoveryCancellation = null;
                        }
                    }
                    cancellation.Dispose();
                }
            });
        }

        private void PowerManager_SystemSuspendStatusChanged(object sender, object args)
        {
            SystemSuspendStatus status = PowerManager.SystemSuspendStatus;
            if (status != SystemSuspendStatus.AutoResume &&
                status != SystemSuspendStatus.ManualResume)
            {
                return;
            }

            string deviceId;
            lock (sync)
            {
                deviceId = activeConnection?.DeviceId;
                if (deviceId != null)
                {
                    activeConnection = null;
                }
            }

            if (deviceId == null)
            {
                return;
            }

            logger.Log(
                DiagnosticLevel.Information,
                "Power.ResumeDetected",
                fields: new[]
                {
                    ("device", (object)logger.GetAnonymousDeviceId(deviceId))
                });
            PublishConnection(new ConnectionSnapshot(
                AudioConnectionState.Recovering,
                deviceId,
                "Resume",
                0,
                connectionPolicy.MaximumAttempts,
                logger.GetAnonymousDeviceId(deviceId),
                null,
                logger.CreateCorrelationId()));

            _ = Task.Run(async () =>
            {
                try
                {
                    ScheduleSoftWatcherRefresh();
                    BluetoothDeviceSnapshot device = null;
                    for (int check = 0; check < 40 && !disposed; check++)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250));
                        device = GetDevice(deviceId);
                        if (device?.IsConnectable == true)
                        {
                            break;
                        }
                    }

                    if (!disposed && device?.IsConnectable == true)
                    {
                        await ConnectAsync(deviceId);
                    }
                    else if (!disposed)
                    {
                        lock (sync)
                        {
                            desiredConnection = false;
                        }
                        PublishConnection(new ConnectionSnapshot(
                            AudioConnectionState.Disconnected,
                            deviceId,
                            "Resume",
                            0,
                            connectionPolicy.MaximumAttempts,
                            logger.GetAnonymousDeviceId(deviceId),
                            "Bluetooth endpoint did not return after resume",
                            logger.CreateCorrelationId()));
                    }
                }
                catch (Exception ex)
                {
                    logger.Log(
                        DiagnosticLevel.Warning,
                        "Power.ResumeRecoveryFailed",
                        exception: ex);
                }
            });
        }

        private IReadOnlyList<BluetoothDeviceSnapshot> BuildDeviceSnapshotsLocked()
        {
            return playbackDevices.Values
                .Select(BuildDeviceSnapshotLocked)
                .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private BluetoothDeviceSnapshot BuildDeviceSnapshotLocked(DeviceInformation device)
        {
            Guid? containerId = GetGuidProperty(device, ContainerIdProperty);
            DeviceAvailability availability;
            bool isSystemConnected = false;
            bool isPaired = true;
            bool endpointIsCurrent = currentPlaybackGeneration.Contains(device.Id);

            if (!playbackEnumerationCompleted || !presenceEnumerationCompleted)
            {
                availability = DeviceAvailability.Checking;
            }
            else if (!endpointIsCurrent)
            {
                availability = DeviceAvailability.Offline;
            }
            else if (!containerId.HasValue)
            {
                availability = DeviceAvailability.Unknown;
            }
            else
            {
                List<DeviceInformation> matchingEndpoints = aepDevices.Values
                    .Where(endpoint =>
                        GetGuidProperty(endpoint, AepContainerIdProperty) == containerId)
                    .ToList();

                bool? isPresent = AggregateBooleanProperty(
                    matchingEndpoints,
                    AepIsPresentProperty);
                bool? connected = AggregateBooleanProperty(
                    matchingEndpoints,
                    AepIsConnectedProperty);
                bool? paired = AggregateBooleanProperty(
                    matchingEndpoints,
                    AepIsPairedProperty);
                isSystemConnected = connected == true;
                isPaired = paired != false;

                if (removedPresenceContainers.Contains(containerId.Value))
                {
                    availability = DeviceAvailability.Offline;
                }
                else if (isPresent == true)
                {
                    availability = DeviceAvailability.Nearby;
                }
                else if (isPresent == false)
                {
                    availability = DeviceAvailability.Offline;
                }
                else
                {
                    availability = DeviceAvailability.Unknown;
                }
            }

            bool isAudioConnected = activeConnection?.DeviceId == device.Id &&
                currentConnection.State == AudioConnectionState.Connected;

            return new BluetoothDeviceSnapshot(
                device.Id,
                containerId,
                string.IsNullOrWhiteSpace(device.Name) ? "Unknown device" : device.Name,
                availability,
                isSystemConnected,
                isAudioConnected,
                endpointIsCurrent &&
                    playbackEnumerationCompleted &&
                    presenceEnumerationCompleted &&
                    availability != DeviceAvailability.Offline &&
                    isPaired);
        }

        private static bool? AggregateBooleanProperty(
            IEnumerable<DeviceInformation> devices,
            string propertyName)
        {
            bool foundValue = false;
            foreach (DeviceInformation device in devices)
            {
                bool? value = GetBooleanProperty(device, propertyName);
                if (value == true)
                {
                    return true;
                }

                if (value.HasValue)
                {
                    foundValue = true;
                }
            }

            return foundValue ? false : null;
        }

        private static Guid? GetGuidProperty(DeviceInformation device, string propertyName)
        {
            if (device?.Properties.TryGetValue(propertyName, out object value) == true &&
                value is Guid guid)
            {
                return guid;
            }

            return null;
        }

        private static bool? GetBooleanProperty(DeviceInformation device, string propertyName)
        {
            if (device?.Properties.TryGetValue(propertyName, out object value) == true &&
                value is bool boolean)
            {
                return boolean;
            }

            return null;
        }

        private BluetoothDeviceSnapshot GetDevice(string deviceId)
        {
            if (deviceResolver != null)
            {
                return deviceResolver(deviceId);
            }

            lock (sync)
            {
                return playbackDevices.TryGetValue(deviceId, out DeviceInformation device)
                    ? BuildDeviceSnapshotLocked(device)
                    : null;
            }
        }

        private void PublishDevicesAndEvaluateAvailability()
        {
            PublishDevices();
            EvaluateActiveDeviceAvailability();
        }

        private void PublishDevices()
        {
            IReadOnlyList<BluetoothDeviceSnapshot> devices;
            lock (sync)
            {
                devices = BuildDeviceSnapshotsLocked();
            }

            DevicesChanged?.Invoke(this, new DevicesChangedEventArgs(devices));
        }

        private void PublishConnection(ConnectionSnapshot snapshot)
        {
            lock (sync)
            {
                currentConnection = snapshot;
            }

            ConnectionChanged?.Invoke(this, new ConnectionChangedEventArgs(snapshot));
        }

        private void PublishWatcherState(DeviceWatcherState state, string message)
        {
            WatcherStateChanged?.Invoke(
                this,
                new WatcherStateChangedEventArgs(state, message));
        }

        private void UpdateReadyWatcherState()
        {
            bool ready;
            lock (sync)
            {
                ready = playbackEnumerationCompleted && presenceEnumerationCompleted;
            }

            if (ready)
            {
                lock (sync)
                {
                    watcherRestartAttempt = 0;
                }
                PublishWatcherState(DeviceWatcherState.Ready, "Device monitoring active");
            }
        }

        private void EvaluateActiveDeviceAvailability()
        {
            string deviceId;
            lock (sync)
            {
                deviceId = activeDeviceId;
            }

            if (deviceId == null)
            {
                CancelOfflineConfirmation();
                return;
            }

            BluetoothDeviceSnapshot device = GetDevice(deviceId);
            if (device?.Availability != DeviceAvailability.Offline)
            {
                CancelOfflineConfirmation();
                return;
            }

            CancellationTokenSource cancellation = new CancellationTokenSource();
            lock (sync)
            {
                offlineConfirmationCancellation?.Cancel();
                offlineConfirmationCancellation = cancellation;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(OfflineConfirmationDelay, cancellation.Token);
                    if (GetDevice(deviceId)?.Availability == DeviceAvailability.Offline)
                    {
                        logger.Log(
                            DiagnosticLevel.Warning,
                            "Connection.DeviceConfirmedOffline",
                            fields: new[]
                            {
                                ("device", (object)logger.GetAnonymousDeviceId(deviceId))
                            });
                        CancelOrDisconnectUnavailableDevice(deviceId);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    lock (sync)
                    {
                        if (offlineConfirmationCancellation == cancellation)
                        {
                            offlineConfirmationCancellation = null;
                        }
                    }
                    cancellation.Dispose();
                }
            });
        }

        private void CancelOfflineConfirmation()
        {
            CancellationTokenSource cancellation;
            lock (sync)
            {
                cancellation = offlineConfirmationCancellation;
                offlineConfirmationCancellation = null;
            }

            cancellation?.Cancel();
        }

        private void CancelOrDisconnectUnavailableDevice(string deviceId)
        {
            bool connecting;
            bool connected;
            ConnectionHandle idleEnabled;
            lock (sync)
            {
                if (activeDeviceId != deviceId)
                {
                    return;
                }

                desiredConnection = false;
                automaticRecoveryCancellation?.Cancel();
                connecting = activeSessionCancellation != null;
                connected = activeConnection != null;
                idleEnabled = !connecting && !connected &&
                    enabledConnection?.DeviceId == deviceId
                        ? enabledConnection
                        : null;
                if (idleEnabled != null)
                {
                    enabledConnection = null;
                }
            }

            if (connecting)
            {
                CancelCurrentOperation();
            }
            else if (connected)
            {
                _ = DisconnectAsync();
            }
            else if (idleEnabled != null)
            {
                _ = DisposeAfterDrainAsync(idleEnabled, "device unavailable");
            }
        }

        private void ClearPendingConnection(ConnectionHandle connection)
        {
            lock (sync)
            {
                if (pendingConnection == connection)
                {
                    pendingConnection = null;
                }
            }
        }

        private void ClearActiveOperation(ActiveOperation operation)
        {
            lock (sync)
            {
                if (activeOperation == operation)
                {
                    activeOperation = null;
                }
            }
        }

        private bool IsSessionCurrent(Guid sessionId)
        {
            lock (sync)
            {
                return !disposed && activeSessionId == sessionId;
            }
        }

        private void EnsureSessionIsCurrent(Guid sessionId)
        {
            lock (sync)
            {
                EnsureSessionIsCurrentLocked(sessionId);
            }
        }

        private void EnsureSessionIsCurrentLocked(Guid sessionId)
        {
            if (disposed || activeSessionId != sessionId)
            {
                throw new OperationCanceledException("The connection session is no longer active.");
            }
        }

        private void SafeDisposeConnection(ConnectionHandle connection, string reason)
        {
            if (connection == null || Interlocked.Exchange(ref connection.Disposed, 1) != 0)
            {
                return;
            }

            connection.Connection.StateChanged -= AudioConnection_StateChanged;
            try
            {
                connection.Connection.Dispose();
                logger.Log(
                    DiagnosticLevel.Information,
                    "Connection.Disposed",
                    fields: new[]
                    {
                        ("correlation", (object)connection.CorrelationId),
                        ("device", connection.AnonymousDeviceId),
                        ("reason", reason)
                    });
            }
            catch (Exception ex)
            {
                logger.Log(
                    DiagnosticLevel.Warning,
                    "Connection.DisposeFailed",
                    exception: ex,
                    fields: new[]
                    {
                        ("correlation", (object)connection.CorrelationId),
                        ("device", connection.AnonymousDeviceId),
                        ("reason", reason)
                    });
            }
        }

        private void TryCancelOperation(ActiveOperation operation, string reason)
        {
            if (operation == null ||
                Interlocked.Exchange(ref operation.CancelRequested, 1) != 0)
            {
                return;
            }

            try
            {
                operation.Operation.Cancel();
                logger.Log(
                    DiagnosticLevel.Information,
                    "Connection.WinRtCancelSent",
                    fields: new[]
                    {
                        ("correlation", (object)operation.Connection.CorrelationId),
                        ("device", operation.Connection.AnonymousDeviceId),
                        ("stage", operation.Stage),
                        ("reason", reason)
                    });
            }
            catch (Exception ex)
            {
                logger.Log(
                    DiagnosticLevel.Warning,
                    "Connection.WinRtCancelFailed",
                    exception: ex,
                    fields: new[]
                    {
                        ("correlation", (object)operation.Connection.CorrelationId),
                        ("stage", operation.Stage)
                    });
            }
        }

        private void TryCloseOperation(ActiveOperation operation)
        {
            if (operation == null || Interlocked.Exchange(ref operation.Closed, 1) != 0)
            {
                return;
            }

            try
            {
                operation.Operation.Close();
            }
            catch (Exception ex)
            {
                logger.Log(
                    DiagnosticLevel.Debug,
                    "Connection.WinRtCloseFailed",
                    exception: ex,
                    fields: new[]
                    {
                        ("correlation", (object)operation.Connection.CorrelationId),
                        ("stage", operation.Stage)
                    });
            }
        }

        private void LogDeviceEvent(string eventName, DeviceInformation device)
        {
            Guid? containerId = GetGuidProperty(device, ContainerIdProperty) ??
                GetGuidProperty(device, AepContainerIdProperty);

            logger.Log(
                DiagnosticLevel.Debug,
                eventName,
                fields: new[]
                {
                    ("device", (object)logger.GetAnonymousDeviceId(device.Id)),
                    ("name", device.Name),
                    ("container", (object)logger.GetAnonymousDeviceId(
                        containerId?.ToString("D"))),
                    ("present", GetBooleanProperty(device, AepIsPresentProperty)),
                    ("systemConnected", GetBooleanProperty(device, AepIsConnectedProperty))
                });
        }

        private static bool IsRetryable(AudioPlaybackConnectionOpenResultStatus status)
        {
            return status == AudioPlaybackConnectionOpenResultStatus.RequestTimedOut ||
                status == AudioPlaybackConnectionOpenResultStatus.UnknownFailure;
        }

        private static string GetOpenResultErrorMessage(
            AudioPlaybackConnectionOpenResultStatus status)
        {
            return status switch
            {
                AudioPlaybackConnectionOpenResultStatus.DeniedBySystem =>
                    "Connection was denied by Windows",
                AudioPlaybackConnectionOpenResultStatus.RequestTimedOut =>
                    "Open request timed out",
                AudioPlaybackConnectionOpenResultStatus.UnknownFailure =>
                    "Windows reported an unknown connection failure",
                _ => "Connection attempt failed"
            };
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(BluetoothAudioService));
            }
        }

        private sealed class ConnectionHandle
        {
            public ConnectionHandle(
                IAudioConnection connection,
                Guid sessionId,
                string deviceId,
                string anonymousDeviceId,
                string correlationId)
            {
                Connection = connection;
                SessionId = sessionId;
                DeviceId = deviceId;
                AnonymousDeviceId = anonymousDeviceId;
                CorrelationId = correlationId;
            }

            public object LifecycleSync { get; } = new object();

            public IAudioConnection Connection { get; }

            public Guid SessionId { get; private set; }

            public string DeviceId { get; }

            public string AnonymousDeviceId { get; }

            public string CorrelationId { get; private set; }

            public Task DrainTask { get; set; } = Task.CompletedTask;

            public bool Started { get; set; }

            public int NativeClosedDuringOpen;

            public void BeginSession(Guid sessionId, string correlationId)
            {
                SessionId = sessionId;
                CorrelationId = correlationId;
            }

            public int Disposed;
        }

        private sealed class ActiveOperation
        {
            public ActiveOperation(
                ICancelableOperation operation,
                ConnectionHandle connection,
                Guid sessionId,
                string stage)
            {
                Operation = operation;
                Connection = connection;
                SessionId = sessionId;
                Stage = stage;
            }

            public ICancelableOperation Operation { get; }

            public ConnectionHandle Connection { get; }

            public Guid SessionId { get; }

            public string Stage { get; }

            public int CancelRequested;

            public int Closed;
        }

        private sealed class ConnectionStageTimeoutException : TimeoutException
        {
            public ConnectionStageTimeoutException(string stage, TimeSpan timeout)
                : base(stage + " exceeded " + timeout.TotalSeconds + " seconds.")
            {
                Stage = stage;
                Timeout = timeout;
            }

            public string Stage { get; }

            public TimeSpan Timeout { get; }
        }
    }
}
