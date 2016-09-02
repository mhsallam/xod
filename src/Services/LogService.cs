using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace Xod.Services
{
    internal class LogService
    {
        string root { get; set; }

        public LogService(string root)
        {
            this.root = root;
        }

        public void Log(string message, LogType type, KeyValuePair<string, string>[] details = null, [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string caller = null, [CallerFilePath] string filePath = null)
        {
            if (string.IsNullOrEmpty(this.root))
                return;

            FileAttributes attr = File.GetAttributes(this.root);
            if (attr.HasFlag(FileAttributes.Directory))
            {
                string logPath = Path.Combine(this.root, "log");
                if (!Directory.Exists(logPath))
                    Directory.CreateDirectory(logPath);

                string targetFile = (type == LogType.Message) ? "message.txt" : "error.txt";
                string logFile = Path.Combine(this.root, "log", targetFile);


                StringBuilder sb = new StringBuilder(string.Format(":: [{0}] - {1}", DateTime.Now.ToString(), message) + Environment.NewLine);
                if (details != null)
                {
                    foreach (var item in details)
                    {
                        sb.Append(string.Format("     -[{0}]: {1}", item.Key, item.Value) + Environment.NewLine);
                    }
                }

                File.AppendAllText(logFile, sb.ToString());
            }
        }
    }

    internal enum LogType
    {
        Message, Error
    }
}
