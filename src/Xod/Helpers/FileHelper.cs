using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xod.Services;

namespace Xod.Helpers
{
    internal class FileHelper
    {
        /// <summary>
        /// Blocks until the file is not locked any more.
        /// </summary>
        /// <param name="filePath"></param>
        internal static bool IsReady(string filePath)
        {
            int numTries = 0;
            LogService log = new LogService(Path.GetDirectoryName(filePath));
            while (true)
            {
                ++numTries;
                try
                {
                    // Attempt to open the file exclusively.
                    using (FileStream fs = new FileStream(filePath,
                        FileMode.Open, FileAccess.Write,
                        FileShare.None, 100))
                    {
                        fs.ReadByte();

                        // If we got this far the file is ready
                        break;
                    }
                }
                catch (Exception ex)
                {
                    log.Log(
                        string.Format("IsReady('{0}') failed to get an exclusive lock: {1}", filePath, ex.ToString()),
                        LogType.Error);

                    if (numTries > 100)
                    {
                        log.Log(
                            string.Format("IsReady('{0}') giving up after 100 tries", filePath),
                            LogType.Error);
                        return false;
                    }

                    // Wait for the lock to be released
                    System.Threading.Thread.Sleep(100);
                }
            }

            //log.Log(
            //    string.Format("IsReady('{0}') returning true after {1} tries", filePath, numTries),
            //    LogType.Message);
            return true;
        }
    }
}
