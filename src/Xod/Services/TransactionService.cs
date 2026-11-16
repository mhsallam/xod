using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using Xod.Infra;

namespace Xod.Services
{
    /// <summary>
    /// A transaction's undo record. Before a file is first changed its bytes are kept (or it is noted as
    /// created); the commit writes them to <c>transaction.xwal</c> before writing any file and deletes the
    /// journal after. Putting the files back is one routine (<see cref="RestoreBeforeImages"/>) for the three
    /// cases: a rollback, a commit that failed anywhere (undone at once), and a journal found at open. A commit
    /// or a rollback runs whole under the engine's lock, so no write of another thread lands in between.
    /// </summary>
    internal sealed class XodTransaction : IXodTransaction
    {
        private readonly Dictionary<string, byte[]> snapshots = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> newlyCreatedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly string rootDirectory;
        private readonly string walFilePath;
        private readonly Action<Action> exclusive;
        private readonly Action onCommit;
        private readonly Action<Action> onRollback;
        private readonly Action<string> onFaulted;
        private readonly Action dropIndexFiles;
        private readonly Action onDispose;
        private bool completed;
        // This commit's own journal: staged (the .tmp written) and in place. Only this commit's is ever deleted.
        private bool journalStaged;
        private bool journalWritten;
        // The files this commit changed on disk (IOService.FileChangedOnDisk, from pool threads too): a failed commit's
        // undo writes back only these. Recorded only while the commit writes.
        private readonly HashSet<string> changedByCommit = new(StringComparer.OrdinalIgnoreCase);
        private volatile bool committing;

        public Guid TransactionId { get; } = Guid.NewGuid();
        public bool IsActive => !completed;

        /// <param name="rootDirectory">The database folder.</param>
        /// <param name="exclusive">Runs a commit or a rollback, whole, under the engine's lock.</param>
        /// <param name="onCommit">Writes the transaction's files.</param>
        /// <param name="onRollback">Puts the context back as it was: called with the routine that restores the
        /// files, which it runs after ending the transaction's batch if it is still open, and before forgetting
        /// everything the context holds, pending writes included.</param>
        /// <param name="onFaulted">Called when a failed commit can't be undone: the context must write nothing
        /// more (the next open undoes the commit from the journal, and would undo those writes with it).</param>
        /// <param name="onDispose">Ends the transaction in the engine.</param>
        /// <param name="dropIndexFiles">Drops the folder's index files, after a commit undone once it had written
        /// them (they may hold its keys): the index service, the one that deletes them in a live context.</param>
        internal XodTransaction(string rootDirectory, Action<Action> exclusive, Action onCommit, Action<Action> onRollback, Action<string> onFaulted, Action onDispose,
            Action dropIndexFiles)
        {
            this.dropIndexFiles = dropIndexFiles;
            this.exclusive = exclusive ?? (a => a());
            this.rootDirectory = rootDirectory;
            this.walFilePath = !string.IsNullOrEmpty(rootDirectory)
                ? Path.Combine(rootDirectory, "transaction.xwal")
                : null;
            this.onCommit = onCommit;
            this.onRollback = onRollback;
            this.onFaulted = onFaulted;
            this.onDispose = onDispose;
        }

        internal void CaptureBeforeMutate(string filePath)
        {
            if (completed || string.IsNullOrEmpty(filePath))
                return;

            // Not an index file: a derived cache, never written while a transaction is open (ISS-100), which a commit
            // undone drops whole (dropIndexFiles; the open's marker step after a crash). The image a delete took (a
            // table's last row deleted removes its index) was written back by that undo, and onto one another
            // program held it failed, faulting the context (review of TASK-070, ISS-110).
            if (IOService.IsIndex(filePath))
                return;

            // Nor the database file: what a transaction writes to it is the AES256 data key, made for a value the
            // transaction holds, and the key must outlast the transaction (IOService.SaveNow). Kept in its image, a
            // rollback, a commit undone or the undo of a journal after a crash took the key back after another
            // context had written values under it: unreadable (review of ISS-150). With one context at a time
            // (PLAN-064) every value under the key is the transaction's own and goes with it, so this is no longer what
            // keeps a value readable; the key stays out because its one writer is IOService.SaveNow, outside every
            // transaction. Nothing else writes the database file once it is created.
            if (Helpers.DatabaseFiles.IsDatabaseFile(filePath))
                return;

            if (snapshots.ContainsKey(filePath) || newlyCreatedFiles.Contains(filePath))
                return;

            if (File.Exists(filePath))
            {
                snapshots[filePath] = File.ReadAllBytes(filePath);
            }
            else
            {
                newlyCreatedFiles.Add(filePath);
            }
        }

        public void Commit()
        {
            if (completed)
                throw new TransactionException("Transaction has already been completed.");

            exclusive(() =>
            {
                completed = true;
                try
                {
                    bool writing = false;
                    try
                    {
                        WriteWalJournal();
                        writing = true;
                        committing = true;
                        try
                        {
                            onCommit?.Invoke();
                        }
                        finally
                        {
                            committing = false;
                        }
                        DeleteWalJournal();
                    }
                    catch (Exception commitFailure)
                    {
                        // Wherever it failed (the journal, the files, the indexes, the journal's deletion), the
                        // commit is undone now, so that this context and the disk agree, and nothing it writes later
                        // is undone by the next open's recovery.
                        UndoFailedCommit(commitFailure, writing);
                    }
                }
                finally
                {
                    snapshots.Clear();
                    newlyCreatedFiles.Clear();
                    lock (changedByCommit)
                        changedByCommit.Clear();
                    onDispose?.Invoke();
                }
            });
        }

        /// <summary>
        /// A data file on disk has changed while this transaction's commit writes (IOService.FileChangedOnDisk). Its
        /// failed commit's undo writes back the files named here, and no other: one the commit never changed is as the
        /// transaction found it, and writing it back could only fail (another program holds it) or do nothing.
        /// </summary>
        internal void NoteFileChanged(string filePath)
        {
            if (!committing || string.IsNullOrEmpty(filePath))
                return;
            lock (changedByCommit)
                changedByCommit.Add(Path.GetFullPath(filePath));
        }

        /// <summary>
        /// A file the commit's writes didn't report (NoteFileChanged) that is still not as the transaction found it:
        /// missing, or other bytes. So a write path that ever fails to report, or a rename that reports a failure it
        /// didn't have, is still undone, not left as half a commit with its journal deleted. A file that can't be read
        /// and wasn't reported is left: its write failed before changing it (another program holds it), and writing it
        /// back could only fail too. Only the failed commit's undo asks; the open's recovery writes every before-image
        /// back.
        /// </summary>
        private static bool NotAsBefore(string filePath, byte[] before)
        {
            if (!File.Exists(filePath))
                return true;
            try
            {
                return !File.ReadAllBytes(filePath).AsSpan().SequenceEqual(before);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return false;
            }
        }

        // filesWritten: the commit got past its journal, so some of its files may be written. Before that nothing
        // was (a transaction writes no data file before its commit, ISS-166), and nothing is put back.
        private void UndoFailedCommit(Exception commitFailure, bool filesWritten)
        {
            try
            {
                // The indexes too: the commit wrote them after its journal, and they may hold its rows.
                onRollback?.Invoke(() =>
                {
                    if (!filesWritten)
                        return;
                    FaultInjection.Step("undo");
                    List<KeyValuePair<string, byte[]>> changed;
                    lock (changedByCommit)
                        changed = snapshots.Where(s => changedByCommit.Contains(Path.GetFullPath(s.Key)) || NotAsBefore(s.Key, s.Value)).ToList();
                    // Every file it created that is there: the commit made it (it wasn't there before the transaction).
                    RestoreBeforeImages(changed, newlyCreatedFiles);
                    dropIndexFiles?.Invoke();
                });
                DeleteWalJournal();
            }
            catch (Exception undoFailure)
            {
                string message = "The commit failed (" + commitFailure.Message + ") and couldn't be undone (" + undoFailure.Message +
                    "). The database is put back as it was before the transaction when it is next opened; until then this context refuses writes.";
                onFaulted?.Invoke(message);
                throw new TransactionException(message, new AggregateException(commitFailure, undoFailure));
            }

            ExceptionDispatchInfo.Capture(commitFailure).Throw();
        }

        private void WriteWalJournal()
        {
            if (string.IsNullOrEmpty(walFilePath) || (snapshots.Count == 0 && newlyCreatedFiles.Count == 0))
                return;

            var rootElm = new XElement("TransactionJournal",
                new XAttribute("id", TransactionId.ToString("N")),
                new XAttribute("timestampUtc", DateTime.UtcNow.ToString("O")));

            foreach (var kvp in snapshots)
            {
                rootElm.Add(new XElement("Snapshot",
                    new XAttribute("file", Path.GetFileName(kvp.Key)),
                    Convert.ToBase64String(kvp.Value)));
            }

            foreach (var created in newlyCreatedFiles)
            {
                rootElm.Add(new XElement("Created",
                    new XAttribute("file", Path.GetFileName(created))));
            }

            var doc = new XDocument(rootElm);
            string tmpWal = walFilePath + ".tmp";

            // The journal's name is the database's: one already there is a commit that didn't finish (this context's,
            // failed and not undone, which refuses writes until the next open undoes it). Written over, or deleted after
            // this commit, it would be lost, and with it the only way to undo that commit.
            const string JournalThere =
                "A commit that didn't finish left its journal in the database folder; it is undone when the database is next opened. This commit is refused.";
            if (File.Exists(walFilePath) || File.Exists(tmpWal))
                throw new TransactionException(JournalThere);

            FaultInjection.Step("journal-writing");
            try
            {
                // Written whole under its staging name, then moved into place: after a process crash a journal at its
                // name is whole. Not after a power cut: like the pages, it isn't flushed to the device (flushToDisk: false).
                using (var stream = new FileStream(tmpWal, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    journalStaged = true;
                    doc.Save(stream);
                    stream.Flush(flushToDisk: false);
                }
                FaultInjection.Step("journal-staged");
                File.Move(tmpWal, walFilePath, overwrite: false);
            }
            catch (IOException e) when (!journalStaged || File.Exists(walFilePath))
            {
                // A journal appeared between the check and here: not this commit's to write over.
                throw new TransactionException(JournalThere, e);
            }
            journalStaged = false;
            journalWritten = true;
        }

        // This commit's journal, staged or in place: never another's.
        private void DeleteWalJournal()
        {
            if (journalStaged)
            {
                File.Delete(walFilePath + ".tmp");
                journalStaged = false;
            }
            if (journalWritten)
            {
                File.Delete(walFilePath);
                journalWritten = false;
            }
        }

        /// <summary>
        /// Puts files back as they were before a transaction: deletes the files it created and writes each
        /// snapshot back (atomically, through <c>.tmp</c>). Idempotent, so a recovery interrupted part-way can run
        /// again. Not the index files, which a commit undone after it wrote them may have left holding its keys:
        /// its context drops them through its index service, and the open's recovery through the marker step
        /// (ISS-110: deleted here, one another program held failed the undo).
        /// <para>
        /// It writes pages without the mutation hook that marks the indexes behind (ISS-043, IOService.IndexesBehind),
        /// and needs none. In a failed commit's undo the context's marker is already set (the transaction's first page change set
        /// it, and nothing empties it while the transaction's batch is open: XmlEngine.FlushToDisk) and stays until a
        /// flush after the undo, while the index files go back with the pages. At the open, recovery runs alone,
        /// before any index is loaded, and drops the indexes itself.
        /// </para>
        /// </summary>
        private static void RestoreBeforeImages(IEnumerable<KeyValuePair<string, byte[]>> before, IEnumerable<string> created)
        {
            foreach (string file in created)
            {
                if (File.Exists(file))
                    File.Delete(file);
            }

            int restoring = 0;
            foreach (var snapshot in before)
            {
                FaultInjection.Step("recover-restoring", ++restoring);
                string tmp = snapshot.Key + ".tmp";
                File.WriteAllBytes(tmp, snapshot.Value);
                File.Move(tmp, snapshot.Key, overwrite: true);
            }
        }

        /// <summary>
        /// Run when the database opens: a journal means a commit that didn't finish, and it is undone. A journal
        /// that doesn't parse was cut short before its commit wrote any file (it is written whole, then renamed
        /// into place), and is deleted. One that can't be undone (a file open elsewhere) stops the open and
        /// stays for the next: opening a commit half done would let new writes in that the journal would later
        /// undo.
        /// </summary>
        internal static void RecoverWalJournalIfPresent(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory) || !Directory.Exists(rootDirectory))
                return;

            foreach (var walFile in Directory.GetFiles(rootDirectory, "*.xwal"))
            {
                XElement journalRoot;
                try
                {
                    journalRoot = XDocument.Load(walFile).Root;
                }
                catch (System.Xml.XmlException)
                {
                    File.Delete(walFile);
                    continue;
                }

                if (journalRoot == null || journalRoot.Name.LocalName != "TransactionJournal")
                {
                    File.Delete(walFile);
                    continue;
                }

                try
                {
                    var created = journalRoot.Elements("Created").Select(e => InFolder(rootDirectory, e.Attribute("file")?.Value)).ToList();
                    var before = journalRoot.Elements("Snapshot")
                        .Select(e => new KeyValuePair<string, byte[]>(InFolder(rootDirectory, e.Attribute("file")?.Value), Convert.FromBase64String(e.Value)))
                        .ToList();
                    // First the mark, so no index file is trusted from here until the marker step, next in this
                    // recovery, has dropped them all (IOService.RecoverInterruptedWrites); one it can't drop stays
                    // untrusted with the marker.
                    IOService.LeaveIndexesBehind(walFile);
                    RestoreBeforeImages(before, created);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException || e is InvalidDataException)
                {
                    throw new XodException("A commit that didn't finish can't be undone (" + e.Message +
                        "). The database opens once it can be; the journal " + Path.GetFileName(walFile) + " is kept.", e);
                }

                File.Delete(walFile);
            }
        }

        // A journal names files of its own folder only.
        private static string InFolder(string root, string fileName)
        {
            if (string.IsNullOrEmpty(fileName) || Path.GetFileName(fileName) != fileName)
                throw new InvalidDataException("The journal names a file outside the database folder: '" + fileName + "'.");
            return Path.Combine(root, fileName);
        }

        public void Rollback()
        {
            if (completed)
                throw new TransactionException("Transaction has already been completed.");

            exclusive(() =>
            {
                completed = true;
                try
                {
                    // Nothing to put back on disk: the transaction wrote no data file (IOService.RefuseWriteInsideTransaction)
                    // and deleted none (its deletes wait for the commit, ISS-166); onRollback forgets them with its batch.
                    onRollback?.Invoke(() => { });
                }
                finally
                {
                    snapshots.Clear();
                    newlyCreatedFiles.Clear();
                    onDispose?.Invoke();
                }
            });
        }

        public void Dispose()
        {
            if (!completed)
            {
                Rollback();
            }
        }
    }
}
