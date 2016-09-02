using System;
using System.Runtime.CompilerServices;

namespace Xod.Services
{
    internal class ExceptionService
    {
        Xod.Services.LogService log = null;

        public ExceptionService(string path)
        {
            this.log = new Services.LogService(path);
        }

        public void Throw(Exception e, [CallerLineNumber] int lineNumber = 0, [CallerMemberName] string caller = null, [CallerFilePath] string filePath = null)
        {
            log.Log(e.Message, Services.LogType.Error, null, lineNumber, caller, filePath);
            throw e;
        }
    }
}
