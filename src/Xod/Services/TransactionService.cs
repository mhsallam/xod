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
        // This commit holds its journal open while it runs (WriteWalJournal, ISS-147).
        private FileStream journalHold;

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
                        onCommit?.Invoke();
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
                    ReleaseJournalHold();
                    snapshots.Clear();
                    newlyCreatedFiles.Clear();
                    onDispose?.Invoke();
                }
            });
        }

        // filesWritten: the commit got past its journal, so some of its files may be written. Before that nothing
        // was, and nothing is put back: another context may have written those files since this one read them.
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
                    RestoreBeforeImages(snapshots, newlyCreatedFiles);
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

            // The journal's name is the database's: one already there is another commit's, under way in another
            // context, or one that didn't finish and waits for an open that has the database to itself. Written
            // over, or deleted after this commit, it would be lost, and with it the only way to undo that commit.
            const string JournalThere =
                "Another commit's journal is in the database folder (one under way in another context, or one that didn't finish and is undone when the database is next opened by a context that has it to itself); this commit is refused.";
            if (File.Exists(walFilePath) || File.Exists(tmpWal))
                throw new TransactionException(JournalThere);

            FaultInjection.Step("journal-writing");
            try
            {
                // Held open from here until the journal is deleted (DeleteWalJournal), or this commit has failed and
                // can't be undone (ReleaseJournalHold): a journal its context holds is a commit under way; one nobody
                // holds won't finish (JournalIn, ISS-147). Shared for reading and deletion, so the move below and the
                // deletion work under the hold (on Windows they need FileShare.Delete; on Unix the hold is flock
                // LOCK_SH, which follows the file through the rename). A probe that shares nothing is refused while it
                // is held: Windows for the sharing, Unix for the lock (.NET takes LOCK_EX for FileShare.None,
                // SafeFileHandle.Unix.cs), as for the lock files (IOService.OpenOneLockFile). Not on a Unix network or
                // unrecognised file system: .NET takes no LOCK_SH for a file opened for writing there (CanLockTheFile:
                // nfs, smb, smb2, cifs, or a type it can't read, release/9.0), so this hold isn't seen and the commit
                // reads as one that won't finish: writes beside it are refused, not let in.
                journalHold = new FileStream(tmpWal, FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
                journalStaged = true;
                doc.Save(journalHold);
                journalHold.Flush(flushToDisk: false);
                FaultInjection.Step("journal-staged");
                File.Move(tmpWal, walFilePath, overwrite: false);
            }
            catch (IOException e) when (!journalStaged || File.Exists(walFilePath))
            {
                // Another commit's staged or placed journal appeared between the check and here.
                throw new TransactionException(JournalThere, e);
            }
            journalStaged = false;
            journalWritten = true;
        }

        // This commit's journal, staged or in place: never another's. Deleted under the hold, which goes after it,
        // so no other context finds it there and unheld (ISS-147).
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
            ReleaseJournalHold();
        }

        // The journal's hold goes: after its deletion, or with the commit over and the journal kept because it
        // couldn't be undone. Kept, it now reads as one that won't finish, and every context refuses to write beside
        // it until an open that has the database to itself undoes it (JournalIn).
        private void ReleaseJournalHold()
        {
            journalHold?.Dispose();
            journalHold = null;
        }

        /// <summary>This context's commit holds its journal: the one journal its own writes may go beside.</summary>
        internal bool HoldsJournal => journalHold != null;

        internal enum JournalState
        {
            /// <summary>No journal in the folder.</summary>
            None,
            /// <summary>A commit under way: its context holds the journal.</summary>
            UnderWay,
            /// <summary>A commit that won't finish (cut short by a crash, or failed and not undone): nobody holds its
            /// journal, which the next open that has the database to itself undoes.</summary>
            WontFinish,
        }

        /// <summary>
        /// Whether the folder holds a commit's journal, and whether that commit is under way (ISS-147). Whatever is
        /// written beside a journal that is later undone is undone with it, after its context was told it was made:
        /// the files the journal names go back to their images from before that commit. A journal nobody holds is
        /// undone at the next open that has the database to itself. A commit is told apart from a crash by the hold
        /// (WriteWalJournal): a probe that shares nothing is refused while it is held. Where the hold can't be seen
        /// (file locking switched off on Unix, a Unix network file system, or a probe that fails for another reason),
        /// the journal is read as one that won't finish, the reading that writes nothing: a commit under way is then
        /// refused beside rather than waited for. The reading is a guess where the probe fails for a reason other than
        /// the sharing: a journal this user can't open reads as under way until the wait runs out, so the refusal's
        /// message can name the wrong cause; what is refused is right either way.
        /// </summary>
        internal static JournalState JournalIn(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory))
                return JournalState.None;
            string journal = Path.Combine(rootDirectory, "transaction.xwal");
            if (!File.Exists(journal))
                return JournalState.None;
            if (IOService.FileLockingSwitchedOff)
                return JournalState.WontFinish;
            try
            {
                using (new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                return JournalState.WontFinish;
            }
            catch (FileNotFoundException)
            {
                return JournalState.None; // its commit ended meanwhile
            }
            catch (IOException e) when (IOService.HeldByAnother(e))
            {
                return JournalState.UnderWay;
            }
            catch (UnauthorizedAccessException)
            {
                // Windows refuses an open of a file whose deletion is pending (a commit that has just ended, under its
                // hold): read as under way, and asked again.
                return File.Exists(journal) ? JournalState.UnderWay : JournalState.None;
            }
            catch (IOException)
            {
                return File.Exists(journal) ? JournalState.WontFinish : JournalState.None;
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
        /// and needs none. In a rollback the context's marker is already set (the transaction's first page write set
        /// it, and nothing empties it while the transaction's batch is open: XmlEngine.FlushToDisk) and stays until a
        /// flush after the rollback, while the index files go back with the pages. At the open, recovery runs alone,
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
                    onRollback?.Invoke(() => RestoreBeforeImages(snapshots, newlyCreatedFiles));
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
