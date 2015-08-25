using AcesDevelopers.Xod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AcesDevelopers.Xod.Playground.Samples.MultiRel
{
    public class Master
    {
        [PrimaryKey, Property(AutoNumber = true)]
        public int Id { get; set; }
        public string Name { get; set; }

        [Children]
        public List<Details> DetailsA { get; set; }

        [Children]
        public List<Details> DetailsB { get; set; }
    }

    public class Details
    {
        [PrimaryKey, Property(AutoNumber = true)]
        public int Id { get; set; }
        public string Name { get; set; }

        [ParentKey("MasterId")]
        public Master Master { get; set; }
        public int MasterId { get; set; }
    }
}
