using AcesDevelopers.Xod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AcesDevelopers.Xod.Playground.Samples.Orders
{
    public class Order
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }

        [Children]
        public List<OrderDetails> Details { get; set; }

        [ParentKey("CustomerId")]
        public Customer Customer { get; set; }
        public int CustomerId { get; set; }
    }

    public class Customer
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Address { get; set; }
        public string Phone { get; set; }

        [Children]
        public List<Order> Orders { get; set; }
    }

    public class OrderDetails
    {
        public long Id { get; set; }
        
        [ForeignKey("ItemId")]
        public ProductItem Item { get; set; }
        public Guid ItemId { get; set; }
        public int Quantity { get; set; }

        [ParentKey("MasterId")]
        public Order Master { get; set; }
        public int MasterId { get; set; }
    }

    public class ProductItem
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public double Price { get; set; }
    }
}
