using System;

namespace Xod.Services
{
    /// <summary>
    /// A write refused because another context's commit held the commit gate for longer than TransactionTimeoutMs
    /// (IOService.CommitGate). Its own type so that a rollback, which puts back the files its transaction set aside
    /// holding the gate, treats it as a put-back that couldn't be made (XmlEngine.Transactions), as for a file another
    /// program holds.
    /// </summary>
    internal sealed class CommitGateBusyException : XodException
    {
        internal CommitGateBusyException(string message, Exception innerException) : base(message, innerException) { }
    }
}
