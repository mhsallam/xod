using System;

namespace Xod
{
    public class DatabaseFileException : XodException
    {
        public DatabaseFileException() : base("An error occurred while loading database file. Make sure of the database path or set InitialCreate option.") { }
        public DatabaseFileException(string message) : base(message) { }
    }

    public class SecurityException : XodException
    {
        public SecurityException() : base("Unable to open database file; a valid password is required.") { }
        public SecurityException(string message) : base(message) { }
    }

    public class TransactionException : XodException
    {
        public TransactionException(string message) : base(message) { }
        public TransactionException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class DatabaseLockedException : XodException
    {
        public DatabaseLockedException(string databasePath)
            : base($"The database at '{databasePath}' is exclusively locked by another process or XodContext instance.") { }
        public DatabaseLockedException(string databasePath, Exception innerException)
            : base($"The database at '{databasePath}' is exclusively locked by another process or XodContext instance.", innerException) { }
    }
}
