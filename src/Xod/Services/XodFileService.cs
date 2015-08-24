using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Xod.Services
{
    public class XodFileService
    {
        public string Root { get; set; }

        public XodFileService(string root)
        {
            this.Root = root;
        }

        public bool FileExists(string path)
        {
            if (!path.StartsWith(this.Root))
                return File.Exists(string.Format("{0}\\{1}", this.Root, path));
            else
                return File.Exists(path);
        }
    }
}
