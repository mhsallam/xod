using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using Xod.Engines.Serializers;
using Xod.Helpers;
using Xod.Infra;
using Xod.Services;

namespace Xod.Engines.Xml
{
    public partial class XmlEngine
    {
        // The rows the statement under way has created, so that a statement that fails removes them (ISS-062): a
        // row whose second child is refused leaves no first child behind. The outermost Create or UpdateItem of a
        // statement keeps the list, inside one of EnterTableWriteLock's locks. Each thread keeps its own, as it keeps
        // what its statement saved (StatementSaves): statements on standalone tables (no relations) run side by side,
        // one per table, under the engine's read lock and their table's write lock; every other statement runs
        // alone, under the engine-wide write lock. What isn't per thread: IOService's batch, which a standalone
        // Delete or InsertRange still opens (ISS-124), and the indexes' set of types to save. A standalone table's
        // row creates nothing else, but its own row is taken back too when its write fails at the statement's end
        // (ISS-120: it stayed in the cache and the index, and the next flush wrote the row its caller was told had
        // failed).
        //
        // A mitigation, not all or nothing (ISS-090): it removes created rows only, while the process runs. What a
        // failed statement changed in rows that were there before (an update's replaced row, children it dropped)
        // isn't put back (ISS-089), and a crash leaves the created rows (they were written as they were created).
        // What the statement under way has done that its undo takes back (null outside a statement): the rows it
        // created, each noted once its path is chosen and before anything is written for it, and the entries it
        // added to existing parents' lists (a child inserted on its own), noted as they are added. Noting before the
        // write means a row or entry the statement got part way through is taken back too (review of TASK-063: a
        // parent kept listing a child whose insert failed, and a row whose own page write failed stayed in the
        // cached page, to be saved by the next write).
        private sealed class StatementLog
        {
            internal readonly List<(Type Type, string Code)> Created = new();
            internal readonly List<(Type ParentType, string ParentCode, Type ChildType, string ChildCode)> Linked = new();
        }

        private readonly ThreadLocal<StatementLog> statementLogs = new ThreadLocal<StatementLog>();

        private StatementLog statementLog
        {
            get => statementLogs.Value;
            set => statementLogs.Value = value;
        }

        private bool BeginStatement()
        {
            if (statementLog != null || (globalWriteDepth == 0 && standaloneWriteDepth.Value == 0))
                return false;
            statementLog = new StatementLog();
            return true;
        }

        private void EndStatement(bool owner)
        {
            if (owner)
                statementLog = null;
        }

        /// <summary>
        /// Removes the rows a failed statement created, newest first, through the one delete path without its
        /// cascade: every row the statement created is in the log (noted before it was written, so one it got part
        /// way through is there too, and one never written is no row to delete), and a cascade could reach rows it
        /// didn't. A row's removal also takes it out of a parent that lists it (ISS-088), and every entry the
        /// statement added to an existing parent's list is taken out, whether or not its row was written. The
        /// counters stay where the
        /// statement left them, as after a rollback (ISS-083): the caller's items hold the numbers it handed out.
        /// The failure is what the caller sees. If the removal fails too, the caller gets an XodException carrying
        /// both, the indexes of the tables the statement wrote are dropped (in memory and on disk: a derived cache,
        /// rebuilt from the pages, PLAN-046 section 8), since they may have dropped rows the pages still hold, and
        /// the context writes nothing more until it is reopened (ISS-091). Not across a crash (see above).
        /// </summary>
        private void UndoStatement(Exception failure)
        {
            var log = statementLog;
            statementLog = null;
            try
            {
                TakeStatementBack(failure, log);
            }
            finally
            {
                // What the statement and its undo saved and didn't write waits for the next flush, not for the
                // statement's end, where a write failing again would replace the failure the caller should see.
                ioService.LeaveSavedToFlush();
            }
        }

        private void TakeStatementBack(Exception failure, StatementLog log)
        {
            if (log == null || (log.Created.Count == 0 && log.Linked.Count == 0))
                return;
            var created = log.Created;

            autonumberMarksPending = true;
            Exception undoFailure = null;
            try
            {
                for (int i = created.Count - 1; i >= 0; i--)
                {
                    DeleteByItemCode(created[i].Type, created[i].Code, isCascade: false, cascadeChildren: false);
                    FaultInjection.Step("undo-statement", created.Count - i);
                }
                for (int i = log.Linked.Count - 1; i >= 0; i--)
                {
                    var link = log.Linked[i];
                    RemoveChildEntry(RowByItemCode(link.ParentType, link.ParentCode), link.ParentType, link.ChildType, link.ChildCode);
                }
            }
            catch (Exception e)
            {
                undoFailure = e;
            }
            // Written as any statement's end writes, with no batch: a batch is the context's, and statements on
            // standalone tables run side by side, so it would take in another thread's writes (ISS-124). This thread's
            // saves, then the indexes of the tables it took rows from. Not in a transaction or buffered mode, which
            // write later. In a context closed meanwhile (ISS-103) every write is refused (ThrowIfWritesRefused), and
            // the caller sees ObjectDisposedException.
            if (undoFailure == null && !ioService.IsBatchOrBuffered)
            {
                try
                {
                    ioService.WriteSaved();
                    indexService.SaveAfterRowsWritten(created.Select(c => c.Type).Concat(log.Linked.Select(l => l.ParentType)).Distinct().ToList());
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // The rows are taken back in the cache and the index; only writing that failed (the statement's
                    // own write often failed the same way: a page another program holds). It stays to be made, by the
                    // next statement that saves it or the next flush, as any write that fails does (ISS-098), and the
                    // indexes stay marked behind until then. Not a reason to refuse writes, which is for a cache that
                    // may not be what the disk should hold (ISS-120: an undo that met the held page faulted the
                    // context). The caller sees the statement's failure.
                }
            }
            if (undoFailure != null)
            {
                var failures = new List<Exception> { failure, undoFailure };
                foreach (var type in created.Select(c => c.Type).Distinct())
                {
                    // Each one tried; one that fails is reported with the rest, and the refusal below stands.
                    try
                    {
                        indexService.Remove(type);
                    }
                    catch (Exception indexFailure)
                    {
                        failures.Add(indexFailure);
                    }
                }
                // After the index removals: each is a file write, which this refuses.
                statementFault = "A statement failed, and removing the rows it had created failed too (" + undoFailure.Message +
                    "): what this context holds may not be what the disk holds, so it writes nothing more. Reopen the database.";
                throw new XodException("The statement failed, and the rows it had created couldn't all be removed: the database may " +
                    "hold some of them. Reopen it to go on writing.", new AggregateException(failures));
            }
        }

        private string Create(Type type, object item, bool shallowWrite = false, ReadWriteTrack writeTrack = null)
        {
            bool statement = BeginStatement();
            try
            {
                string code = CreateRow(type, item, shallowWrite, writeTrack);
                // What the statement saved is written inside its undo: a write that fails takes its rows back, as a
                // failure in its middle does (ISS-062, ISS-099).
                if (statement)
                    ioService.WriteSaved();
                return code;
            }
            catch (Exception failure) when (statement)
            {
                UndoStatement(failure);
                throw;
            }
            finally
            {
                EndStatement(statement);
            }
        }

        private string CreateRow(Type type, object item, bool shallowWrite, ReadWriteTrack writeTrack)
        {
            if (item == null)
                exceptionService.Throw(new ArgumentNullException(nameof(item)));

            if (type == typeof(object))
                type = item.GetActualType();

            propertyService.LoadType(type);
            EnsureIndexHydrated(type);

            TriggerEventArgs trigger = null;
            if (BeforeAction != null || AfterAction != null)
            {
                trigger = new TriggerEventArgs
                {
                    Item = item,
                    Type = type,
                    Action = DatabaseActions.Insert
                };

                BeforeAction?.Invoke(this, trigger);
                if (trigger.Cancel)
                    return null;
            }

            // Validate primary key types
            ValidatePrimaryKeyTypes(type);

            // Validate required properties before allocating autonumber sequence values
            ValidateRequiredProperties(type, item);

            // Set auto number values
            Dictionary<string, dynamic> autonumbers = autonumberService.Autonumber(type, item);

            // Enforce PrimaryKey and UniqueKey constraints
            long tUnique = WriteDiagnostics.Begin();
            ValidateUniqueAndPrimaryConstraints(type, item, existingCodeToIgnore: null);
            WriteDiagnostics.End(ref WriteDiagnostics.UniqueTicks, tUnique);

            string refCode = AddToPage(type, item, autonumbers, shallowWrite, writeTrack);

            if (trigger != null)
                AfterAction?.Invoke(this, trigger);
            return refCode;
        }

        private string AddToPage(Type type, object item, Dictionary<string, dynamic> autonumbers = null, bool shallowWrite = false, ReadWriteTrack writeTrack = null)
        {
            string itemCode = null;
            string itemPath = null;

            string typeName = type.FullName;
            if (typeName == "System.Object")
                typeName = item.GetActualType().FullName;

            string tableFileName = $"{typeName}.xtab";

            long tOpenTable = WriteDiagnostics.Begin();
            XFile tableFile = ioService.OpenFileOrCreate(tableFileName, typeof(Table));
            WriteDiagnostics.End(ref WriteDiagnostics.OpenTableTicks, tOpenTable);
            if (tableFile != null)
            {
                var meta = propertyService.GetMetadata(type);
                if (meta != null && meta.IsPureScalarStandalone && !shallowWrite && writeTrack == null)
                {
                    bool inTx = activeTransaction != null && activeTransaction.IsActive;
                    long tRaw = WriteDiagnostics.Begin();
                    bool rawOk = ioService.TryAppendRawRowToPage(tableFile, item, meta, exceptionService, pageSize, inTx, out itemPath, out XElement rawPageElement, out bool rawTableModified);
                    WriteDiagnostics.End(ref WriteDiagnostics.RawTicks, tRaw);
                    if (rawOk)
                    {
                        statementLog?.Created.Add((type, itemPath));
                        indexService.IndexItem(type, item, itemPath);

                        if (rawTableModified)
                        {
                            long tSave = WriteDiagnostics.Begin();
                            ioService.Save(tableFile);
                            WriteDiagnostics.End(ref WriteDiagnostics.SaveTicks, tSave);
                            if (!ioService.IsBatchOrBuffered)
                                indexService.SaveToDisk(type);
                        }
                        return itemPath;
                    }
                }

                // The row is built before the table changes: a row that fails to build (a refused
                // value, a child that breaks a constraint) must leave no page registered that nothing
                // would save (ISS-061). The row's path names its page, so the page is chosen first and
                // registered only once the row exists, as the fast row writer does.
                string pageFileName = ioService.ChooseAppendPage(tableFile, out XElement tablePageElement);
                {
                    string pageCode = System.IO.Path.GetFileNameWithoutExtension(pageFileName);
                    long tPick = WriteDiagnostics.Begin();
                    itemCode = ValueHelper.PickCode();
                    WriteDiagnostics.End(ref WriteDiagnostics.PickCodeTicks, tPick);
                    itemPath = $"{pageCode}.{itemCode}";
                    // Noted before anything is written for it: its children, an existing parent's list, its page.
                    statementLog?.Created.Add((type, itemPath));

                    var itemCreateTrack = new ReadWriteTrack
                    {
                        Item = item,
                        Type = type,
                        Code = itemPath,
                        Parent = writeTrack
                    };

                    long tWrite = WriteDiagnostics.Begin();
                    XElement xItem = Write(type, item, shallowWrite, itemCreateTrack);
                    WriteDiagnostics.End(ref WriteDiagnostics.WriteTicks, tWrite);
                    WriteDiagnostics.EndWrite();
                    WriteDiagnostics.Entity();
                    bool tableModified = false;
                    if (xItem != null)
                    {
                        long tOpenPage = WriteDiagnostics.Begin();
                        if (tablePageElement == null)
                        {
                            tablePageElement = ioService.AddPage(tableFile, pageFileName);
                            tableModified = true;
                        }
                        XFile pageFile = ioService.OpenFileOrCreate(pageFileName, typeof(TablePage));
                        WriteDiagnostics.End(ref WriteDiagnostics.OpenPageTicks, tOpenPage);

                        long tAddRow = WriteDiagnostics.Begin();
                        var re = new XElement("Row", new XAttribute("code", itemCode), xItem);
                        pageFile.AddRow(itemCode, re);
                        Services.FaultInjection.RowWritten();
                        WriteDiagnostics.End(ref WriteDiagnostics.AddRowTicks, tAddRow);

                        if (tablePageElement != null)
                        {
                            // A hint, not a write to make of the table file (IOService.ChangeHint).
                            ioService.ChangeHint(tableFile, () =>
                            {
                                XAttribute countAttr = tablePageElement.Attribute("count");
                                if (countAttr != null && int.TryParse(countAttr.Value, out int currentCount))
                                    countAttr.Value = (currentCount + 1).ToString();
                                else
                                    tablePageElement.SetAttributeValue("count", pageFile.Rows().Count());
                            });

                            if (ioService.IsBatchOrBuffered)
                                tableModified = true;
                        }

                        long tIndex = WriteDiagnostics.Begin();
                        indexService.IndexItem(type, item, itemPath);
                        WriteDiagnostics.End(ref WriteDiagnostics.IndexTicks, tIndex);

                        long tPageSave = WriteDiagnostics.Begin();
                        ioService.Save(pageFile);
                        WriteDiagnostics.End(ref WriteDiagnostics.PageSaveTicks, tPageSave);
                        WriteDiagnostics.PageSaved();

                        long currentPageSize = pageFile.LastSavedSize > 0 ? pageFile.LastSavedSize : ioService.Size(pageFile.Path);
                        if (currentPageSize >= pageSize && tablePageElement != null)
                        {
                            tablePageElement.SetAttributeValue("full", "true");
                            tableModified = true;
                            if (activeTransaction == null)
                            {
                                ioService.FlushFullPageInBatch(pageFile);
                            }
                        }
                    }

                    if (tableModified)
                    {
                        long tTableSave = WriteDiagnostics.Begin();
                        ioService.Save(tableFile);
                        WriteDiagnostics.End(ref WriteDiagnostics.TableSaveTicks, tTableSave);
                        WriteDiagnostics.TableSaved();
                        if (!ioService.IsBatchOrBuffered)
                            indexService.SaveToDisk(type);
                    }
                }
            }
            return itemPath;
        }

        private XElement Write(Type type, object item, bool shallowWrite = false, ReadWriteTrack writeTrack = null, UpdateFilter filter = null)
        {
            if (item == null)
                return null;

            if (type == typeof(object))
                type = item.GetActualType();

            var meta = propertyService.GetMetadata(type);
            var xItem = new XElement(type.Name);

            PropertyInfoItem[] props = meta != null
                ? meta.WriteProperties
                : propertyService.Properties(type.FullName)
                    .Where(s => !s.IsNotMapped)
                    .OrderByDescending(s => s.PropertyType != null && ValueHelper.IsScalarType(s.PropertyType))
                    .ToArray();

            if (filter?.Properties != null)
            {
                IEnumerable<PropertyInfoItem> filterProps = filter.Behavior == UpdateFilterBehavior.Skip
                    ? props.Where(s => !filter.Properties.Contains(s.PropertyName))
                    : props.Where(s => filter.Properties.Contains(s.PropertyName));

                foreach (var prop in filterProps)
                    WriteProperty(type, item, xItem, prop, shallowWrite, writeTrack);
            }
            else
            {
                for (int i = 0; i < props.Length; i++)
                    WriteProperty(type, item, xItem, props[i], shallowWrite, writeTrack);
            }

            return xItem;
        }

        private XElement WriteProperty(Type type, object item, XElement xItem, PropertyInfoItem prop, bool shallowWrite = false, ReadWriteTrack writeTrack = null)
        {
            object value = prop.GetValue(item);
            if (prop.IsRequired && IsMissingRequiredValue(value))
            {
                exceptionService.Throw(new RequiredPropertyException(prop.PropertyName));
            }

            XElement propertyElement = null;
            Type effectivePropType = prop.PropertyType;

            if (prop.IsGenericType && value != null)
            {
                effectivePropType = value.GetActualType();
                propertyService.LoadType(effectivePropType);
            }

            // Scalar / value types
            if (ValueHelper.IsScalarType(effectivePropType))
            {
                if (StoredValue.IsLeftOut(value, effectivePropType, prop))
                    return null;

                // A null a read wouldn't give back (a new instance holds something else): marked (ISS-060).
                if (value == null)
                {
                    propertyElement = new XElement(prop.PropertyName, new XAttribute(StoredValue.NilAttribute, "true"));
                    xItem.Add(propertyElement);
                    return propertyElement;
                }

                string formattedValue = StoredValue.Text(value, prop.PropertyName);

                // [Crypto] applies to a string wherever it is stored, element or attribute (ISS-064).
                if (effectivePropType == typeof(string))
                {
                    formattedValue = prop.Encryption switch
                    {
                        CryptoMethod.MD5 or CryptoMethod.SHA1 or CryptoMethod.SHA256 or CryptoMethod.SHA512 =>
                            CryptoHelper.HashProperty(formattedValue, prop.Encryption),
                        CryptoMethod.AES256 => propertyKeys.Encrypt(formattedValue, prop.PropertyName),
                        _ => formattedValue
                    };
                }

                if (prop.ValuePosition == ValuePosition.Attribute)
                {
                    xItem.SetAttributeValue(prop.PropertyName, formattedValue);
                }
                else if (effectivePropType == typeof(string))
                {
                    propertyElement = new XElement(prop.PropertyName, XmlText.TextNode(formattedValue, preferCData: prop.IsMarkup));
                    xItem.Add(propertyElement);
                }
                else
                {
                    propertyElement = new XElement(prop.PropertyName, formattedValue);
                    xItem.Add(propertyElement);
                }
            }
            // Dictionary<TKey, TValue> support. A map reads back as written (ISS-097): an empty one is stored, as an
            // empty list is (left out, it read back null), and a null value is marked (StoredValue.ItemContent).
            else if (prop.TypeCategory == PropertyTypeCategory.Dictionary)
            {
                if (value is IDictionary dict)
                {
                    propertyElement = new XElement(prop.PropertyName,
                        new XAttribute("collType", "dictionary"));

                    foreach (DictionaryEntry entry in dict)
                    {
                        if (entry.Key != null)
                        {
                            propertyElement.Add(new XElement("Entry",
                                new XAttribute("key", StoredValue.Text(entry.Key, prop.PropertyName)),
                                StoredValue.ItemContent(entry.Value, prop.PropertyName)));
                        }
                    }
                    xItem.Add(propertyElement);
                }
            }
            // Array and generic collection values
            else if ((effectivePropType.IsArray && effectivePropType.GetArrayRank() == 1) ||
                     (effectivePropType.IsGenericType && typeof(IEnumerable).IsAssignableFrom(effectivePropType)))
            {
                if (value == null)
                    return null;

                Type itmType;
                string collType;
                if (effectivePropType.IsArray)
                {
                    itmType = effectivePropType.GetElementType();
                    collType = "array";
                }
                else
                {
                    itmType = effectivePropType.GetGenericArguments().FirstOrDefault();
                    collType = "generic";
                }

                if (itmType == null)
                    return null;

                propertyElement = new XElement(prop.PropertyName,
                    new XAttribute("dataType", itmType.FullName),
                    new XAttribute("collType", collType));

                if (ValueHelper.IsScalarType(itmType))
                {
                    // Every item, a null one marked (ISS-097: it was left out), named as the readers look for it.
                    if (value is IEnumerable items)
                    {
                        string itemName = StoredValue.ItemName(itmType);
                        foreach (var itm in items)
                            propertyElement.Add(new XElement(itemName, StoredValue.ItemContent(itm, prop.PropertyName)));
                    }
                }
                else if (!shallowWrite)
                {
                    var children = (value as IEnumerable)?.Cast<object>();
                    if (children != null)
                        ParseChildren(type, item, itmType, children, prop, propertyElement, shallowWrite, writeTrack);
                }

                xItem.Add(propertyElement);
            }
            // Reference type values
            else if (!shallowWrite && effectivePropType.IsClass)
            {
                ParseReference(type, item, value, prop, effectivePropType, xItem, writeTrack);
            }

            return propertyElement;
        }

        private bool UpdateItem(Type type, object item, object newItem, ReadWriteTrack track = null, UpdateFilter filter = null)
        {
            bool statement = BeginStatement();
            try
            {
                bool updated = UpdateRows(type, item, newItem, track, filter);
                if (statement)
                    ioService.WriteSaved();
                return updated;
            }
            catch (Exception failure) when (statement)
            {
                UndoStatement(failure);
                throw;
            }
            finally
            {
                EndStatement(statement);
            }
        }

        private bool UpdateRows(Type type, object item, object newItem, ReadWriteTrack track, UpdateFilter filter)
        {
            propertyService.LoadType(type);
            EnsureIndexHydrated(type);

            if (item == null || newItem == null)
                exceptionService.Throw(new ArgumentNullException());

            if (track?.Parent != null && RecursionError(track, track.Parent))
                return false;

            var trigger = new TriggerEventArgs
            {
                Item = newItem,
                Type = type,
                Action = DatabaseActions.Update
            };

            BeforeAction?.Invoke(this, trigger);
            if (trigger.Cancel)
                return false;

            bool updated = false;
            var meta = propertyService.GetMetadata(type);

            if (meta != null &&
                meta.IsStandalone &&
                track == null &&
                options.EnableIndexing &&
                meta.PrimaryKeyProperties.Length > 0 &&
                TryLookupCodesByProperties(type, item, meta.PrimaryKeyProperties, out var fastPkCodes, out bool fastPkCovered) &&
                fastPkCovered)
            {
                if (fastPkCodes.Count == 0)
                    return false;

                foreach (var itemCode in fastPkCodes)
                {
                    int dot = itemCode.IndexOf('.');
                    if (dot <= 0 || dot >= itemCode.Length - 1)
                        continue;

                    string pageCode = itemCode.Substring(0, dot);
                    string pageFileName = pageCode + ".xpag";
                    string rowCode = itemCode.Substring(dot + 1);

                    if (ioService.TryGetRowFromRawPage(pageFileName, rowCode, out XElement rawRow, out int startOffset, out int byteLen))
                    {
                        object rowItem = ItemForRow(type, rawRow, newItem, filter);
                        ValidateUniqueAndPrimaryConstraints(type, rowItem, existingCodeToIgnore: itemCode);

                        var itemTrack = new ReadWriteTrack
                        {
                            Item = item,
                            Type = type,
                            Code = itemCode,
                            Parent = null
                        };

                        XElement xOldRow = rawRow.Element(type.Name);
                        RecordAutonumberChanges(type, rawRow, rowItem);
                        XElement xNewRow = Write(type, rowItem, false, itemTrack, filter);
                        KeepLeftOutValues(xOldRow, xNewRow, filter);

                        if (!xOldRow.ElementEquals(xNewRow))
                        {
                            indexService.RemoveRowElement(type, rawRow, itemCode, persist: false);
                            indexService.IndexItem(type, rowItem, itemCode);
                            var newRowWrapper = new XElement("Row", new XAttribute("code", rowCode), xNewRow);
                            ioService.SpliceRowInRawPage(pageFileName, startOffset, byteLen, newRowWrapper, rowCode, out _);
                        }

                        updated = true;
                        continue;
                    }
                }

                if (updated)
                {
                    AfterAction?.Invoke(this, trigger);
                    return true;
                }
            }

            XRowTree rowTree = GetTree(type, item);
            if (rowTree != null && rowTree.Rows.Count > 0)
            {
                IEnumerable<PropertyInfoItem> props;
                if (filter?.Properties != null)
                {
                    props = filter.Behavior == UpdateFilterBehavior.Skip
                        ? propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped && !filter.Properties.Contains(s.PropertyName))
                        : propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped && filter.Properties.Contains(s.PropertyName));
                    props = props.OrderByDescending(s => s.PropertyType != null && ValueHelper.IsScalarType(s.PropertyType)).ToList();
                }
                else if (meta != null)
                {
                    props = meta.WriteProperties;
                }
                else
                {
                    props = propertyService.Properties(type.FullName).Where(s => !s.IsNotMapped)
                        .OrderByDescending(s => s.PropertyType != null && ValueHelper.IsScalarType(s.PropertyType)).ToList();
                }

                string pageCode = rowTree.Page?.GetFileCode();

                foreach (var row in rowTree.Rows)
                {
                    XAttribute rowCodeAtt = row.Attribute("code");
                    string itemCode = rowCodeAtt != null && pageCode != null
                        ? $"{pageCode}.{rowCodeAtt.Value}"
                        : "";

                    object rowItem = ItemForRow(type, row, newItem, filter);
                    ValidateUniqueAndPrimaryConstraints(type, rowItem, existingCodeToIgnore: itemCode);

                    if (meta == null || !meta.IsStandalone)
                        itemsCacheService.Clear(type, rowCodeAtt?.Value);

                    var itemTrack = new ReadWriteTrack
                    {
                        Item = item,
                        Type = type,
                        Code = itemCode,
                        Parent = track
                    };

                    XElement xOldRow = row.Element(type.Name);
                    RecordAutonumberChanges(type, row, rowItem);
                    XElement xNewRow = Write(type, rowItem, false, itemTrack, filter);
                    KeepLeftOutValues(xOldRow, xNewRow, filter);

                    if (!row.Element(type.Name).ElementEquals(xNewRow))
                    {
                        if (meta != null && meta.IsStandalone)
                        {
                            indexService.RemoveRowElement(type, row, itemCode, persist: false);
                            row.ReplaceNodes(xNewRow);
                            indexService.IndexItem(type, rowItem, itemCode);
                        }
                        else
                        {
                            var cascadeDelProps = props.Where(s => s.Cascade.HasFlag(CascadeOptions.Delete)).Select(s => s.PropertyName).ToHashSet();
                            string[] delRefTypes = { "complex", "children" };

                            var rowCollRefNodes = row.Element(type.Name).Elements().Where(s =>
                            {
                                XAttribute refTypeAtt = s.Attribute("refType");
                                XAttribute collTypeAtt = s.Attribute("collType");
                                return collTypeAtt != null && refTypeAtt != null &&
                                    (delRefTypes.Contains(refTypeAtt.Value) ||
                                    (refTypeAtt.Value == "reference" && cascadeDelProps.Contains(s.Name.LocalName)));
                            }).SelectMany(s => s.Elements());

                            var newCollRefNodes = xNewRow.Elements().Where(s =>
                            {
                                XAttribute refTypeAtt = s.Attribute("refType");
                                XAttribute collTypeAtt = s.Attribute("collType");
                                return collTypeAtt != null && refTypeAtt != null &&
                                    (delRefTypes.Contains(refTypeAtt.Value) ||
                                    (refTypeAtt.Value == "reference" && cascadeDelProps.Contains(s.Name.LocalName)));
                            }).SelectMany(s => s.Elements());

                            var newCollRefNodesString = newCollRefNodes.Select(s => s.ToString()).ToHashSet();
                            var delRefNodes = rowCollRefNodes.Where(s => !newCollRefNodesString.Contains(s.ToString())).ToList();
                            foreach (var code in delRefNodes)
                            {
                                XElement xDelRef = GetRawData(code.Value);
                                if (xDelRef == null) continue;

                                Type delRefType = ioService.GetItemType(code.Value);
                                if (delRefType == null) continue;

                                object delRef = Read(delRefType, xDelRef);
                                Delete(delRefType, delRef);
                            }

                            indexService.RemoveRowElement(type, row, itemCode, persist: false);
                            row.ReplaceNodes(xNewRow);
                            indexService.IndexItem(type, rowItem, itemCode);

                            var propRefProps = props.Where(s =>
                                s.TypeCategory != PropertyTypeCategory.None &&
                                s.TypeCategory != PropertyTypeCategory.Dictionary &&
                                s.ReferenceType != PropertyReferenceType.Complex &&
                                s.ReferenceType != PropertyReferenceType.SelfForeign);

                            foreach (var propRefProp in propRefProps)
                            {
                                object oldRefItem = propRefProp.Property.GetValue(item);
                                object newRefItem = propRefProp.Property.GetValue(rowItem);

                                if (propRefProp.ReferenceType == PropertyReferenceType.Parent)
                                {
                                    bool sameParent = true;
                                    Dictionary<string, object> oldParentKeyValues = null;
                                    Dictionary<string, object> newParentKeyValues = null;
                                    string[] baseParentKeys = null;

                                    if (oldRefItem != null)
                                    {
                                        oldParentKeyValues = GetPrimaryValues(propRefProp.PropertyType, oldRefItem);
                                        baseParentKeys = oldParentKeyValues.Select(s => s.Key).ToArray();
                                    }

                                    if (newRefItem != null)
                                    {
                                        newParentKeyValues = GetPrimaryValues(propRefProp.PropertyType, newRefItem);
                                        baseParentKeys ??= newParentKeyValues.Select(s => s.Key).ToArray();
                                    }

                                    if (baseParentKeys != null)
                                    {
                                        foreach (var parentKey in baseParentKeys)
                                        {
                                            var oldKeyValue = oldParentKeyValues != null && oldParentKeyValues.TryGetValue(parentKey, out var okv) ? okv : null;
                                            var newKeyValue = newParentKeyValues != null && newParentKeyValues.TryGetValue(parentKey, out var nkv) ? nkv : null;
                                            if (!Equals(oldKeyValue, newKeyValue))
                                            {
                                                sameParent = false;
                                                break;
                                            }
                                        }
                                    }

                                    if (!sameParent)
                                        RemoveFromParent(type, itemCode, propRefProp, oldRefItem);

                                    continue;
                                }

                                if (newRefItem == null)
                                    continue;

                                if (ReferenceEquals(oldRefItem, newRefItem) || (oldRefItem != null && oldRefItem.Equals(newRefItem)))
                                    continue;

                                if (oldRefItem != null &&
                                    (propRefProp.TypeCategory == PropertyTypeCategory.ValueTypeArray ||
                                     propRefProp.TypeCategory == PropertyTypeCategory.ValueTypeCollection))
                                {
                                    var oldRefItems = ((IEnumerable)oldRefItem).Cast<object>().ToList();
                                    var newRefItems = ((IEnumerable)newRefItem).Cast<object>().ToList();
                                    if (TypeExtentions.ListsEqual(oldRefItems, newRefItems))
                                        continue;
                                }

                                if (propRefProp.TypeCategory == PropertyTypeCategory.Class)
                                {
                                    if (oldRefItem != null)
                                        UpdateItem(propRefProp.PropertyType, oldRefItem, newRefItem, itemTrack);
                                }
                                else if (propRefProp.TypeCategory == PropertyTypeCategory.Array ||
                                         propRefProp.TypeCategory == PropertyTypeCategory.GenericCollection)
                                {
                                    Type childType = propRefProp.TypeCategory == PropertyTypeCategory.Array
                                        ? propRefProp.PropertyType.GetElementType()
                                        : propRefProp.PropertyType.GetGenericArguments().FirstOrDefault();

                                    var children = (newRefItem as IEnumerable)?.Cast<object>().ToList();
                                    if (children != null && children.Count > 0)
                                    {
                                        foreach (var child in children)
                                            Update(childType, child);
                                    }
                                }
                            }
                        }
                    }

                    ioService.Save(rowTree.Page);
                    updated = true;
                }
            }

            if (updated)
            {
                AfterAction?.Invoke(this, trigger);
            }

            return updated;
        }

        /// <summary>
        /// A relationship wrote a key into the item after the row's scalars were written: the key's stored form is
        /// written again by the one rule (<see cref="WriteProperty"/>, StoredValue), in its place, so the row never
        /// holds two forms of it (a <c>nil</c> mark beside the value read back null: review of TASK-063). Every
        /// write-back of a key goes through here.
        /// </summary>
        private void RewriteKeyProperty(Type type, object item, XElement xItem, string propertyName)
        {
            PropertyInfoItem prop = propertyService.Properties(type.FullName).FirstOrDefault(p => p.PropertyName == propertyName);
            if (prop == null)
                return;
            var scratch = new XElement(xItem.Name);
            WriteProperty(type, item, scratch, prop);
            XElement written = scratch.Element(prop.PropertyName);
            XElement stored = xItem.Element(prop.PropertyName);
            foreach (var extra in xItem.Elements(prop.PropertyName).Skip(1).ToList())
                extra.Remove();
            if (written != null)
            {
                written.Remove();
                if (stored != null)
                    stored.ReplaceWith(written);
                else
                    xItem.Add(written);
            }
            else
            {
                stored?.Remove();
            }
            xItem.SetAttributeValue(prop.PropertyName, scratch.Attribute(prop.PropertyName)?.Value);
        }

        private void WritePropertyMap(Type type, object item, Type propType, object value, XElement xItem, Dictionary<string, string> relationshipProps)
        {
            foreach (var relationshipProp in relationshipProps)
            {
                PropertyInfo remoteProp = propType.GetProperty(relationshipProp.Key);
                if (remoteProp != null)
                {
                    object remoteKeyValue = remoteProp.GetValue(value);
                    PropertyInfo localProp = type.GetProperty(relationshipProp.Value);
                    if (remoteKeyValue != null && localProp != null)
                    {
                        object localKeyValue = localProp.GetValue(item);
                        if (!Equals(remoteKeyValue, ValueHelper.DefaultOf(localProp.PropertyType)) && !remoteKeyValue.Equals(localKeyValue))
                        {
                            localProp.SetValue(item, remoteKeyValue);
                            RewriteKeyProperty(type, item, xItem, localProp.Name);
                        }
                    }
                }
            }
        }

        private XElement WriteItem(string name, Type type, object item, Dictionary<string, object> refKeySets, ReadWriteTrack writeTrack = null)
        {
            string refCode = null;
            if (refKeySets != null)
            {
                object refItem = null;
                if (refKeySets.Count > 0)
                {
                    refItem = SelectItemsByExample(type, PopulateItemProperties(type, refKeySets)).FirstOrDefault();
                }

                if (refItem != null)
                {
                    XRow xRow = GetRow(type, refItem);
                    if (xRow?.Row != null)
                        refCode = $"{System.IO.Path.GetFileNameWithoutExtension(xRow.Page.Path)}.{xRow.Row.Attribute("code").Value}";
                }
                else
                {
                    refCode = Create(type, item, false, writeTrack);
                }
            }

            Dictionary<string, object> keyValues = GetPrimaryValues(type, item);
            var xpe = new XElement(name, new XAttribute("refType", "reference"), refCode);
            if (!string.IsNullOrEmpty(refCode) && keyValues != null)
            {
                foreach (var keyValue in keyValues)
                    xpe.Add(new XAttribute(keyValue.Key, keyValue.Value));
            }
            return xpe;
        }

        public object Insert(Type type, object item, bool lazyLoad = false)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                Create(type, item, lazyLoad);
                return GetPrimaryValues(type, item);
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public int InsertRange(Type type, IEnumerable<object> items, bool lazyLoad = false)
        {
            if (items == null) return 0;

            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                ioService.BeginBatch();
                int count = 0;
                try
                {
                    foreach (var item in items)
                    {
                        Create(type, item, lazyLoad);
                        count++;
                    }
                }
                finally
                {
                    ioService.EndBatch();
                    // The batch's pages are on disk and its indexes not yet (ISS-043's crash window).
                    FaultInjection.Step("batch-written");
                    indexService.SaveAfterRowsWritten(null);
                }
                return count;
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public object InsertOrUpdate(Type type, object item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                var primValues = GetPrimaryValues(type, item);

                object reference = null;
                if (primValues != null && primValues.Count > 0)
                    reference = SelectItemsByExample(type, PopulateItemProperties(type, primValues), "*").FirstOrDefault();

                if (reference != null)
                    Update(type, reference, item, filter);
                else
                    Create(type, item, lazyLoad);

                return GetPrimaryValues(type, item);
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public bool Update(Type type, object item, object newItem, UpdateFilter filter = null)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                return UpdateItem(type, item, newItem, null, filter);
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public bool Update(Type type, object item, UpdateFilter filter = null)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                if (item == null)
                    exceptionService.Throw(new ArgumentNullException(nameof(item)));

                var meta = propertyService.GetMetadata(type);
                if (meta != null && meta.IsStandalone)
                {
                    if (meta.PrimaryKeyProperties.Length == 0)
                        exceptionService.Throw(new MissingPrimaryKeyValueException());

                    return UpdateItem(type, item, item, null, filter);
                }

                var primProps = propertyService.Properties(type.FullName)
                    .Where(s => s.IsPrimaryKey)
                    .Select(s => s.PropertyName)
                    .ToList();

                if (primProps.Count == 0)
                    exceptionService.Throw(new MissingPrimaryKeyValueException());

                object old = SelectItemsByExample(type, FilterItemProperties(type, item, primProps), "*").FirstOrDefault();
                if (old != null)
                    return UpdateItem(type, old, item, null, filter);

                return false;
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public bool Delete(Type type, object item) => Delete(type, item, false);

        public bool Delete(Type type, object item, bool isCascade)
        {
            EnterTableWriteLock(type, out var tLock, out bool usedGlobalWrite);
            try
            {
                propertyService.LoadType(type);
                if (item == null)
                    exceptionService.Throw(new ArgumentNullException(nameof(item)));

                var trigger = new TriggerEventArgs
                {
                    Item = item,
                    Type = type,
                    Action = DatabaseActions.Delete
                };

                BeforeAction?.Invoke(this, trigger);
                if (trigger.Cancel)
                    return false;

                bool shouldBatch = !isCascade && !ioService.IsBatchOrBuffered;
                if (shouldBatch)
                    ioService.BeginBatch();
                try
                {
                    bool deleted = false;
                    var meta = propertyService.GetMetadata(type);

                    if (meta != null &&
                        options.EnableIndexing &&
                        meta.PrimaryKeyProperties.Length > 0 &&
                        TryLookupCodesByProperties(type, item, meta.PrimaryKeyProperties, out var fastPkCodes, out bool fastPkCovered) &&
                        fastPkCovered)
                    {
                        if (fastPkCodes.Count == 0)
                            return false;

                        foreach (var itemCode in fastPkCodes)
                        {
                            if (DeleteByItemCode(type, itemCode, isCascade))
                                deleted = true;
                        }

                        if (deleted)
                        {
                            AfterAction?.Invoke(this, trigger);
                            return true;
                        }
                    }

                XRowTree rowTree = GetTree(type, item);
                if (rowTree != null && rowTree.Rows.Count > 0)
                {
                    string pageCode = rowTree.Page?.GetFileCode();

                    foreach (var row in rowTree.Rows.ToList())
                    {
                        XAttribute rowCodeAtt = row.Attribute("code");
                        string itemCode = rowCodeAtt != null && pageCode != null
                            ? $"{pageCode}.{rowCodeAtt.Value}"
                            : "";

                        if (meta == null || !meta.IsStandalone)
                            itemsCacheService.Clear(type, rowCodeAtt?.Value);
                        indexService.RemoveRowElement(type, row, itemCode);

                        if (meta == null || meta.HasCascadeDeleteOrParentProperties)
                        {
                            // Ensure full entity graph is loaded for cascade delete if caller passed a shell object
                            object fullEntity = Read(type, row, "*") ?? item;

                            var propRefProps = propertyService.Properties(type.FullName).Where(s =>
                                s.ReferenceType != PropertyReferenceType.Parent &&
                                s.ReferenceType != PropertyReferenceType.SelfForeign &&
                                s.Cascade.HasFlag(CascadeOptions.Delete) &&
                                s.TypeCategory != PropertyTypeCategory.None &&
                                s.TypeCategory != PropertyTypeCategory.Dictionary);

                            foreach (var propRefProp in propRefProps)
                            {
                                object refItem = propRefProp.Property.GetValue(fullEntity) ?? propRefProp.Property.GetValue(item);
                                if (refItem == null)
                                    continue;

                                if (propRefProp.TypeCategory == PropertyTypeCategory.Class)
                                {
                                    Delete(propRefProp.PropertyType, refItem, isCascade: true);
                                }
                                else
                                {
                                    Type childType = propRefProp.TypeCategory == PropertyTypeCategory.Array
                                        ? propRefProp.PropertyType.GetElementType()
                                        : propRefProp.PropertyType.GetGenericArguments().FirstOrDefault();

                                    if (childType == null)
                                        continue;

                                    var children = (refItem as IEnumerable)?.Cast<object>().ToList();
                                    if (children != null)
                                    {
                                        foreach (var child in children)
                                            Delete(childType, child, isCascade: true);
                                    }
                                }
                            }

                            if (!isCascade)
                                RemoveFromParentElements(type, row, itemCode);
                        }

                        rowTree.Page.RemoveRow(row);
                        deleted = true;

                        int remainingRows = rowTree.Page.Rows().Count();
                        if (remainingRows == 0)
                            ioService.Delete(rowTree.Page.Path);
                        else
                            ioService.Save(rowTree.Page);
                        RecordRowRemoved(type, rowTree.Table, rowTree.Page.Path, row, remainingRows);
                    }
                }

                    if (deleted)
                    {
                        AfterAction?.Invoke(this, trigger);
                    }

                    return deleted;
                }
                finally
                {
                    if (shouldBatch)
                        ioService.EndBatch();
                }
            }
            finally
            {
                ExitTableWriteLock(tLock, usedGlobalWrite);
            }
        }

        public bool DropType(Type type)
        {
            ThrowIfWritesRefused();
            EnterExclusive();
            // A statement, like any write (ISS-101): a flush from its triggers comes in its middle.
            globalWriteDepth++;
            try
            {
                var trigger = new TriggerEventArgs
                {
                    Cancel = false,
                    Type = type,
                    Action = DatabaseActions.Drop
                };

                BeforeAction?.Invoke(this, trigger);
                if (trigger.Cancel)
                    return false;

                string docFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(docFileName, typeof(Table), openOnly: true);

                if (tableFile != null)
                {
                    var pageFiles = tableFile.Pages()
                        .Select(p => p.Attribute("file")?.Value)
                        .Where(f => !string.IsNullOrEmpty(f))
                        .ToList();

                    foreach (var pageFile in pageFiles)
                    {
                        ioService.Delete(pageFile);
                    }

                    ioService.Delete(tableFile.Path);
                    autonumberService.ClearType(type);
                    indexService.Remove(type);
                    itemsCacheService.ClearCurrentCache();

                    AfterAction?.Invoke(this, trigger);
                    return true;
                }

                return false;
            }
            finally
            {
                try
                {
                    if (WritesRefused() == null)
                        ioService.WriteSaved();
                }
                finally
                {
                    globalWriteDepth--;
                    ExitExclusive();
                }
            }
        }
        private bool DeleteByItemCode(Type type, string itemCode, bool isCascade, bool cascadeChildren = true)
        {
            int dot = itemCode.IndexOf('.');
            if (dot <= 0 || dot >= itemCode.Length - 1)
                return false;

            string pageCode = itemCode.Substring(0, dot);
            string pageFileName = pageCode + ".xpag";
            string rowCode = itemCode.Substring(dot + 1);

            if (ioService.TryDeleteRow(pageFileName, rowCode, out XElement rawRow, out bool pageEmpty))
            {
                var meta = propertyService.GetMetadata(type);
                if (meta == null || !meta.IsStandalone)
                    itemsCacheService.Clear(type, rowCode);

                indexService.RemoveRowElement(type, rawRow, itemCode);

                string docFileName = $"{type.FullName}.xtab";
                XFile tableFile = ioService.OpenFileOrCreate(docFileName, type, openOnly: true);
                if (tableFile != null)
                {
                    if (pageEmpty)
                        ioService.Delete(pageFileName);
                    RecordRowRemoved(type, tableFile, pageFileName, rawRow, pageEmpty ? 0 : null);
                }

                if (meta == null || meta.HasCascadeDeleteOrParentProperties)
                {
                    if (cascadeChildren)
                        CascadeDeleteChildElements(type, rawRow, itemCode, isCascade);
                    else if (!isCascade)
                        RemoveFromParentElements(type, rawRow, itemCode);
                }

                return true;
            }

            return false;
        }

        private void CascadeDeleteChildElements(Type type, XElement rawRow, string itemCode, bool isCascade)
        {
            if (rawRow == null) return;

            var propRefProps = propertyService.Properties(type.FullName).Where(s =>
                s.ReferenceType != PropertyReferenceType.Parent &&
                s.ReferenceType != PropertyReferenceType.SelfForeign &&
                s.Cascade.HasFlag(CascadeOptions.Delete) &&
                s.TypeCategory != PropertyTypeCategory.None &&
                s.TypeCategory != PropertyTypeCategory.Dictionary);

            foreach (var prop in propRefProps)
            {
                Type childType = prop.TypeCategory == PropertyTypeCategory.Array
                    ? prop.PropertyType.GetElementType()
                    : prop.PropertyType.GetGenericArguments().FirstOrDefault() ?? prop.PropertyType;

                if (childType == null) continue;

                var childContainer = rawRow.Element(type.Name)?.Element(prop.PropertyName) ?? rawRow.Element(prop.PropertyName);
                if (childContainer != null)
                {
                    propertyService.LoadType(childType);
                    var childPkProps = propertyService.Properties(childType.FullName).Where(p => p.IsPrimaryKey).ToList();
                    foreach (var childElm in childContainer.Elements(childType.Name))
                    {
                        string childItemCode = childElm.Value;
                        if (!string.IsNullOrEmpty(childItemCode) && childItemCode.Contains('.'))
                        {
                            DeleteByItemCode(childType, childItemCode, isCascade: true);
                        }
                        else if (childPkProps.Count > 0)
                        {
                            object childShell = ValueHelper.CreateInstance(childType);
                            bool allPkFound = true;
                            foreach (var pkProp in childPkProps)
                            {
                                string attrVal = childElm.Attribute(pkProp.PropertyName)?.Value;
                                if (!string.IsNullOrEmpty(attrVal))
                                {
                                    object converted = ValueHelper.ParseScalar(pkProp.PropertyType, attrVal);
                                    pkProp.SetValue(childShell, converted);
                                }
                                else
                                {
                                    allPkFound = false;
                                    break;
                                }
                            }

                            if (allPkFound)
                            {
                                Delete(childType, childShell, isCascade: true);
                            }
                        }
                    }
                }
            }

            if (!isCascade)
            {
                RemoveFromParentElements(type, rawRow, itemCode);
            }
        }

        // A parent lists a child by its item code, "page.row" (XmlEngine.Relations, ParseChildren); the bare row code
        // matched no entry, so a delete through here left the child listed (ISS-088).
        /// <summary>
        /// Takes a deleted row out of the lists of the parents that hold it, for every delete path (by item code, by
        /// a scan) and the statement undo: the one unlink. The parent is the one the row names (the
        /// <c>refType="parent"</c> element a child row stores, whether or not the child has a key property: review of
        /// TASK-063), else the one its parent keys find. A child with no parent property names none, so nothing finds
        /// its parent here (ISS-095).
        /// </summary>
        private void RemoveFromParentElements(Type type, XElement rawRow, string itemCode)
        {
            var parRefProps = propertyService.Properties(type.FullName)
                .Where(s => s.ReferenceType == PropertyReferenceType.Parent);
            XElement entity = rawRow?.Element(type.Name) ?? rawRow?.Elements().FirstOrDefault();

            foreach (var parRefProp in parRefProps)
            {
                XElement named = entity?.Element(parRefProp.PropertyName);
                if (named != null && named.Attribute("refType")?.Value == "parent" &&
                    RemoveChildEntry(RowByItemCode(parRefProp.PropertyType, named.Value), parRefProp.PropertyType, type, itemCode))
                    continue;

                var parentPrmVals = new Dictionary<string, object>();
                if (parRefProp.ParentKeys != null)
                {
                    foreach (var pk in parRefProp.ParentKeys)
                    {
                        var pkProp = propertyService.Properties(type.FullName)
                            .FirstOrDefault(s => s.PropertyName == pk.LocalProperty);
                        if (pkProp != null)
                        {
                            string valStr = rawRow.Element(type.Name)?.Element(pkProp.PropertyName)?.Value
                                ?? rawRow.Element(pkProp.PropertyName)?.Value
                                ?? rawRow.Element(type.Name)?.Attribute(pkProp.PropertyName)?.Value
                                ?? rawRow.Attribute(pkProp.PropertyName)?.Value;
                            if (!string.IsNullOrEmpty(valStr))
                            {
                                parentPrmVals[pk.RemoteProperty] = ValueHelper.ParseScalar(pkProp.PropertyType, valStr);
                            }
                        }
                    }
                }

                if (parentPrmVals.Count > 0)
                {
                    object parent = SelectItemsByExample(parRefProp.PropertyType, PopulateItemProperties(parRefProp.PropertyType, parentPrmVals), "*").FirstOrDefault();
                    if (parent != null)
                        RemoveFromParent(type, itemCode, parRefProp, parent);
                }
            }
        }
    }
}
