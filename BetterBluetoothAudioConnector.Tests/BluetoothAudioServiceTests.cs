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
        public async Task StartTimeoutCancelsOperationAndDisposesConnection()
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
            Assert.IsTrue(connection.IsDisposed);
            Assert.AreEqual(AudioConnectionState.TimedOut, service.CurrentConnection.State);
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
            Assert.IsTrue(connection.IsDisposed);
            Assert.AreEqual(
                AudioConnectionState.Disconnected,
                service.CurrentConnection.State);
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
        public async Task RetryCreatesFreshConnectionAndCanSucceed()
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
                    2));

            await service.ConnectAsync("device-1").WaitAsync(TimeSpan.FromSeconds(1));

            Assert.AreEqual(2, factory.CreateCount);
            Assert.IsTrue(first.IsDisposed);
            Assert.IsFalse(second.IsDisposed);
            Assert.AreEqual(AudioConnectionState.Connected, service.CurrentConnection.State);
        }

        private static BluetoothAudioService CreateService(
            IAudioConnectionFactory factory,
            ConnectionPolicy policy)
        {
            return new BluetoothAudioService(
                new FakeLogger(),
                factory,
                policy,
                id => new BluetoothDeviceSnapshot(
                    id,
                    Guid.NewGuid(),
                    "Test phone",
                    DeviceAvailability.Nearby,
                    false,
                    false));
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
        private readonly ICancelableOperation start;
        private readonly ICancelableOperation<AudioPlaybackConnectionOpenResultStatus> open;

        public FakeAudioConnection(
            ICancelableOperation start,
            ICancelableOperation<AudioPlaybackConnectionOpenResultStatus> open)
        {
            this.start = start;
            this.open = open;
        }

        public event EventHandler StateChanged;

        public AudioPlaybackConnectionState State { get; set; } =
            AudioPlaybackConnectionState.Opened;

        public bool IsDisposed { get; private set; }

        public ICancelableOperation StartAsync() => start;

        public ICancelableOperation<AudioPlaybackConnectionOpenResultStatus> OpenAsync() => open;

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
