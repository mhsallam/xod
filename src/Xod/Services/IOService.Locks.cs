using System;
using System.IO;

namespace Xod.Services
{
    internal partial class IOService
    {
        private void AcquireDatabaseLock(int retryTimeoutMs)
        {
            this.lockFilePath = this.path + ".lock";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int delayMs = 5;

            while (true)
            {
                try
                {
                    this.lockStream = new FileStream(
                        this.lockFilePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None);
                    return;
                }
                catch (IOException ex)
                {
                    if (retryTimeoutMs <= 0 || sw.ElapsedMilliseconds >= retryTimeoutMs)
                    {
                        throw new DatabaseLockedException(this.path, ex);
                    }

                    int remaining = retryTimeoutMs - (int)sw.ElapsedMilliseconds;
                    if (remaining <= 0)
                    {
                        throw new DatabaseLockedException(this.path, ex);
                    }

                    System.Threading.Thread.Sleep(Math.Min(delayMs, remaining));
                    delayMs = Math.Min(delayMs * 2, 50);
                }
            }
        }
    }
}
