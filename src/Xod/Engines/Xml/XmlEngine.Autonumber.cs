using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Xod.Services;

namespace Xod.Engines.Xml
{
    // Autonumber values and the table (ISS-052). A value handed out must never be handed out again. The
    // rows are the evidence of what was handed out, until a row is deleted: so a delete first records, in
    // the table document, the highest value its table has held (<Autonumber property="Id" last="7"/>,
    // PLAN-046 section 6). The next value is then above both that mark and every row.
    public partial class XmlEngine
    {
        private const string AutonumberMarkElement = "Autonumber";

        private PropertyInfoItem[] AutonumberPropertiesOf(Type type)
        {
            var meta = propertyService.GetMetadata(type);
            return meta != null
                ? meta.AutonumberProperties
                : propertyService.Properties(type.FullName).Where(p => p.IsAutonumber).ToArray();
        }

        private static bool IsNumericAutonumber(PropertyInfoItem prop)
        {
            Type t = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            return t != typeof(Guid) && Helpers.ValueHelper.IsNumeric(t);
        }

        /// <summary>The highest value the property has held in the table: the larger of the mark and the
        /// rows. AutonumberService starts each counter from this.</summary>
        private decimal? HighestAutonumberUsed(Type type, PropertyInfoItem prop)
        {
            if (!IsNumericAutonumber(prop))
                return null;

            XFile tableFile = ioService.OpenFileOrCreate($"{type.FullName}.xtab", typeof(Table), openOnly: true);
            if (tableFile == null)
                return null;

            decimal? high = ReadAutonumberMark(tableFile, prop.PropertyName);
            decimal? inRows = HighestInRows(type, prop, tableFile);
            if (inRows.HasValue && (!high.HasValue || inRows.Value > high.Value))
                high = inRows;
            return high;
        }

        private decimal? HighestInRows(Type type, PropertyInfoItem prop, XFile tableFile)
        {
            if (options.EnableIndexing && (prop.IsPrimaryKey || prop.IsIndexed || prop.IsUnique))
            {
                EnsureIndexHydrated(type);
                if (indexService.TryGetHighestInteger(type, prop, out decimal? fromIndex))
                    return fromIndex;
            }

            // No index to ask: read the column.
            decimal? high = null;
            foreach (var pageElm in tableFile.Pages().ToList())
            {
                string pageFileName = pageElm.Attribute("file")?.Value;
                if (string.IsNullOrEmpty(pageFileName) || !ioService.FileExists(pageFileName))
                    continue;
                XFile pageFile = ioService.OpenFileOrCreate<TablePage>(pageFileName, openOnly: true);
                if (pageFile == null)
                    continue;
                foreach (var row in pageFile.Rows())
                {
                    decimal? v = AutonumberValueOf(type, prop, row);
                    if (v.HasValue && (!high.HasValue || v.Value > high.Value))
                        high = v;
                }
            }
            return high;
        }

        private static decimal? AutonumberValueOf(Type type, PropertyInfoItem prop, XElement row) =>
            decimal.TryParse(StoredText(type, prop, row), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v) ? v : null;

        // The stored text of a property of a row (its <Row> element).
        private static string StoredText(Type type, PropertyInfoItem prop, XElement row)
        {
            XElement entity = row?.Element(type.Name) ?? row?.Elements().FirstOrDefault();
            if (entity == null)
                return null;
            return prop.ValuePosition == ValuePosition.Attribute
                ? entity.Attribute(prop.PropertyName)?.Value
                : entity.Element(prop.PropertyName)?.Value;
        }

        private static decimal? ReadAutonumberMark(XFile tableFile, string propertyName)
        {
            XElement mark = tableFile.Root()?.Elements(AutonumberMarkElement)
                .FirstOrDefault(e => e.Attribute("property")?.Value == propertyName);
            return decimal.TryParse(mark?.Attribute("last")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v)
                ? v
                : null;
        }

        private static readonly Func<object, object> ShallowCopy = (Func<object, object>)Delegate.CreateDelegate(
            typeof(Func<object, object>), typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic));

        /// <summary>
        /// The item an update writes to one row, for every update path: the new item, or a shallow copy of it that
        /// carries the row's own values where the update leaves them as they are, so that the checks, the index and
        /// the counter see the row as it will be stored. The caller's new item is never changed: one new item may
        /// serve several rows (an update by example) or several updates. A copy keeps the row's value for an
        /// autonumber the new item leaves unset (ISS-075: the row lost its number), and for a key, unique, indexed
        /// or autonumber value the update filter leaves out (the row keeps it, <see cref="KeepLeftOutValues"/>; the
        /// index was rebuilt from the new item's value instead).
        /// </summary>
        private object ItemForRow(Type type, XElement oldRow, object newItem, UpdateFilter filter)
        {
            object rowItem = newItem;
            foreach (var prop in AutonumberPropertiesOf(type))
            {
                if (UpdateWrites(filter, prop.PropertyName) && AutonumberService.IsUnset(prop, prop.GetValue(newItem)))
                    KeepStoredValue(type, prop, oldRow, newItem, ref rowItem);
            }

            if (filter?.Properties != null)
            {
                foreach (var prop in propertyService.Properties(type.FullName))
                {
                    if ((prop.IsPrimaryKey || prop.IsUnique || prop.IsIndexed || prop.IsAutonumber) &&
                        !prop.IsNotMapped && !UpdateWrites(filter, prop.PropertyName))
                        KeepStoredValue(type, prop, oldRow, newItem, ref rowItem);
                }
            }
            return rowItem;
        }

        // Whether an update with this filter writes the property (no filter: every property).
        private static bool UpdateWrites(UpdateFilter filter, string propertyName) =>
            filter?.Properties == null ||
            (filter.Behavior == UpdateFilterBehavior.Skip) != filter.Properties.Contains(propertyName);

        // The row's stored value, read as a read gives it, set on the row's item (copied from the new item first).
        private void KeepStoredValue(Type type, PropertyInfoItem prop, XElement oldRow, object newItem, ref object rowItem)
        {
            string text = StoredText(type, prop, oldRow);
            object value = string.IsNullOrEmpty(text)
                ? Helpers.ValueHelper.DefaultOf(prop.PropertyType)
                : ReadValue(prop, prop.PropertyType, text);
            if (ReferenceEquals(rowItem, newItem))
                rowItem = ShallowCopy(newItem);
            prop.SetValue(rowItem, value);
        }

        /// <summary>
        /// An update's autonumber values, once its checks have passed and before the row is written: a value the
        /// update changes is seen by the counter, and the old one is recorded in the table's mark, as a delete
        /// records it, so neither is handed out again, in this context or after reopening (ISS-059).
        /// <paramref name="rowItem"/> is what <see cref="ItemForRow"/> gave: a value the update leaves as it was
        /// is the row's, so it is no change.
        /// </summary>
        private void RecordAutonumberChanges(Type type, XElement oldRow, object rowItem)
        {
            bool changed = false;
            foreach (var prop in AutonumberPropertiesOf(type))
            {
                if (!IsNumericAutonumber(prop))
                    continue;
                object current = prop.GetValue(rowItem);
                if (AutonumberService.IsUnset(prop, current))
                    continue;

                decimal value = Convert.ToDecimal(current, CultureInfo.InvariantCulture);
                decimal? old = AutonumberValueOf(type, prop, oldRow);
                if (old == value)
                    continue;
                autonumberService.Observe(type, prop, value);
                changed |= old.HasValue;
            }

            if (!changed)
                return;
            XFile tableFile = ioService.OpenFileOrCreate($"{type.FullName}.xtab", typeof(Table), openOnly: true);
            if (tableFile != null && RaiseAutonumberMarks(type, tableFile, oldRow))
                ioService.Save(tableFile);
        }

        /// <summary>
        /// What an update filter leaves out keeps the old row's value: its elements and its attributes. One copy
        /// for both update paths; values stored as attributes, an autonumber key among them, were dropped.
        /// </summary>
        private static void KeepLeftOutValues(XElement xOldRow, XElement xNewRow, UpdateFilter filter)
        {
            if (filter?.Properties == null)
                return;

            if (filter.Behavior == UpdateFilterBehavior.Skip)
            {
                foreach (var name in filter.Properties)
                {
                    xNewRow.Element(name)?.Remove();
                    XElement old = xOldRow.Element(name);
                    if (old != null)
                        xNewRow.Add(new XElement(old));
                    xNewRow.SetAttributeValue(name, xOldRow.Attribute(name)?.Value);
                }
                return;
            }

            foreach (var old in xOldRow.Elements().Where(e => !filter.Properties.Contains(e.Name.LocalName)))
                xNewRow.Add(new XElement(old));
            foreach (var old in xOldRow.Attributes().Where(a => !filter.Properties.Contains(a.Name.LocalName)))
                xNewRow.SetAttributeValue(old.Name, old.Value);
        }

        /// <summary>Raises the table's marks to cover the row being deleted and everything this context
        /// handed out. True when the table document changed.</summary>
        private bool RaiseAutonumberMarks(Type type, XFile tableFile, XElement removedRow)
        {
            XElement root = tableFile.Root();
            if (root == null)
                return false;

            bool changed = false;
            foreach (var prop in AutonumberPropertiesOf(type))
            {
                if (!IsNumericAutonumber(prop))
                    continue;

                decimal? high = AutonumberValueOf(type, prop, removedRow);
                decimal? known = autonumberService.KnownHigh(type, prop.PropertyName);
                if (known.HasValue && (!high.HasValue || known.Value > high.Value))
                    high = known;
                if (!high.HasValue)
                    continue;

                decimal? mark = ReadAutonumberMark(tableFile, prop.PropertyName);
                if (mark.HasValue && mark.Value >= high.Value)
                    continue;

                XElement markElm = root.Elements(AutonumberMarkElement)
                    .FirstOrDefault(e => e.Attribute("property")?.Value == prop.PropertyName);
                if (markElm == null)
                {
                    markElm = new XElement(AutonumberMarkElement, new XAttribute("property", prop.PropertyName));
                    root.Add(markElm);
                }
                markElm.SetAttributeValue("last", high.Value.ToString(CultureInfo.InvariantCulture));
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// The table document's side of deleting a row, for both delete paths: the autonumber marks, the
        /// page's entry (its count, or the entry itself when the page is now empty), and the table document
        /// itself when no page is left. A table that holds a mark is kept with no pages, so that emptying a
        /// table doesn't restart its numbering; only <see cref="DropType"/> does. <c>remainingRows</c> is
        /// the rows left on the page: 0 when it's empty, null when the caller only knows it isn't (the
        /// count then goes down by one).
        /// </summary>
        private void RecordRowRemoved(Type type, XFile tableFile, string pageFileName, XElement removedRow, int? remainingRows)
        {
            if (tableFile == null)
                return;

            bool changed = RaiseAutonumberMarks(type, tableFile, removedRow);

            string pageName = System.IO.Path.GetFileName(pageFileName);
            XElement tablePage = tableFile.Pages().FirstOrDefault(s =>
                string.Equals(s.Attribute("file")?.Value, pageName, StringComparison.OrdinalIgnoreCase));

            if (remainingRows == 0)
            {
                if (tablePage != null)
                {
                    tablePage.Remove();
                    changed = true;
                    if (!tableFile.Pages().Any())
                    {
                        indexService.Remove(type);
                        if (!tableFile.Root().Elements(AutonumberMarkElement).Any())
                        {
                            ioService.Delete(tableFile.Path);
                            return;
                        }
                    }
                }
            }
            else if (tablePage != null)
            {
                XAttribute countAttr = tablePage.Attribute("count");
                int? count = remainingRows;
                if (count == null && countAttr != null && int.TryParse(countAttr.Value, out int current) && current > 0)
                    count = current - 1;
                if (count.HasValue)
                {
                    tablePage.SetAttributeValue("count", count.Value);
                    changed = true;
                }
            }

            if (changed)
                ioService.Save(tableFile);
        }
    }
}
