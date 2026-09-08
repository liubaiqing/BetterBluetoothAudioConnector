using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BetterBluetoothAudioConnector.Diagnostics
{
    public sealed class FileDiagnosticLogger : IDiagnosticLogger
    {
        private const long DefaultMaximumFileBytes = 5L * 1024L * 1024L;
        private const int DefaultMaximumPartsPerDay = 3;
        private const int DefaultRetentionDays = 7;

        private readonly BlockingCollection<string> pendingLines =
            new BlockingCollection<string>(new ConcurrentQueue<string>(), 1000);
        private readonly CancellationTokenSource writerCancellation =
            new CancellationTokenSource();
        private readonly byte[] deviceHashKey;
        private readonly long maximumFileBytes;
        private readonly int maximumPartsPerDay;
        private readonly int retentionDays;
        private readonly Task writerTask;
        private bool disposed;
        private bool fileLoggingAvailable;

        public FileDiagnosticLogger()
            : this(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "BetterBluetoothAudioConnector"),
                DefaultMaximumFileBytes,
                DefaultMaximumPartsPerDay,
                DefaultRetentionDays)
        {
        }

        internal FileDiagnosticLogger(
            string applicationDirectory,
            long maximumFileBytes,
            int maximumPartsPerDay,
            int retentionDays)
        {
            this.maximumFileBytes = maximumFileBytes;
            this.maximumPartsPerDay = maximumPartsPerDay;
            this.retentionDays = retentionDays;
            LogDirectory = Path.Combine(applicationDirectory, "Logs");

            try
            {
                Directory.CreateDirectory(LogDirectory);
                deviceHashKey = LoadOrCreateHashKey(
                    Path.Combine(applicationDirectory, "device-id.salt"));
                DeleteExpiredLogs();
                fileLoggingAvailable = true;
            }
            catch (Exception ex)
            {
                deviceHashKey = RandomNumberGenerator.GetBytes(32);
                fileLoggingAvailable = false;
                Debug.WriteLine("Diagnostic log initialization failed: " + ex);
            }

            writerTask = Task.Run(WriteLoop);
        }

        public string LogDirectory { get; }

        public string CreateCorrelationId()
        {
            return Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
                .Substring(0, 8)
                .ToUpperInvariant();
        }

        public string GetAnonymousDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return "none";
            }

            using HMACSHA256 hmac = new HMACSHA256(deviceHashKey);
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(deviceId));
            return Convert.ToHexString(hash, 0, 6);
        }

        public void Log(
            DiagnosticLevel level,
            string eventName,
            string message = null,
            Exception exception = null,
            params (string Key, object Value)[] fields)
        {
            string line;

            try
            {
                line = FormatLine(level, eventName, message, exception, fields);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Diagnostic log formatting failed: " + ex);
                return;
            }

            Debug.WriteLine(line);

            if (disposed || !fileLoggingAvailable)
            {
                return;
            }

            if (!pendingLines.TryAdd(line))
            {
                Debug.WriteLine("Diagnostic log queue is full; an entry was dropped.");
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pendingLines.CompleteAdding();

            try
            {
                if (!writerTask.Wait(TimeSpan.FromSeconds(2)))
                {
                    writerCancellation.Cancel();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Diagnostic log shutdown failed: " + ex);
            }

            writerCancellation.Dispose();
            pendingLines.Dispose();
        }

        private static byte[] LoadOrCreateHashKey(string path)
        {
            if (File.Exists(path))
            {
                byte[] existing = File.ReadAllBytes(path);
                if (existing.Length >= 32)
                {
                    return existing;
                }
            }

            byte[] key = RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, key);
            return key;
        }

        private void DeleteExpiredLogs()
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-retentionDays);

            foreach (string path in Directory.EnumerateFiles(
                LogDirectory,
                "BetterBluetoothAudioConnector-*.log",
                SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to delete an expired diagnostic log: " + ex);
                }
            }
        }

        private void WriteLoop()
        {
            try
            {
                foreach (string line in pendingLines.GetConsumingEnumerable(
                    writerCancellation.Token))
                {
                    WriteLine(line);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                fileLoggingAvailable = false;
                Debug.WriteLine("Diagnostic log writer failed: " + ex);
            }
        }

        private void WriteLine(string line)
        {
            string baseName = "BetterBluetoothAudioConnector-" +
                DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

            for (int part = 1; part <= maximumPartsPerDay; part++)
            {
                string suffix = part == 1 ? string.Empty : "-part" + part;
                string path = Path.Combine(LogDirectory, baseName + suffix + ".log");

                if (!File.Exists(path) || new FileInfo(path).Length < maximumFileBytes)
                {
                    File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
                    return;
                }
            }

            Debug.WriteLine("Daily diagnostic log limit reached; an entry was dropped.");
        }

        private static string FormatLine(
            DiagnosticLevel level,
            string eventName,
            string message,
            Exception exception,
            (string Key, object Value)[] fields)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture));
            builder.Append(" [").Append(level).Append("] ");
            builder.Append(Sanitize(eventName));

            if (!string.IsNullOrWhiteSpace(message))
            {
                builder.Append(" message=\"").Append(Sanitize(message)).Append('"');
            }

            if (fields != null)
            {
                foreach ((string key, object value) in fields.Where(field => field.Value != null))
                {
                    builder.Append(' ')
                        .Append(Sanitize(key))
                        .Append("=\"")
                        .Append(Sanitize(Convert.ToString(value, CultureInfo.InvariantCulture)))
                        .Append('"');
                }
            }

            if (exception != null)
            {
                builder.Append(" hresult=\"0x")
                    .Append(exception.HResult.ToString("X8", CultureInfo.InvariantCulture))
                    .Append("\" exception=\"")
                    .Append(Sanitize(exception.ToString()))
                    .Append('"');
            }

            return builder.ToString();
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Replace("\"", "'", StringComparison.Ordinal);
        }
    }
}
