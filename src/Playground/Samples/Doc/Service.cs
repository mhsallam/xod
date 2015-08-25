using AcesDevelopers.Xod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AcesDevelopers.Xod.Playground.Infra;

namespace AcesDevelopers.Xod.Playground.Samples.Doc
{
    public class Service : ISample
    {
        XodContext db = null;

        public void Open()
        {
            string appDir = Directory.GetCurrentDirectory();
            string path = Path.GetFullPath(Path.Combine(appDir, @"..\..\Data\Doc\Xod"));
            string dirName = Path.GetDirectoryName(path);

            Console.WriteLine();
            if (File.Exists(path))
            {
                Console.WriteLine("Cleaning previous data.. Reinitializing database..");
                Directory.Delete(dirName, true);
            }
            else
                Console.WriteLine("Initializing database for the first time..");

            Console.WriteLine();
            if (!Directory.Exists(dirName))
                Directory.CreateDirectory(dirName);

            db = new XodContext(path);

            db.RegisterType<Papers>();
            db.RegisterType<Food>();
            db.RegisterType<Toys>();

            db.Insert(new Bag()
            {
                Description = "Today, I have toys in my bag.",
                Content = new Toys()
            });

            //db = new XodContext(
            //    @"<database-file-path>\Xod",
            //    "<password>",
            //    new DatabaseOptions()
            //    {
            //        InitialCreate = false,
            //        LazyLoad = true
            //    });
        }

        public void Init()
        {
            Employee emp1 = new Employee()
            {
                Name = "Employee 1",
                BirthDate = new DateTime(1981, 5, 29),
                BasicSalary = 2000,
                Contacts = new List<Contact>
                {
                    new Contact() { Type = ContactType.Email, Value = "employee1@gmail.com" },
                },
                Credentials = new CredentialsDetails()
                {
                    UserName = "emp1",
                    Password = new System.Text.UTF8Encoding().GetBytes("12345678")
                }
            };
            Console.WriteLine("Create emp1!");
            Console.ReadKey();
            db.Insert(emp1);

            Employee emp2 = new Employee()
            {
                Name = "Employee 2",
                BirthDate = new DateTime(1981, 5, 29),
                BasicSalary = 3000,
                Contacts = new List<Contact>
                {
                    new Contact() { Type = ContactType.Email, Value = "employee2@gmail.com" },
                },
                Credentials = new CredentialsDetails()
                {
                    UserName = "emp2",
                    Password = new System.Text.UTF8Encoding().GetBytes("abcdefgh")
                },
                Supervisor = emp1
            };

    var empAdelHassan = db.Select<Employee>().FirstOrDefault(s =>
        s.Name.StartsWith("Adel")
        && s.Name.EndsWith("Hassan"));

    var empsOver3000 = db.Select<Employee>().Where(s => s.BasicSalary > 3000);

    var empNames = db.Select<Employee>().Where(s => s.BasicSalary > 3000).Select(s => s.Name);

    //Activate trigger
    EventHandler<TriggerEventArgs> after = (s, e) =>
    {
        if (e.Action == DatabaseActions.Insert && e.Type == typeof(Employee))
        {
            //code for creating new log item
        }
    };
    db.AfterAction += after;

    db.Insert(emp1);

    //Deactivate trigger
    db.AfterAction -= after;
        }

        public void Run()
        {
            Console.WriteLine("Delete!");
            Console.ReadKey();
            var emp = db.Select<Employee>().FirstOrDefault(s => s.Credentials.UserName == "emp2");
            db.Delete(emp);
        }
    }
}
