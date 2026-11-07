using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using Xod.Infra;

namespace Xod.Services
{
    internal sealed class XodTransaction : IXodTransaction
    {
        private readonly Dictionary<string, byte[]> snapshots = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> newlyCreatedFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly string walFilePath;
        private readonly Action onCommit;
        private readonly Action onRollback;
        private readonly Action onDispose;
        private bool completed;

        public Guid TransactionId { get; } = Guid.NewGuid();
        public bool IsActive => !completed;

        internal XodTransaction(string rootDirectory, Action onCommit, Action onRollback, Action onDispose)
        {
            this.walFilePath = !string.IsNullOrEmpty(rootDirectory)
                ? Path.Combine(rootDirectory, "transaction.xwal")
                : null;
            this.onCommit = onCommit;
            this.onRollback = onRollback;
            this.onDispose = onDispose;
        }

        internal void CaptureBeforeMutate(string filePath)
        {
            if (completed || string.IsNullOrEmpty(filePath))
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

            completed = true;
            try
            {
                WriteWalJournal();
                onCommit?.Invoke();
                DeleteWalJournal();
            }
            finally
            {
                snapshots.Clear();
                newlyCreatedFiles.Clear();
                onDispose?.Invoke();
            }
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
            using (var fs = new FileStream(tmpWal, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                doc.Save(fs);
                fs.Flush(flushToDisk: false);
            }
            File.Move(tmpWal, walFilePath, overwrite: true);
        }

        private void DeleteWalJournal()
        {
            if (!string.IsNullOrEmpty(walFilePath) && File.Exists(walFilePath))
            {
                File.Delete(walFilePath);
            }
        }

        internal static void RecoverWalJournalIfPresent(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory) || !Directory.Exists(rootDirectory))
                return;

            foreach (var walFile in Directory.GetFiles(rootDirectory, "*.xwal"))
            {
                try
                {
                    var doc = XDocument.Load(walFile);
                    var journalRoot = doc.Root;
                    if (journalRoot != null && journalRoot.Name.LocalName == "TransactionJournal")
                    {
                        foreach (var createdElm in journalRoot.Elements("Created"))
                        {
                            string fileName = createdElm.Attribute("file")?.Value;
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                string target = Path.Combine(rootDirectory, fileName);
                                if (File.Exists(target))
                                    File.Delete(target);
                            }
                        }

                        foreach (var snapElm in journalRoot.Elements("Snapshot"))
                        {
                            string fileName = snapElm.Attribute("file")?.Value;
                            if (!string.IsNullOrEmpty(fileName))
                            {
                                byte[] originalBytes = Convert.FromBase64String(snapElm.Value);
                                string target = Path.Combine(rootDirectory, fileName);
                                string tmpTarget = target + ".tmp";
                                File.WriteAllBytes(tmpTarget, originalBytes);
                                File.Move(tmpTarget, target, overwrite: true);
                            }
                        }

                        // Remove .xidx files so IndexService rebuilds indexes cleanly from the restored .xpag pages
                        foreach (var idxFile in Directory.GetFiles(rootDirectory, "*.xidx"))
                        {
                            File.Delete(idxFile);
                        }
                    }

                    File.Delete(walFile);
                }
                catch
                {
                    // If the .xwal file itself was truncated/corrupted before commit started, remove it
                    try { File.Delete(walFile); } catch { }
                }
            }
        }

        public void Rollback()
        {
            if (completed)
                throw new TransactionException("Transaction has already been completed.");

            completed = true;

            try
            {
                onRollback?.Invoke();

                foreach (var created in newlyCreatedFiles)
                {
                    if (File.Exists(created))
                        File.Delete(created);
                }

                foreach (var kvp in snapshots)
                {
                    File.WriteAllBytes(kvp.Key, kvp.Value);
                }
            }
            finally
            {
                snapshots.Clear();
                newlyCreatedFiles.Clear();
                onDispose?.Invoke();
            }
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
