using AcesDevelopers.Xod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcesDevelopers.Xod.Playground.Infra;

namespace AcesDevelopers.Xod.Playground.Samples.Sample1
{
    public class Service : ISample
    {
        private XodContext db = null;

        public void Open()
        {
            string appDir = Directory.GetCurrentDirectory();
            string dataDir = Path.GetFullPath(Path.Combine(appDir, @"..\..\Data\Sample1\Xod"));
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
            Section root = new Section()
            {
                Name = "root",
                Sections = new List<Section>
                {
                    new Section() { Name = "ar" },
                    new Section() { Name = "en" },
                }
            };

            Page home = new Page()
            {
                Name = "home",
                Section = root
            };

            db.Insert<Section>(root);
            db.Insert<Page>(home);

            Console.WriteLine("INSERT Section object { Name: 'root', Sections: [ Section object { Name = 'ar' }, Section object { Name = 'en' } ] }");
            Console.WriteLine("INSERT Page object { Name: 'home', Section: reference to 'root' }");
        }

        public void Run()
        {
            Console.WriteLine();
            Console.WriteLine("Query 1: SELECT Page object WHERE Name property = '?'");

            Console.Write("Page.Name=?> ");
            string pageName = Console.ReadLine();
            var page = db.Select<Page>().FirstOrDefault(s => s.Name == pageName);
            if (page != null)
            {
                Console.WriteLine("Page.Name: {0}", page.Name);
            }
            else
            {
                Console.WriteLine("Sorry! Page not found");
            }

            Console.WriteLine();
            Console.WriteLine("Query 2: SELECT Section object WHERE Name property = '?'");

            Console.Write("Section.Name=?> ");
            string sectionName = Console.ReadLine();
            var section = db.Select<Section>().FirstOrDefault(s => s.Name == sectionName);
            if (section != null)
            {
                Console.WriteLine("Section.Name: {0}", section.Name);
            }
            else
            {
                Console.WriteLine("Sorry! Section not found");
            }
        }
    }
}
