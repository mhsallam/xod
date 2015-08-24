using Xod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xod.Playground.Infra;

namespace Xod.Playground.Samples.Emplyees
{
    public class Service : ISample
    {
        private XodContext db = null;

        public void Open()
        {
            string appDir = Directory.GetCurrentDirectory();
            string dataDir = Path.GetFullPath(Path.Combine(appDir, @"..\..\Data\Employees\Xod"));
            string dirName = Path.GetDirectoryName(dataDir);

            Console.WriteLine();
            if (File.Exists(dataDir))
            {
                Console.WriteLine("Cleaning previous data.. Reinitializing database..");
                Directory.Delete(dirName, true);
            }
            else
                Console.WriteLine("Initializing database for the first time..");

            Console.WriteLine();
            if (!Directory.Exists(dirName))
                Directory.CreateDirectory(dirName);

            db = new XodContext(dataDir);
        }

        public void Init()
        {
            Console.WriteLine("INSERT Section object { Name: 'root', Sections: [ Section object { Name = 'ar' }, Section object { Name = 'en' } ] }");
            Console.WriteLine("INSERT Page object { Name: 'home', Section: reference to 'root' }");
        }

        public void Run()
        {
            Console.WriteLine();
            Console.WriteLine("Query 1: SELECT Page object WHERE Name property = '?'");

            Console.Write("Page.Name=?> ");
            string pageName = Console.ReadLine();
        }
    }
}
