using System.Collections.Generic;
using System.Xml.Linq;

namespace Xod
{
    // The rows an example matches (XmlEngine.GetTree), each with the page it is on: they may be on several (ISS-136).
    internal class XRowTree
    {
        public XFile Table { get; set; }
        public List<XRow> Rows { get; set; }
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
