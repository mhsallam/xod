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
        // The rows the statement under way has created, so that a statement that fails in a transaction or in buffered
        // mode removes them (ISS-062): a row whose second child is refused leaves no first child behind. The outermost
        // Create or UpdateItem of a statement keeps the list, under WriteStatement's engine-wide write lock. In direct
        // mode the outermost statement puts its files back instead, all or nothing (WriteStatement, ISS-125).
        //
        // A mitigation, not all or nothing: it removes created rows only. What a failed statement changed in rows that
        // were there before (an update's replaced row, children it dropped) isn't put back (ISS-089): in a transaction
        // its rollback takes back the rest; in buffered mode the rest stays, written by the next flush.
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

        // The statement's own undo of its created rows, in a transaction or buffered mode: in direct mode the outermost
        // statement puts its files back instead (WriteStatement).
        private bool BeginStatement()
        {
            if (statementLog != null || globalWriteDepth == 0 || statementSavepoint)
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
            TakeStatementBack(failure, log);
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
                    DeleteByItemCode(created[i].Type, created[i].Code, isCascade: false, ownTrigger: false, cascadeChildren: false);
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
                return CreateRow(type, item, shallowWrite, writeTrack);
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
                    long tRaw = WriteDiagnostics.Begin();
                    bool rawOk = ioService.TryAppendRawRowToPage(tableFile, item, meta, exceptionService, pageSize, out itemPath, out XElement rawPageElement, out bool rawTableModified);
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
                                    countAttr.Value = (currentCount + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
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

        // storedRow: the row an update replaces, for the digests it keeps (StoredDigest).
        private XElement Write(Type type, object item, bool shallowWrite = false, ReadWriteTrack writeTrack = null, UpdateFilter filter = null, XElement storedRow = null)
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
                    WriteProperty(type, item, xItem, prop, shallowWrite, writeTrack, storedRow);
            }
            else
            {
                for (int i = 0; i < props.Length; i++)
                    WriteProperty(type, item, xItem, props[i], shallowWrite, writeTrack, storedRow);
            }

            return xItem;
        }

        /// <summary>
        /// The XOD 1.x digest a row stores for <paramref name="prop"/>, when <paramref name="value"/> is it: a read
        /// gives a one-way value back as stored, and an update of the row writes it again. 1.x's digests are decimal
        /// digits, which <see cref="CryptoHelper.HashProperty"/> can't tell from a value, so they were hashed again
        /// (ISS-151). Kept only when the value has a 1.x digest's form and is what the row stores: a value the row
        /// stores in plaintext (a database of 2.0.1 or earlier, ISS-064, or a property given [Crypto] later) is hashed,
        /// as before. Null otherwise, and the value is hashed. A digit-only plaintext of that form that the row stores
        /// stays (ISS-171).
        /// </summary>
        private static string StoredDigest(XElement storedRow, PropertyInfoItem prop, string value)
        {
            if (storedRow == null || value == null || !CryptoHelper.IsLegacy1xDigest(value, prop.Encryption))
                return null;
            string stored = prop.ValuePosition == ValuePosition.Attribute
                ? storedRow.Attribute(prop.PropertyName)?.Value
                : storedRow.Element(prop.PropertyName)?.Value;
            return string.Equals(stored, value, StringComparison.Ordinal) ? stored : null;
        }

        private XElement WriteProperty(Type type, object item, XElement xItem, PropertyInfoItem prop, bool shallowWrite = false, ReadWriteTrack writeTrack = null, XElement storedRow = null)
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
                            StoredDigest(storedRow, prop, formattedValue) ?? CryptoHelper.HashProperty(formattedValue, prop.Encryption),
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
            // A value no branch stores (ISS-142): refused, never dropped. Its class's load refuses a declared one; this is
            // one an object property holds.
            else if (value != null && effectivePropType.IsValueType)
            {
                exceptionService.Throw(new XodException($"Property '{prop.PropertyName}' of {type.Name} holds {effectivePropType.Name}, " +
                    "which XOD can't store."));
            }

            return propertyElement;
        }

        private bool UpdateItem(Type type, object item, object newItem, ReadWriteTrack track = null, UpdateFilter filter = null)
        {
            bool statement = BeginStatement();
            try
            {
                return UpdateRows(type, item, newItem, track, filter);
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
                        XElement xNewRow = Write(type, rowItem, false, itemTrack, filter, xOldRow);
                        KeepLeftOutValues(xOldRow, xNewRow, filter);

                        if (!xOldRow.ElementEquals(xNewRow))
                        {
                            indexService.ReplaceRowKeys(type, rawRow, rowItem, itemCode);
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

                foreach (var match in rowTree.Rows)
                {
                    XElement row = match.Row;
                    string pageCode = match.Page?.GetFileCode();
                    XAttribute rowCodeAtt = row.Attribute("code");
                    string itemCode = rowCodeAtt != null && pageCode != null
                        ? $"{pageCode}.{rowCodeAtt.Value}"
                        : "";

                    object rowItem = ItemForRow(type, row, newItem, filter);
                    ValidateUniqueAndPrimaryConstraints(type, rowItem, existingCodeToIgnore: itemCode);

                    var itemTrack = new ReadWriteTrack
                    {
                        Item = item,
                        Type = type,
                        Code = itemCode,
                        Parent = track
                    };

                    XElement xOldRow = row.Element(type.Name);
                    RecordAutonumberChanges(type, row, rowItem);
                    XElement xNewRow = Write(type, rowItem, false, itemTrack, filter, xOldRow);
                    KeepLeftOutValues(xOldRow, xNewRow, filter);

                    if (!row.Element(type.Name).ElementEquals(xNewRow))
                    {
                        if (meta != null && meta.IsStandalone)
                        {
                            indexService.ReplaceRowKeys(type, row, rowItem, itemCode);
                            row.ReplaceNodes(xNewRow);
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
                            // Each dropped entry's row is deleted by the code the entry holds, with its own trigger
                            // (ISS-180, ISS-130). It was read and deleted by example, which matched the copy of a
                            // keyless child this update had just written, and any other parent's equal child.
                            foreach (var code in delRefNodes)
                            {
                                Type delRefType = ioService.GetItemType(code.Value);
                                if (delRefType == null) continue;

                                DeleteByItemCode(delRefType, code.Value, isCascade: false, ownTrigger: true);
                            }

                            indexService.ReplaceRowKeys(type, row, rowItem, itemCode);
                            row.ReplaceNodes(xNewRow);

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

                                    // A child without a key: the write above wrote it as a new row holding its
                                    // values, and it can't be updated by key; every update of a parent whose list
                                    // held one threw (ISS-179). A child with a key is updated by key after the write,
                                    // whether the write linked its stored row or created it.
                                    var children = ListLinksStoredRows(childType)
                                        ? (newRefItem as IEnumerable)?.Cast<object>().ToList()
                                        : null;
                                    if (children != null && children.Count > 0)
                                    {
                                        foreach (var child in children)
                                            Update(childType, child);
                                    }
                                }
                            }
                        }
                    }

                    ioService.Save(match.Page);
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
            return WriteStatement<object>(type, () =>
            {
                propertyService.LoadType(type);
                Create(type, item, lazyLoad);
                return GetPrimaryValues(type, item);
            });
        }

        public int InsertRange(Type type, IEnumerable<object> items, bool lazyLoad = false)
        {
            if (items == null) return 0;

            return WriteStatement(type, () =>
            {
                propertyService.LoadType(type);
                // The range is one statement (ISS-125): an item refused takes the items before it back too. In direct
                // mode its files go back (WriteStatement); in the other modes its own undo takes back every row it
                // created. Each item used to be a statement of its own.
                bool statement = BeginStatement();
                try
                {
                    int count = InBatch(true, () =>
                    {
                        int created = 0;
                        foreach (var item in items)
                        {
                            Create(type, item, lazyLoad);
                            created++;
                        }
                        return created;
                    });
                    // The batch's pages are on disk and its indexes not yet (ISS-043's crash window).
                    FaultInjection.Step("batch-written");
                    indexService.SaveAfterRowsWritten(null);
                    return count;
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
            });
        }

        public object InsertOrUpdate(Type type, object item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            return WriteStatement<object>(type, () =>
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
            });
        }

        public bool Update(Type type, object item, object newItem, UpdateFilter filter = null)
        {
            return WriteStatement(type, () =>
            {
                return UpdateItem(type, item, newItem, null, filter);
            });
        }

        public bool Update(Type type, object item, UpdateFilter filter = null)
        {
            return WriteStatement(type, () =>
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
            });
        }

        public bool Delete(Type type, object item) => Delete(type, item, false);

        public bool Delete(Type type, object item, bool isCascade)
        {
            return WriteStatement(type, () =>
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
                return InBatch(shouldBatch, () =>
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
                            if (DeleteByItemCode(type, itemCode, isCascade, ownTrigger: false))
                                deleted = true;
                        }

                        if (deleted)
                        {
                            AfterAction?.Invoke(this, trigger);
                            return true;
                        }
                    }

                    // The rows the example matches, each deleted by its code as the rows a key finds are, with the
                    // one cascade (ISS-130). Each was deleted on the page it was found on, and each cascaded child by
                    // example, so a child equal to another parent's went too (ISS-185). The statement's trigger is
                    // the caller's item, fired once.
                    XRowTree rowTree = GetTree(type, item);
                    var itemCodes = rowTree == null
                        ? new List<string>()
                        : rowTree.Rows
                            .Where(m => m.Page != null && m.Row.Attribute("code") != null)
                            .Select(m => $"{m.Page.GetFileCode()}.{m.Row.Attribute("code").Value}")
                            .ToList();
                    foreach (var itemCode in itemCodes)
                    {
                        if (DeleteByItemCode(type, itemCode, isCascade, ownTrigger: false))
                            deleted = true;
                    }

                    if (deleted)
                    {
                        AfterAction?.Invoke(this, trigger);
                    }

                    return deleted;
                });
            });
        }

        /// <summary>
        /// The rows of a type a predicate matches, deleted as one write statement (ISS-176): the matches are found under
        /// the statement's lock, and each is deleted by its code (<see cref="DeleteByItemCode"/>), its trigger given the
        /// match. Each was deleted by example, so the first of several equal rows without a key took them all, and the
        /// count and the triggers were short (ISS-186). In direct mode a delete refused part way (a trigger) leaves every match, the ones before it too
        /// (WriteStatement's savepoint); their changes wait in the statement's batch and are written once, at its end.
        /// It used to query outside a write, then delete each match as a statement of its own. So the predicate runs
        /// inside the write, as a trigger does: it may read, a write it makes is part of the statement, and it must not
        /// wait for another thread's read or write of this context, which waits for the statement.
        /// </summary>
        int IXodEngine.DeleteWhere(Type type, Func<object, bool> predicate)
        {
            if (predicate == null)
                exceptionService.Throw(new ArgumentNullException(nameof(predicate)));
            return WriteStatement(type, () => InBatch(!ioService.IsBatchOrBuffered, () =>
            {
                propertyService.LoadType(type);
                XFile tableFile = ioService.OpenFileOrCreate($"{type.FullName}.xtab", type, openOnly: true);
                if (tableFile == null)
                    return 0;

                var matches = new List<(string Code, object Item)>();
                foreach (var (page, row) in StoredRows(tableFile))
                {
                    object entity = Read(type, row, "*");
                    string rowCode = row.Attribute("code")?.Value;
                    if (entity != null && rowCode != null && predicate(entity))
                        matches.Add(($"{page}.{rowCode}", entity));
                }

                int deleted = 0;
                foreach (var (code, entity) in matches)
                {
                    if (DeleteByItemCode(type, code, isCascade: false, ownTrigger: true, triggerItem: entity))
                        deleted++;
                }
                return deleted;
            }));
        }

        /// <summary>
        /// Every registered table dropped as one write statement (ISS-176): each by <see cref="DropType"/>, nested in it.
        /// In direct mode a drop refused part way (its trigger) leaves every table, the ones dropped before it too, with
        /// their counters (WriteStatement's savepoint, AutonumberService.UndoStatement). Like a drop, it records no
        /// schema. It used to drop each type as a statement of its own. True when every type was dropped.
        /// </summary>
        bool IXodEngine.DropAll()
        {
            return WriteStatement<bool>(null, () =>
            {
                bool allDropped = true;
                foreach (var type in RegisteredTypes().ToList())
                {
                    if (!DropType(type))
                        allDropped = false;
                }
                return allDropped;
            }, recordSchemas: false);
        }

        // A statement's batch (InsertRange, a Delete, a DeleteWhere): its changes wait in it, and are written at its end, each file
        // once, when the statement succeeds (IOService.EndBatch). When the statement fails, the batch ends unwritten
        // (IOService.EndBatchUnwritten, ISS-177), before the statement's undo: it used to be ended in a finally, so a
        // failed statement wrote what it had done before its undo put the files back, and a write failing there
        // replaced the statement's own exception with the write's. Not open: the body runs as it is.
        private T InBatch<T>(bool open, Func<T> body)
        {
            if (!open)
                return body();
            ioService.BeginBatch();
            T result;
            try
            {
                result = body();
            }
            catch
            {
                ioService.EndBatchUnwritten();
                throw;
            }
            ioService.EndBatch();
            return result;
        }

        // A write statement like the others (WriteStatement, ISS-178): in direct mode its deletes are noted first and put
        // back if it fails, its AfterAction included. It was a write of its own (the lock, the depth, and its end written
        // in a finally, failed or not), with no savepoint: refused after its deletes, it kept them.
        public bool DropType(Type type)
        {
            return WriteStatement(type, () =>
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
                    // The counters go with the table; if the statement fails, its undo gives them back
                    // (AutonumberService.UndoStatement).
                    autonumberService.ClearType(type);
                    indexService.Remove(type);

                    AfterAction?.Invoke(this, trigger);
                    return true;
                }

                return false;
            }, recordSchemas: false);
        }
        /// <summary>
        /// The one delete of a stored row (ISS-130), by its item code (<c>page.row</c>): the rows a key or a scan finds,
        /// the rows a cascade reaches, an update's dropped entries, DeleteWhere's matches and the statement undo. It
        /// removes the row and its index keys, cascades (<see cref="CascadeDelete"/>) and, unless the row goes with its
        /// parent (<paramref name="isCascade"/>), takes it out of its parent's list. <paramref name="ownTrigger"/>: the
        /// row fires its own BeforeAction (which may keep it) and AfterAction, given <paramref name="triggerItem"/> or
        /// the object read from the row; without it the caller fires one for its statement (Delete), or none (the undo).
        /// False when the row is not there, as when an earlier cascade of the same statement took it.
        /// </summary>
        private bool DeleteByItemCode(Type type, string itemCode, bool isCascade, bool ownTrigger, object triggerItem = null, bool cascadeChildren = true)
        {
            int dot = itemCode?.IndexOf('.') ?? -1;
            if (dot <= 0 || dot >= itemCode.Length - 1)
                return false;

            string pageCode = itemCode.Substring(0, dot);
            string pageFileName = pageCode + ".xpag";
            string rowCode = itemCode.Substring(dot + 1);

            propertyService.LoadType(type);
            TriggerEventArgs trigger = null;
            if (ownTrigger && (BeforeAction != null || AfterAction != null))
            {
                XElement stored = GetRawData(itemCode);
                if (stored == null)
                    return false;

                trigger = new TriggerEventArgs
                {
                    Item = triggerItem ?? Read(type, stored),
                    Type = type,
                    Action = DatabaseActions.Delete
                };
                BeforeAction?.Invoke(this, trigger);
                if (trigger.Cancel)
                    return false;
            }

            if (!ioService.TryDeleteRow(pageFileName, rowCode, out XElement rawRow, out bool pageEmpty))
                return false;

            var meta = propertyService.GetMetadata(type);
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
                    CascadeDelete(type, rawRow);
                if (!isCascade)
                    RemoveFromParentElements(type, rawRow, itemCode);
            }

            if (trigger != null)
                AfterAction?.Invoke(this, trigger);
            return true;
        }

        /// <summary>
        /// The one cascade (ISS-130): each row a deleted row's <c>[Property(Cascade = Delete)]</c> properties name is
        /// deleted by its code, with its own trigger. A single reference stores its code in the property's element
        /// (<c>&lt;Addr refType="reference"&gt;page.row&lt;/Addr&gt;</c>), a list one entry per row; a property stored
        /// inside the row (no <c>refType</c>) has no row of its own. The delete by key looked only for list entries,
        /// so a single reference was left, and fired no child trigger; the delete by a scan deleted each child by
        /// example, with its trigger. An entry without a code is found by the key it carries.
        /// </summary>
        private void CascadeDelete(Type type, XElement rawRow)
        {
            XElement entity = rawRow?.Element(type.Name) ?? rawRow?.Elements().FirstOrDefault();
            if (entity == null)
                return;

            var cascadeProps = propertyService.Properties(type.FullName).Where(s =>
                s.ReferenceType != PropertyReferenceType.Parent &&
                s.ReferenceType != PropertyReferenceType.SelfForeign &&
                s.Cascade.HasFlag(CascadeOptions.Delete) &&
                (s.TypeCategory == PropertyTypeCategory.Class ||
                 s.TypeCategory == PropertyTypeCategory.Array ||
                 s.TypeCategory == PropertyTypeCategory.GenericCollection)).ToList();

            foreach (var prop in cascadeProps)
            {
                XElement xProp = entity.Element(prop.PropertyName);
                if (xProp?.Attribute("refType") == null)
                    continue;

                Type declared = prop.TypeCategory switch
                {
                    PropertyTypeCategory.Array => prop.PropertyType.GetElementType(),
                    PropertyTypeCategory.GenericCollection => prop.PropertyType.GetGenericArguments().FirstOrDefault(),
                    _ => prop.PropertyType,
                };
                if (declared == null)
                    continue;

                var entries = xProp.Attribute("collType") != null ? xProp.Elements().ToList() : new List<XElement> { xProp };
                foreach (var entry in entries)
                {
                    string code = entry.Value;
                    // The row of an object property, or of a list or array of object, is of the type its page holds
                    // (as Read takes it): deleted as System.Object, which has no table, it kept its index keys and
                    // its table kept an emptied page (review of TASK-089).
                    Type childType = declared == typeof(object) ? ioService.GetItemType(code) : declared;
                    if (childType == null)
                        continue;
                    if (string.IsNullOrEmpty(code) || !code.Contains('.'))
                        code = CodeByEntryKeys(childType, entry);
                    if (code != null)
                        DeleteByItemCode(childType, code, isCascade: true, ownTrigger: true);
                }
            }
        }

        // The code of the row an entry names by its key attributes alone, or null.
        private string CodeByEntryKeys(Type childType, XElement entry)
        {
            propertyService.LoadType(childType);
            var pkProps = propertyService.Properties(childType.FullName).Where(p => p.IsPrimaryKey).ToList();
            if (pkProps.Count == 0)
                return null;

            object shell = ValueHelper.CreateInstance(childType);
            foreach (var pkProp in pkProps)
            {
                string attrVal = entry.Attribute(pkProp.PropertyName)?.Value;
                if (string.IsNullOrEmpty(attrVal))
                    return null;
                pkProp.SetValue(shell, ValueHelper.ParseScalar(pkProp.PropertyType, attrVal));
            }

            XRow row = GetRow(childType, shell);
            string rowCode = row?.Row?.Attribute("code")?.Value;
            return rowCode == null ? null : $"{row.Page.GetFileCode()}.{rowCode}";
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
