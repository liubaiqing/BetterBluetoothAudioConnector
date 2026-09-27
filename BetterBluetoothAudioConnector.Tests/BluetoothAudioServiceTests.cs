using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterBluetoothAudioConnector.Diagnostics;
using BetterBluetoothAudioConnector.Models;
using BetterBluetoothAudioConnector.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Media.Audio;

namespace BetterBluetoothAudioConnector.Tests
{
    [TestClass]
    public sealed class BluetoothAudioServiceTests
    {
        [TestMethod]
        public async Task StartTimeoutDisposesOnlyAfterLateOperationDrains()
        {
            FakeCancelableOperation start = new FakeCancelableOperation();
            FakeAudioConnection connection = new FakeAudioConnection(
                start,
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeConnectionFactory factory = new FakeConnectionFactory(connection);
            using BluetoothAudioService service = CreateService(
                factory,
                new ConnectionPolicy(
                    TimeSpan.FromMilliseconds(25),
                    TimeSpan.FromMilliseconds(25),
                    TimeSpan.Zero,
                    1));

            await service.ConnectAsync("device-1").WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(1, start.CancelCount);
            Assert.IsFalse(connection.IsDisposed);
            Assert.AreEqual(AudioConnectionState.TimedOut, service.CurrentConnection.State);

            start.Complete();
            await WaitUntilAsync(() => connection.IsDisposed);
        }

        [TestMethod]
        public async Task UserCancelReturnsPromptlyWithoutRetry()
        {
            FakeCancelableOperation start = new FakeCancelableOperation();
            FakeAudioConnection connection = new FakeAudioConnection(
                start,
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeConnectionFactory factory = new FakeConnectionFactory(connection);
            using BluetoothAudioService service = CreateService(
                factory,
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.Zero,
                    2));

            Task connectTask = service.ConnectAsync("device-1");
            await factory.FirstCreate.Task.WaitAsync(TimeSpan.FromSeconds(1));
            service.CancelCurrentOperation();
            await connectTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(1, start.CancelCount);
            Assert.AreEqual(1, factory.CreateCount);
            Assert.IsFalse(connection.IsDisposed);
            Assert.AreEqual(
                AudioConnectionState.Disconnected,
                service.CurrentConnection.State);

            start.Complete();
            await WaitUntilAsync(() => connection.IsDisposed);
        }

        [TestMethod]
        public async Task LateCompletionCannotReviveTimedOutSession()
        {
            FakeCancelableOperation start = new FakeCancelableOperation();
            FakeAudioConnection connection = new FakeAudioConnection(
                start,
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            using BluetoothAudioService service = CreateService(
                new FakeConnectionFactory(connection),
                new ConnectionPolicy(
                    TimeSpan.FromMilliseconds(25),
                    TimeSpan.FromMilliseconds(25),
                    TimeSpan.Zero,
                    1));

            await service.ConnectAsync("device-1").WaitAsync(TimeSpan.FromSeconds(1));
            start.Complete();
            await Task.Delay(25);

            Assert.AreEqual(AudioConnectionState.TimedOut, service.CurrentConnection.State);
            Assert.AreEqual(1, start.CloseCount);
        }

        [TestMethod]
        public async Task RetryCreatesFreshEnabledConnectionAndCanSucceed()
        {
            FakeAudioConnection first = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.RequestTimedOut));
            FakeAudioConnection second = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeConnectionFactory factory = new FakeConnectionFactory(first, second);
            using BluetoothAudioService service = CreateService(
                factory,
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    2,
                    reconnectMinimumDelay: TimeSpan.Zero,
                    reconnectDisconnectTimeout: TimeSpan.Zero));

            await service.ConnectAsync("device-1").WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(2, factory.CreateCount);
            Assert.AreEqual(1, first.StartCallCount);
            Assert.AreEqual(1, first.OpenCallCount);
            Assert.IsTrue(first.IsDisposed);
            Assert.AreEqual(1, second.StartCallCount);
            Assert.AreEqual(1, second.OpenCallCount);
            Assert.IsFalse(second.IsDisposed);
            Assert.AreEqual(AudioConnectionState.Connected, service.CurrentConnection.State);
        }

        [TestMethod]
        public async Task TimedOutOpenMustDrainBeforeSecondOpenStarts()
        {
            FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus> firstOpen =
                new FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>();
            FakeAudioConnection first = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                firstOpen);
            FakeAudioConnection second = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            using BluetoothAudioService service = CreateService(
                new FakeConnectionFactory(first, second),
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(25),
                    TimeSpan.Zero,
                    2,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    TimeSpan.Zero));

            Task connectTask = service.ConnectAsync("device-1");
            await WaitUntilAsync(() => firstOpen.CancelCount == 1);
            Assert.AreEqual(1, first.OpenCallCount);
            Assert.AreEqual(0, second.OpenCallCount);

            firstOpen.Complete(AudioPlaybackConnectionOpenResultStatus.UnknownFailure);
            await connectTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.IsTrue(first.IsDisposed);
            Assert.AreEqual(1, second.OpenCallCount);
            Assert.AreEqual(AudioConnectionState.Connected, service.CurrentConnection.State);
        }

        [TestMethod]
        public async Task NewSessionCannotCreateConnectionUntilCanceledStartDrains()
        {
            FakeCancelableOperation firstStart = new FakeCancelableOperation();
            FakeAudioConnection first = new FakeAudioConnection(
                firstStart,
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeAudioConnection second = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeConnectionFactory factory = new FakeConnectionFactory(first, second);
            using BluetoothAudioService service = CreateService(
                factory,
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    1,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero));

            Task firstSession = service.ConnectAsync("device-1");
            await WaitUntilAsync(() => first.StartCallCount == 1);
            service.CancelCurrentOperation();
            await firstSession.WaitAsync(TimeSpan.FromSeconds(1));

            Task secondSession = service.ConnectAsync("device-1");
            await Task.Delay(40);
            Assert.AreEqual(1, factory.CreateCount);

            firstStart.Complete();
            await secondSession.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(2, factory.CreateCount);
            Assert.AreEqual(AudioConnectionState.Connected, service.CurrentConnection.State);
        }

        [TestMethod]
        public async Task ClosedDuringPendingOpenCannotPublishGhostConnected()
        {
            FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus> open =
                new FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>();
            FakeAudioConnection connection = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                open);
            using BluetoothAudioService service = CreateService(
                new FakeConnectionFactory(connection),
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    1,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromMilliseconds(10)));

            Task connectTask = service.ConnectAsync("device-1");
            await WaitUntilAsync(() => connection.OpenCallCount == 1);
            connection.State = AudioPlaybackConnectionState.Closed;
            connection.RaiseStateChanged();
            open.Complete(AudioPlaybackConnectionOpenResultStatus.Success);
            await connectTask.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(AudioConnectionState.Failed, service.CurrentConnection.State);
            Assert.IsTrue(connection.IsDisposed);
        }

        [TestMethod]
        public async Task NativeClosedDisposesEnabledConnectionBeforeRecovery()
        {
            FakeAudioConnection connection = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            using BluetoothAudioService service = CreateService(
                new FakeConnectionFactory(connection),
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    1,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero));

            await service.ConnectAsync("device-1");
            connection.State = AudioPlaybackConnectionState.Closed;
            connection.RaiseStateChanged();

            Assert.AreEqual(AudioConnectionState.Disconnected, service.CurrentConnection.State);
            await WaitUntilAsync(() => connection.IsDisposed);
        }

        [TestMethod]
        public async Task AutomaticRecoveryCreatesFreshNativeConnection()
        {
            FakeAudioConnection first = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeAudioConnection second = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeConnectionFactory factory = new FakeConnectionFactory(first, second);
            using BluetoothAudioService service = CreateService(
                factory,
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    1,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero));

            await service.ConnectAsync("device-1");
            first.State = AudioPlaybackConnectionState.Closed;
            first.RaiseStateChanged();

            await WaitUntilAsync(
                () => factory.CreateCount == 2 &&
                    service.CurrentConnection.State == AudioConnectionState.Connected,
                TimeSpan.FromSeconds(2));

            Assert.IsTrue(first.IsDisposed);
            Assert.AreEqual(1, second.StartCallCount);
            Assert.AreEqual(1, second.OpenCallCount);
        }

        [TestMethod]
        public async Task ReconnectWaitsForSystemDisconnectBeforeCreatingFreshConnection()
        {
            FakeAudioConnection first = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeAudioConnection second = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeConnectionFactory factory = new FakeConnectionFactory(first, second);
            bool systemConnected = true;
            using BluetoothAudioService service = CreateService(
                factory,
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    1,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(1)),
                id => CreateDevice(id, systemConnected));

            await service.ConnectAsync("device-1");
            Task reconnect = service.ReconnectAsync("device-1");
            await WaitUntilAsync(() => first.IsDisposed);
            await Task.Delay(50);

            Assert.AreEqual(1, factory.CreateCount);

            systemConnected = false;
            await reconnect.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(2, factory.CreateCount);
            Assert.AreEqual(AudioConnectionState.Connected, service.CurrentConnection.State);
        }

        [TestMethod]
        public async Task InitialSuccessfulConnectIsStabilizedWithFreshConnection()
        {
            FakeAudioConnection first = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeAudioConnection second = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeConnectionFactory factory = new FakeConnectionFactory(first, second);
            using BluetoothAudioService service = CreateService(
                factory,
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    2,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    TimeSpan.Zero,
                    true));

            await service.ConnectAsync("device-1");

            Assert.AreEqual(2, factory.CreateCount);
            Assert.IsTrue(first.IsDisposed);
            Assert.AreEqual(1, first.StartCallCount);
            Assert.AreEqual(1, first.OpenCallCount);
            Assert.IsFalse(second.IsDisposed);
            Assert.AreEqual(1, second.StartCallCount);
            Assert.AreEqual(1, second.OpenCallCount);
            Assert.AreEqual(AudioConnectionState.Connected, service.CurrentConnection.State);
        }

        [TestMethod]
        public async Task LaterConnectCreatesFreshLeaseAfterPreviousOpenFailure()
        {
            FakeAudioConnection first = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.UnknownFailure));
            FakeAudioConnection second = new FakeAudioConnection(
                FakeCancelableOperation.Completed(),
                FakeCancelableOperation<AudioPlaybackConnectionOpenResultStatus>.Completed(
                    AudioPlaybackConnectionOpenResultStatus.Success));
            FakeConnectionFactory factory = new FakeConnectionFactory(first, second);
            using BluetoothAudioService service = CreateService(
                factory,
                new ConnectionPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero,
                    1,
                    TimeSpan.FromSeconds(1),
                    TimeSpan.Zero));

            await service.ConnectAsync("device-1");
            Assert.AreEqual(AudioConnectionState.Failed, service.CurrentConnection.State);

            await service.ConnectAsync("device-1");

            Assert.AreEqual(2, factory.CreateCount);
            Assert.IsTrue(first.IsDisposed);
            Assert.AreEqual(1, second.StartCallCount);
            Assert.AreEqual(1, second.OpenCallCount);
            Assert.AreEqual(AudioConnectionState.Connected, service.CurrentConnection.State);
        }

        private static async Task WaitUntilAsync(
            Func<bool> predicate,
            TimeSpan? timeout = null)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(1));
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Assert.Fail("Timed out waiting for the expected test state.");
                }

                await Task.Delay(10);
            }
        }

        private static BluetoothAudioService CreateService(
            IAudioConnectionFactory factory,
            ConnectionPolicy policy)
        {
            return CreateService(factory, policy, id => CreateDevice(id, false));
        }

        private static BluetoothAudioService CreateService(
            IAudioConnectionFactory factory,
            ConnectionPolicy policy,
            Func<string, BluetoothDeviceSnapshot> deviceResolver)
        {
            return new BluetoothAudioService(
                new FakeLogger(),
                factory,
                policy,
                deviceResolver);
        }

        private static BluetoothDeviceSnapshot CreateDevice(
            string id,
            bool systemConnected)
        {
            return new BluetoothDeviceSnapshot(
                id,
                Guid.NewGuid(),
                "Test phone",
                DeviceAvailability.Nearby,
                systemConnected,
                false);
        }
    }

    internal sealed class FakeConnectionFactory : IAudioConnectionFactory
    {
        private readonly Queue<IAudioConnection> connections;

        public FakeConnectionFactory(params IAudioConnection[] connections)
        {
            this.connections = new Queue<IAudioConnection>(connections);
        }

        public TaskCompletionSource<bool> FirstCreate { get; } =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CreateCount { get; private set; }

        public IAudioConnection Create(string deviceId)
        {
            CreateCount++;
            FirstCreate.TrySetResult(true);
            return connections.Dequeue();
        }
    }

    internal sealed class FakeAudioConnection : IAudioConnection
    {
        private readonly Queue<ICancelableOperation> starts;
        private readonly Queue<ICancelableOperation<AudioPlaybackConnectionOpenResultStatus>> opens;

        public FakeAudioConnection(
            ICancelableOperation start,
            params ICancelableOperation<AudioPlaybackConnectionOpenResultStatus>[] opens)
        {
            starts = new Queue<ICancelableOperation>(new[] { start });
            this.opens = new Queue<ICancelableOperation<AudioPlaybackConnectionOpenResultStatus>>(
                opens);
        }

        public event EventHandler StateChanged;

        public AudioPlaybackConnectionState State { get; set; } =
            AudioPlaybackConnectionState.Opened;

        public bool IsDisposed { get; private set; }

        public int StartCallCount { get; private set; }

        public int OpenCallCount { get; private set; }

        public ICancelableOperation StartAsync()
        {
            StartCallCount++;
            return starts.Peek();
        }

        public ICancelableOperation<AudioPlaybackConnectionOpenResultStatus> OpenAsync()
        {
            OpenCallCount++;
            return opens.Count > 1 ? opens.Dequeue() : opens.Peek();
        }

        public void Dispose()
        {
            IsDisposed = true;
        }

        public void RaiseStateChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal class FakeCancelableOperation : ICancelableOperation
    {
        private readonly TaskCompletionSource<bool> completion =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CancelCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task Completion => completion.Task;

        public static FakeCancelableOperation Completed()
        {
            FakeCancelableOperation operation = new FakeCancelableOperation();
            operation.Complete();
            return operation;
        }

        public void Complete()
        {
            completion.TrySetResult(true);
        }

        public void Cancel()
        {
            CancelCount++;
        }

        public void Close()
        {
            CloseCount++;
        }
    }

    internal sealed class FakeCancelableOperation<T> : ICancelableOperation<T>
    {
        private readonly TaskCompletionSource<T> completion =
            new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CancelCount { get; private set; }

        public int CloseCount { get; private set; }

        public Task<T> Completion => completion.Task;

        Task ICancelableOperation.Completion => Completion;

        public static FakeCancelableOperation<T> Completed(T value)
        {
            FakeCancelableOperation<T> operation = new FakeCancelableOperation<T>();
            operation.completion.TrySetResult(value);
            return operation;
        }

        public void Complete(T value)
        {
            completion.TrySetResult(value);
        }

        public void Cancel()
        {
            CancelCount++;
        }

        public void Close()
        {
            CloseCount++;
        }
    }

    internal sealed class FakeLogger : IDiagnosticLogger
    {
        public string LogDirectory => string.Empty;

        public string CreateCorrelationId() => "TEST0001";

        public string GetAnonymousDeviceId(string deviceId) => "DEVICE000001";

        public void Log(
            DiagnosticLevel level,
            string eventName,
            string message = null,
            Exception exception = null,
            params (string Key, object Value)[] fields)
        {
        }

        public void Dispose()
        {
        }
    }
}
