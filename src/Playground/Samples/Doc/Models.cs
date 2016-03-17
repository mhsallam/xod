using Xod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Xod.Playground.Samples.Doc
{
    public class Employee
    {
        [Property(AutoNumber = true)]
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime BirthDate { get; set; }
        public double BasicSalary { get; set; }
        public List<Contact> Contacts { get; set; }

        //[ForeignKey("CredentialsId")]
        public CredentialsDetails Credentials { get; set; }
        //public int CredentialsId { get; set; }

        [ForeignKey("SupervisorId")]
        public Employee Supervisor { get; set; }
        public int SupervisorId { get; set; }
    }

    public class Contact
    {
        public string Tag { get; set; }
        public ContactType Type { get; set; }
        public string Value { get; set; }
    }

    public enum ContactType
    {
        Phone, Fax, Email
    }

    public class CredentialsDetails
    {
        [Property(AutoNumber = true)]
        public int Id { get; set; }
        public string UserName { get; set; }
        public byte[] Password { get; set; }
    }

    public class Bag
    {
        public int Id { get; set; }
        public string Description { get; set; }
        
        public object Content { get; set; }
    }

    public class Papers { }
    public class Food { }
    public class Toys { }
}
