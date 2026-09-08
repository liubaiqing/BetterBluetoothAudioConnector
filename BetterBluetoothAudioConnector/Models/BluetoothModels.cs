using System;
using System.Collections.Generic;

namespace BetterBluetoothAudioConnector.Models
{
    public enum DeviceAvailability
    {
        Checking,
        Nearby,
        Offline,
        Unknown
    }

    public enum AudioConnectionState
    {
        Idle,
        Connecting,
        Connected,
        Canceling,
        Disconnecting,
        Disconnected,
        TimedOut,
        Failed
    }

    public enum DeviceWatcherState
    {
        Searching,
        Ready,
        Retrying,
        Unavailable,
        Stopped
    }

    public sealed class BluetoothDeviceSnapshot
    {
        public BluetoothDeviceSnapshot(
            string id,
            Guid? containerId,
            string name,
            DeviceAvailability availability,
            bool isSystemConnected,
            bool isAudioConnected)
        {
            Id = id;
            ContainerId = containerId;
            Name = name;
            Availability = availability;
            IsSystemConnected = isSystemConnected;
            IsAudioConnected = isAudioConnected;
        }

        public string Id { get; }

        public Guid? ContainerId { get; }

        public string Name { get; }

        public DeviceAvailability Availability { get; }

        public bool IsSystemConnected { get; }

        public bool IsAudioConnected { get; }
    }

    public sealed class ConnectionSnapshot
    {
        public ConnectionSnapshot(
            AudioConnectionState state,
            string deviceId,
            string stage,
            int attempt,
            int maximumAttempts,
            string anonymousDeviceId,
            string errorSummary,
            string correlationId)
        {
            State = state;
            DeviceId = deviceId;
            Stage = stage;
            Attempt = attempt;
            MaximumAttempts = maximumAttempts;
            AnonymousDeviceId = anonymousDeviceId;
            ErrorSummary = errorSummary;
            CorrelationId = correlationId;
        }

        public AudioConnectionState State { get; }

        public string DeviceId { get; }

        public string Stage { get; }

        public int Attempt { get; }

        public int MaximumAttempts { get; }

        public string AnonymousDeviceId { get; }

        public string ErrorSummary { get; }

        public string CorrelationId { get; }

        public static ConnectionSnapshot Idle { get; } =
            new ConnectionSnapshot(
                AudioConnectionState.Idle,
                null,
                null,
                0,
                0,
                null,
                null,
                null);
    }

    public sealed class DevicesChangedEventArgs : EventArgs
    {
        public DevicesChangedEventArgs(IReadOnlyList<BluetoothDeviceSnapshot> devices)
        {
            Devices = devices;
        }

        public IReadOnlyList<BluetoothDeviceSnapshot> Devices { get; }
    }

    public sealed class ConnectionChangedEventArgs : EventArgs
    {
        public ConnectionChangedEventArgs(ConnectionSnapshot connection)
        {
            Connection = connection;
        }

        public ConnectionSnapshot Connection { get; }
    }

    public sealed class WatcherStateChangedEventArgs : EventArgs
    {
        public WatcherStateChangedEventArgs(DeviceWatcherState state, string message)
        {
            State = state;
            Message = message;
        }

        public DeviceWatcherState State { get; }

        public string Message { get; }
    }
}
