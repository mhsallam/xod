using System;
using System.IO;

namespace Xod.Services
{
    internal class LogService
    {
        private readonly string root;
        private readonly object syncLock = new object();

        public LogService(string root)
        {
            this.root = root;
        }

        public void Log(string message)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                return;

            lock (syncLock)
            {
                string logFile = Path.Combine(root, "xod.log");
                File.AppendAllText(logFile, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
            }
        }
    }
}
