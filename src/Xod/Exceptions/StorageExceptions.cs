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

    /// <summary>
    /// The database is open in another context or process. A database is open in one context at a time (PLAN-064):
    /// dispose the other context first, keep one context for the life of the application, or set
    /// <see cref="DatabaseOptions.LockRetryTimeoutMs"/> to wait for it.
    /// </summary>
    public class DatabaseLockedException : XodException
    {
        public DatabaseLockedException(string databasePath)
            : base(Text(databasePath)) { }
        public DatabaseLockedException(string databasePath, Exception innerException)
            : base(Text(databasePath), innerException) { }

        private static string Text(string databasePath) =>
            $"The database at '{databasePath}' is locked: another process or XodContext instance has it open. A database is " +
            "open in one context at a time; dispose the other context, or set LockRetryTimeoutMs to wait for it.";
    }
}
