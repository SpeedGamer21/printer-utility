using System;
using System.IO;
using System.Text;

namespace PrinterUtility
{
    /// <summary>Thread-safe logger. The UI subscribes to <see cref="Line"/>; the elevated helper writes to a file.</summary>
    internal static class Logger
    {
        private static readonly object Sync = new object();
        private static StreamWriter _file;

        public static event Action<string> Line;

        public static void UseFile(string path)
        {
            lock (Sync)
            {
                _file = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            }
        }

        public static void CloseFile()
        {
            lock (Sync)
            {
                _file?.Dispose();
                _file = null;
            }
        }

        public static void Info(string msg) => Write("INFO ", msg);
        public static void Ok(string msg) => Write("OK   ", msg);
        public static void Warn(string msg) => Write("WARN ", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        /// <summary>Re-emit an already formatted line (used for output of the elevated helper).</summary>
        public static void Raw(string line) => Emit(line);

        private static void Write(string level, string msg)
        {
            Emit($"{DateTime.Now:HH:mm:ss} {level} {msg}");
        }

        private static void Emit(string line)
        {
            lock (Sync)
            {
                _file?.WriteLine(line);
            }
            Line?.Invoke(line);
        }
    }
}
