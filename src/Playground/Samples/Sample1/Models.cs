using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Xod.Playground.Samples.Sample1
{
    public class Section
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public List<Page> Pages { get; set; }
        public List<Section> Sections { get; set; }

        public Section Parent { get; set; }
    }

    public class Page
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public Section Section { get; set; }
    }
}
