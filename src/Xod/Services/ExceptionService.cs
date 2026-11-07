using System;

namespace Xod.Services
{
    internal class ExceptionService
    {
        private readonly string path;

        public ExceptionService(string path)
        {
            this.path = path;
        }

        public void Throw(Exception exception)
        {
            throw exception;
        }
    }
}
