using System.Collections.Generic;

namespace Xod
{
    internal class Database
    {
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public string Version { get; set; } = "2.0";

        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public string Format { get; set; } = "Xml";
    }

    internal class Table
    {
        public string Name { get; set; }
        public List<TablePage> Pages { get; set; }
        public List<PageIndex> Indexes { get; set; }
        internal AutonumberCache Seed { get; set; }

        public Table()
        {
            Pages = new List<TablePage>();
            Indexes = new List<PageIndex>();
        }
    }

    internal class TablePage
    {
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public bool Full { get; set; }
        public List<PageRow> Rows { get; set; }

        public TablePage()
        {
            Rows = new List<PageRow>();
        }
    }

    internal class PageIndex
    {
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public string Begins { get; set; }
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public string Ends { get; set; }
    }

    internal class PageRow
    {
        public string File { get; set; }
        [PropertyAttribute(Position = ValuePosition.Attribute)]
        public bool Full { get; set; }
    }
}
