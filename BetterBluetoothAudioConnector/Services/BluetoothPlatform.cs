using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Audio;

namespace BetterBluetoothAudioConnector.Services
{
    internal interface IAudioConnectionFactory
    {
        IAudioConnection Create(string deviceId);
    }

    internal interface IAudioConnection : IDisposable
    {
        event EventHandler StateChanged;

        AudioPlaybackConnectionState State { get; }

        ICancelableOperation StartAsync();

        ICancelableOperation<AudioPlaybackConnectionOpenResultStatus> OpenAsync();
    }

    internal interface ICancelableOperation
    {
        Task Completion { get; }

        void Cancel();

        void Close();
    }

    internal interface ICancelableOperation<T> : ICancelableOperation
    {
        new Task<T> Completion { get; }
    }

    internal sealed class WinRtAudioConnectionFactory : IAudioConnectionFactory
    {
        public IAudioConnection Create(string deviceId)
        {
            AudioPlaybackConnection connection =
                AudioPlaybackConnection.TryCreateFromId(deviceId);
            return connection == null ? null : new WinRtAudioConnection(connection);
        }
    }

    internal sealed class WinRtAudioConnection : IAudioConnection
    {
        private readonly AudioPlaybackConnection connection;

        public WinRtAudioConnection(AudioPlaybackConnection connection)
        {
            this.connection = connection;
            connection.StateChanged += Connection_StateChanged;
        }

        public event EventHandler StateChanged;

        public AudioPlaybackConnectionState State => connection.State;

        public ICancelableOperation StartAsync()
        {
            return new WinRtActionOperation(connection.StartAsync());
        }

        public ICancelableOperation<AudioPlaybackConnectionOpenResultStatus> OpenAsync()
        {
            return new WinRtOpenOperation(connection.OpenAsync());
        }

        public void Dispose()
        {
            connection.StateChanged -= Connection_StateChanged;
            connection.Dispose();
        }

        private void Connection_StateChanged(AudioPlaybackConnection sender, object args)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal sealed class WinRtActionOperation : ICancelableOperation
    {
        private readonly IAsyncAction operation;

        public WinRtActionOperation(IAsyncAction operation)
        {
            this.operation = operation;
            Completion = operation.AsTask();
        }

        public Task Completion { get; }

        public void Cancel()
        {
            operation.Cancel();
        }

        public void Close()
        {
            operation.Close();
        }
    }

    internal sealed class WinRtOpenOperation :
        ICancelableOperation<AudioPlaybackConnectionOpenResultStatus>
    {
        private readonly IAsyncOperation<AudioPlaybackConnectionOpenResult> operation;

        public WinRtOpenOperation(
            IAsyncOperation<AudioPlaybackConnectionOpenResult> operation)
        {
            this.operation = operation;
            Completion = GetStatusAsync(operation);
        }

        public Task<AudioPlaybackConnectionOpenResultStatus> Completion { get; }

        Task ICancelableOperation.Completion => Completion;

        public void Cancel()
        {
            operation.Cancel();
        }

        public void Close()
        {
            operation.Close();
        }

        private static async Task<AudioPlaybackConnectionOpenResultStatus> GetStatusAsync(
            IAsyncOperation<AudioPlaybackConnectionOpenResult> operation)
        {
            AudioPlaybackConnectionOpenResult result = await operation;
            return result.Status;
        }
    }

    internal sealed class ConnectionPolicy
    {
        public static ConnectionPolicy Default { get; } = new ConnectionPolicy(
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(1),
            3,
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(350),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            true);

        public ConnectionPolicy(
            TimeSpan startTimeout,
            TimeSpan openTimeout,
            TimeSpan retryDelay,
            int maximumAttempts,
            TimeSpan? operationDrainTimeout = null,
            TimeSpan? openStabilityDelay = null,
            TimeSpan? reconnectMinimumDelay = null,
            TimeSpan? reconnectDisconnectTimeout = null,
            bool stabilizeInitialConnection = false)
        {
            StartTimeout = startTimeout;
            OpenTimeout = openTimeout;
            RetryDelay = retryDelay;
            MaximumAttempts = maximumAttempts;
            OperationDrainTimeout = operationDrainTimeout ?? TimeSpan.FromSeconds(15);
            OpenStabilityDelay = openStabilityDelay ?? TimeSpan.FromMilliseconds(100);
            ReconnectMinimumDelay = reconnectMinimumDelay ?? TimeSpan.FromSeconds(2);
            ReconnectDisconnectTimeout = reconnectDisconnectTimeout ??
                TimeSpan.FromSeconds(5);
            StabilizeInitialConnection = stabilizeInitialConnection;
        }

        public TimeSpan StartTimeout { get; }

        public TimeSpan OpenTimeout { get; }

        public TimeSpan RetryDelay { get; }

        public int MaximumAttempts { get; }

        public TimeSpan OperationDrainTimeout { get; }

        public TimeSpan OpenStabilityDelay { get; }

        public TimeSpan ReconnectMinimumDelay { get; }

        public TimeSpan ReconnectDisconnectTimeout { get; }

        public bool StabilizeInitialConnection { get; }

        public TimeSpan GetRetryDelay(int completedAttempt)
        {
            double multiplier = Math.Pow(2, Math.Max(0, completedAttempt - 1));
            double milliseconds = Math.Min(
                RetryDelay.TotalMilliseconds * multiplier,
                TimeSpan.FromSeconds(5).TotalMilliseconds);
            return TimeSpan.FromMilliseconds(milliseconds);
        }
    }
}
