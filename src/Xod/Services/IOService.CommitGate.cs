using System;
using System.IO;

namespace Xod.Services
{
    internal partial class IOService
    {
        /// <summary>
        /// The commit gate (ISS-164), <c>xod.commit.lock</c> beside the folder's lock file, never deleted. A commit
        /// holds it exclusively from before it compares its files with the disk and writes its journal until its
        /// journal is deleted, or its undo is done (XodTransaction.Commit). Every other write of a data file holds it
        /// shared, from its journal check (the engine's hook, NotBesideAnotherJournal) to the end of the write
        /// (<see cref="InCommitGate"/>). So no write of another context lands while a journal is in the folder: a look
        /// for a journal is a hold, and a commit's undo can't take back a write acknowledged beside it. Before, the look
        /// held nothing, and a commit could write its journal between another context's look and its write.
        /// <para>
        /// A file lock, so it holds across processes and between contexts of one process. Windows: the sharing of the
        /// open (FileShare.None refuses every other open, of this process or another). Unix: .NET's flock, LOCK_EX for
        /// an open that shares nothing, LOCK_SH for the others, both without blocking (SafeFileHandle.Unix.cs,
        /// release/8.0 and release/9.0); only EWOULDBLOCK is reported, any other failure to lock is ignored. Nothing
        /// signals its release, so a taker asks again, as an open asks for a held lock file (<see cref="OpenOneLockFile"/>),
        /// up to TransactionTimeoutMs.
        /// </para>
        /// <para>
        /// Not proven exclusive, unlike a hold alone, and weaker on some file systems (ISS-104's family). An NFS client
        /// (Linux 2.6.12 and later) emulates flock as an fcntl lock on the whole file, which belongs to the process, not
        /// the open (flock(2), "NFS details"; fcntl_locking(2)): the gate holds between processes, keeps nothing apart
        /// between contexts of one process, and a context that closes its shared hold drops every hold of its process,
        /// a commit's exclusive one with it. Mounted with local_lock or nolock, it holds on one machine only. An SMB
        /// client before Linux 5.5 doesn't pass flock to the server (flock(2), "CIFS details"). A gate file this user
        /// can only read is opened for reading, and NFS takes no exclusive lock on such an open, without a word. With
        /// file locking switched off on Unix, nothing is kept apart. Where nothing is, ISS-147's look for a journal is
        /// what is left. XOD 2.0.2 and earlier don't take the gate at all.
        /// </para>
        /// </summary>
        internal const string CommitGateName = "xod.commit.lock";

        // This context's hold on the commit gate: shared, by every thread of it writing now (counted), or exclusive,
        // by its commit. commitGateUnavailable: the gate can't be created (a folder this user can't write, where this
        // context writes no data file either).
        private readonly object commitGateLock = new object();
        private FileStream commitGateShared;
        private int commitGateSharers;
        private FileStream commitGateExclusive;
        private bool commitGateUnavailable;

        /// <summary>How long a write waits for another context's commit, and a commit for the writes of others:
        /// the engine's TransactionTimeoutMs.</summary>
        internal int CommitGateWaitMs { get; set; } = 30000;

        private string CommitGatePath => Path.Combine(root, CommitGateName);

        /// <summary>
        /// Runs <paramref name="write"/> holding the commit gate shared, unless this context's commit holds it (its
        /// writes are the commit's). Every write of a data file runs inside it, its check in the engine's hook
        /// included; a write outside it is refused where it would be made (<see cref="RequireCommitGate"/>).
        /// </summary>
        private void InCommitGate(Action write)
        {
            bool entered = EnterCommitGate();
            try
            {
                write();
            }
            finally
            {
                if (entered)
                    ExitCommitGate();
            }
        }

        private bool EnterCommitGate()
        {
            lock (commitGateLock)
            {
                if (commitGateExclusive != null || commitGateUnavailable)
                    return false;
                if (commitGateShared != null)
                {
                    commitGateSharers++;
                    return true;
                }
            }

            // Waited for outside the lock above; another thread of this context may open its own meanwhile (two
            // shared opens agree), and the second is let go.
            FileStream gate = OpenCommitGate(exclusive: false);
            lock (commitGateLock)
            {
                if (gate == null)
                {
                    commitGateUnavailable = true;
                    return false;
                }
                if (commitGateShared == null)
                    commitGateShared = gate;
                else
                    gate.Dispose();
                commitGateSharers++;
                return true;
            }
        }

        private void ExitCommitGate()
        {
            lock (commitGateLock)
            {
                if (--commitGateSharers > 0)
                    return;
                commitGateShared?.Dispose();
                commitGateShared = null;
            }
        }

        /// <summary>
        /// Holds the commit gate exclusively for this context's commit, until the returned hold is disposed: waits
        /// for the writes of other contexts to end, up to <see cref="CommitGateWaitMs"/>, then refuses the commit. A
        /// folder where the gate can't be created holds nothing (no data file is written there).
        /// </summary>
        internal IDisposable HoldCommitGateExclusively()
        {
            FileStream gate = OpenCommitGate(exclusive: true);
            lock (commitGateLock)
            {
                commitGateExclusive = gate;
            }
            return new CommitGateHold(this, gate);
        }

        private sealed class CommitGateHold : IDisposable
        {
            private readonly IOService owner;
            private FileStream gate;

            internal CommitGateHold(IOService owner, FileStream gate)
            {
                this.owner = owner;
                this.gate = gate;
            }

            public void Dispose()
            {
                lock (owner.commitGateLock)
                {
                    if (ReferenceEquals(owner.commitGateExclusive, gate))
                        owner.commitGateExclusive = null;
                }
                gate?.Dispose();
                gate = null;
            }
        }

        // The gate opened shared or exclusively, waited for while another context holds it the other way. Null where
        // it isn't there and can't be created.
        private FileStream OpenCommitGate(bool exclusive)
        {
            try
            {
                return OpenOneLockFile(CommitGatePath, path, exclusive, CommitGateWaitMs, prove: false);
            }
            catch (UnauthorizedAccessException) when (!File.Exists(CommitGatePath))
            {
                return null;
            }
            catch (DatabaseLockedException e)
            {
                if (exclusive)
                    throw new TransactionException("Other contexts of the database in '" + root + "' were writing or committing, and went on for longer " +
                        "than TransactionTimeoutMs (" + CommitGateWaitMs + " ms): this commit is refused, and nothing of it was written.", e);
                throw new CommitGateBusyException("Another context's commit is under way in '" + root + "' and didn't end within TransactionTimeoutMs (" +
                    CommitGateWaitMs + " ms). This write is refused rather than made beside it, where an undo of that commit would undo it too.", e);
            }
        }

        /// <summary>
        /// Refuses a write of a data file made outside the commit gate: a write path that doesn't take it would land
        /// beside another context's commit (ISS-164). Index files and the database file are outside every journal (a
        /// journal never names them, TransactionService.CaptureBeforeMutate), and aren't asked.
        /// </summary>
        private void RequireCommitGate(string filePath)
        {
            if (IsIndex(filePath) || Helpers.DatabaseFiles.IsDatabaseFile(filePath))
                return;
            lock (commitGateLock)
            {
                if (commitGateShared != null || commitGateExclusive != null || commitGateUnavailable)
                    return;
            }
            throw new InvalidOperationException("A write of '" + filePath + "' outside the commit gate (ISS-164): every write of a data file " +
                "holds it (IOService.InCommitGate).");
        }

        /// <summary>
        /// The bytes of a file on disk now, as this context's transaction left it: a file it set aside (deleted inside
        /// the transaction, ISS-107) is read where it waits. Null when it isn't there, and for a file set aside that is
        /// at its name again: another context wrote it there (a page it had read, or a new table at a table file's fixed
        /// name), and the commit, deleting what it set aside, would leave that in place of its delete. For a commit's
        /// comparison with what it first read (XodTransaction.Commit).
        /// </summary>
        internal byte[] BytesOnDisk(string filePath)
        {
            bool aside;
            lock (syncLock)
            {
                aside = setAside.Contains(filePath);
            }
            if (aside)
                return File.Exists(filePath) ? null : File.ReadAllBytes(filePath + SetAsideSuffix);
            return File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;
        }

        /// <summary>
        /// Refuses a write of a data file while this context's transaction is open. A transaction's files are written
        /// by its commit alone (its batch is written once the transaction has completed, under the commit gate held
        /// exclusively), so before that nothing of it is on disk but the files it set aside, which its rollback puts
        /// back, and the rollback has nothing else to take back (XodTransaction.Rollback). Index files and the database
        /// file are outside every journal and aren't asked.
        /// </summary>
        private void RefuseWriteInsideTransaction(string filePath)
        {
            if (IsIndex(filePath) || Helpers.DatabaseFiles.IsDatabaseFile(filePath))
                return;
            if (HasActiveTransaction != null && HasActiveTransaction())
                throw new InvalidOperationException("A write of '" + filePath + "' while its context's transaction is open: a transaction's " +
                    "files are written by its commit alone.");
        }

        private void ReleaseCommitGate()
        {
            lock (commitGateLock)
            {
                commitGateShared?.Dispose();
                commitGateShared = null;
                commitGateSharers = 0;
                commitGateExclusive?.Dispose();
                commitGateExclusive = null;
            }
        }
    }
}
