using System.Collections.Generic;
using System.Xml.Linq;

namespace Xod
{
    internal class XRowTree
    {
        public XFile Table { get; set; }
        public XFile Page { get; set; }
        public List<XElement> Rows { get; set; }
    }

    internal class XRow
    {
        public XFile Table { get; set; }
        public XFile Page { get; set; }
        public XElement Row { get; set; }
    }

    internal class XCreatedItem
    {
        public string Code { get; set; }
        public object Item { get; set; }
    }

    internal class XMasterDetails
    {
        public XFile Master { get; set; }
        public List<XMasterDetails> Details { get; set; } = new List<XMasterDetails>();
    }
}
