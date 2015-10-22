using AcesDevelopers.Xod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcesDevelopers.Xod.Playground.Infra;

namespace AcesDevelopers.Xod.Playground.Samples.MultiRel
{
    public class Service : ISample
    {
        private XodContext db = null;

        public void Open()
        {
            string appDir = Directory.GetCurrentDirectory();
            string dataDir = Path.GetFullPath(Path.Combine(appDir, @"..\..\Data\MultiRel\Xod"));
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
            Master m1 = new Master()
            {
                Name = "master1",
                DetailsA = new List<Details>
                {
                    new Details() { Name = "A.Item1" },
                    new Details() { Name = "A.Item2" },
                    new Details() { Name = "A.Item3" }
                },
                DetailsB = new List<Details>
                {
                    new Details() { Name = "B.Item1" },
                    new Details() { Name = "B.Item2" },
                    new Details() { Name = "B.Item3" }
                }
            };
            db.Insert(m1);


            Master m2 = new Master() { Name = "master2" };
            db.Insert(m2);


            var dB2 = db.Select<Details>().FirstOrDefault(s => s.Name == "B.Item2");
            dB2.MasterId = m2.Id;
            dB2.Master = m2;

            db.Update(dB2);
        }

        public void Run()
        {

        }
    }
}
