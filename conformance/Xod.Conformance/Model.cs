// The conformance dataset's classes (PLAN-047). Their full names are the table names in the fixtures,
// so renaming or moving one is a format change: regenerate the fixtures and say so in the plan.
using System;
using System.Collections.Generic;

namespace Xod.Conformance.Model
{
    public enum Tier { Basic = 0, Silver = 1, Gold = 2 }

    [Flags]
    public enum Perm { None = 0, Read = 1, Write = 2, Admin = 4 }

    public class Address
    {
        public string City { get; set; }
        public string Country { get; set; }
    }

    public class Customer
    {
        public int Id { get; set; }
        [Required] public string Name { get; set; }
        [UniqueKey] public string Email { get; set; }
        [Property(Indexed = true)] public Tier Tier { get; set; }
        public string City { get; set; }
        public int? Rating { get; set; }
        public decimal CreditLimit { get; set; }
        public DateTime Joined { get; set; }
        [Property(Position = ValuePosition.Attribute)] public bool Active { get; set; }
        [Crypto] public string PasswordHash { get; set; }
        public Dictionary<string, string> Tags { get; set; }
        public List<string> Labels { get; set; }
        public Address Address { get; set; }
        [Children] public List<Order> Orders { get; set; }
    }

    public class Order
    {
        public int Id { get; set; }
        public int CustomerId { get; set; }
        [Property(Indexed = true)] public string Status { get; set; }
        public DateOnly Placed { get; set; }
        public decimal? Discount { get; set; }
        [ParentKey(nameof(CustomerId))] public Customer Customer { get; set; }
        [Children] public List<OrderLine> Lines { get; set; }
    }

    public class OrderLine
    {
        public int Id { get; set; }
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        [ParentKey(nameof(OrderId))] public Order Order { get; set; }
        [ForeignKey(nameof(ProductId))] public Product Product { get; set; }
    }

    public class Product
    {
        public int Id { get; set; }
        [UniqueKey] public string Sku { get; set; }
        public string Name { get; set; }
        [Property(Indexed = true)] public string Category { get; set; }
        public decimal Price { get; set; }
        public double? Weight { get; set; }
        public bool Discontinued { get; set; }
    }

    /// <summary>One column of every scalar type XOD stores (PLAN-045 section 4), with nulls.</summary>
    public class Event
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public byte Priority { get; set; }
        public short Seats { get; set; }
        public long Big { get; set; }
        public ulong Huge { get; set; }
        public float Ratio { get; set; }
        public double Score { get; set; }
        public decimal Price { get; set; }
        public char Code { get; set; }
        public Perm Perms { get; set; }
        public Guid Ref { get; set; }
        public DateTime StartsUtc { get; set; }
        public DateTime Floating { get; set; }
        public DateTimeOffset At { get; set; }
        public DateOnly Day { get; set; }
        public TimeOnly Opens { get; set; }
        public TimeSpan Length { get; set; }
        public int? MaybeInt { get; set; }
        public DateTime? MaybeWhen { get; set; }
        public bool? MaybeFlag { get; set; }
        [Markup] public string Html { get; set; }
        [Property(Indexed = true)] public decimal Fee { get; set; }
    }

    public class Integration
    {
        public int Id { get; set; }
        public string Name { get; set; }
        [Crypto(CryptoMethod.AES256)] public string ApiKey { get; set; }
    }
}

namespace Xod.Conformance.Model.Shop
{
    public class Note
    {
        public int Id { get; set; }
        public string Text { get; set; }
    }
}

namespace Xod.Conformance.Model.Archive
{
    public class Note
    {
        public int Id { get; set; }
        public string Text { get; set; }
        public int Year { get; set; }
    }
}
