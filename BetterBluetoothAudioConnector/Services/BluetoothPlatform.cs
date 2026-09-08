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
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(500),
            2);

        public ConnectionPolicy(
            TimeSpan startTimeout,
            TimeSpan openTimeout,
            TimeSpan retryDelay,
            int maximumAttempts)
        {
            StartTimeout = startTimeout;
            OpenTimeout = openTimeout;
            RetryDelay = retryDelay;
            MaximumAttempts = maximumAttempts;
        }

        public TimeSpan StartTimeout { get; }

        public TimeSpan OpenTimeout { get; }

        public TimeSpan RetryDelay { get; }

        public int MaximumAttempts { get; }
    }
}
