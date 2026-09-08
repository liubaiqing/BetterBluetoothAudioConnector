using System;

namespace BetterBluetoothAudioConnector.Diagnostics
{
    public interface IDiagnosticLogger : IDisposable
    {
        string LogDirectory { get; }

        string CreateCorrelationId();

        string GetAnonymousDeviceId(string deviceId);

        void Log(
            DiagnosticLevel level,
            string eventName,
            string message = null,
            Exception exception = null,
            params (string Key, object Value)[] fields);
    }

    public enum DiagnosticLevel
    {
        Debug,
        Information,
        Warning,
        Error,
        Critical
    }
}
