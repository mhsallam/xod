using Xod;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Xod.Playground.Samples.Employees
{
    public class Employee
    {
        [Property(AutoNumber = true)]
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime BirthDate { get; set; }
        public double BasicSalary { get; set; }

        public List<Contact> Contacts { get; set; }
        public Employee Supervisor { get; set; }
    }

    public class Contact
    {
        public Guid Id { get; set; }
        public string Tag { get; set; }
        public ContactType Type { get; set; }
        public string Value { get; set; }
    }

    public enum ContactType
    {
        Phone, Fax, Email
    }
}
