using System;
using System.Globalization;
using System.Linq;
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

        private static decimal? AutonumberValueOf(Type type, PropertyInfoItem prop, XElement row)
        {
            XElement entity = row?.Element(type.Name) ?? row?.Elements().FirstOrDefault();
            if (entity == null)
                return null;
            string text = prop.ValuePosition == ValuePosition.Attribute
                ? entity.Attribute(prop.PropertyName)?.Value
                : entity.Element(prop.PropertyName)?.Value;
            return decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v) ? v : null;
        }

        private static decimal? ReadAutonumberMark(XFile tableFile, string propertyName)
        {
            XElement mark = tableFile.Root()?.Elements(AutonumberMarkElement)
                .FirstOrDefault(e => e.Attribute("property")?.Value == propertyName);
            return decimal.TryParse(mark?.Attribute("last")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v)
                ? v
                : null;
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
