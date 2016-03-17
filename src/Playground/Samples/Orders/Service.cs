using Xod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xod.Playground.Infra;

namespace Xod.Playground.Samples.Orders
{
    public class Service : ISample
    {
        private XodContext db = null;

        public void Open()
        {
            string appDir = Directory.GetCurrentDirectory();
            string dataDir = Path.GetFullPath(Path.Combine(appDir, @"..\..\Data\Orders\Xod"));
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
            Create4();
        }

        private void Create1()
        {
            ProductItem item1 = new ProductItem()
            {
                Name = "Product 1",
                Price = 15.5
            };
            db.Insert(item1);

            Customer customer1 = new Customer()
            {
                Name = "Sallam",
                Orders = new List<Order>
                {
                    new Order()
                    {
                        Date = DateTime.Now,
                        Details = new List<OrderDetails>
                        {
                            new OrderDetails() { Quantity = 2, Item = item1 }
                        }
                    }
                }
            };

            db.Insert(customer1);
        }

        private void Create2()
        {
            Order ord = new Order()
            {
                Date = DateTime.Now,
                Details = new List<OrderDetails>
                {
                    new OrderDetails() { Quantity = 2 }
                },
                Customer = new Customer() { Name = "Adel" }
            };
            db.Insert(ord);
        }

        private void Create3()
        {
            ProductItem item1 = new ProductItem()
            {
                Name = "Product 1",
                Price = 15.5
            };
            db.Insert(item1);

            Customer customer1 = new Customer()
            {
                Name = "Sallam",
            };

            db.Insert(customer1);

            Order order = new Order()
            {
                Date = DateTime.Now,
                Details = new List<OrderDetails>
                {
                    new OrderDetails() { Quantity = 2, Item = item1 }
                },
                CustomerId = customer1.Id
            };

            db.Insert(order);
        }

        private void Create4()
        {
            Create3();
            var od = db.Select<OrderDetails>().FirstOrDefault();
            od.Quantity = 4;
            db.Update(od);
        }

        public void Run()
        {

        }
    }
}
