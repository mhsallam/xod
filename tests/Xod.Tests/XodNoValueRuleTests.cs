using System;
using System.Collections.Generic;
using System.Linq;
using Xod.Helpers;
using Xod.Services;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-199: a value that is no value (a key part, a property an example sets, an autonumber left unset, a relation's
    /// key) has one rule, <c>ValueHelper.IsNoValue</c>: null or its type's default. <c>ValueHelper.SetsValue</c> reads a
    /// property through its typed getter where it has one; each typed case must give that rule's answer.
    /// </summary>
    public class XodNoValueRuleTests
    {
        public enum NvColor { None, Red }

        public class NvRow
        {
            public int Int { get; set; }
            public long Long { get; set; }
            public decimal Decimal { get; set; }
            public double Double { get; set; }
            public bool Bool { get; set; }
            public string String { get; set; }
            public Guid Guid { get; set; }
            public DateTime DateTime { get; set; }
            public short Short { get; set; }
            public float Float { get; set; }
            public NvColor Color { get; set; }
            public int? NullableInt { get; set; }
            public Guid? NullableGuid { get; set; }
        }

        // Each property's values to try: its default in every spelling C# has, and values that are values.
        public static IEnumerable<object[]> Cases() => new (string Prop, object[] Values)[]
        {
            ("Int", new object[] { 0, 1, -1 }),
            ("Long", new object[] { 0L, 1L, long.MinValue }),
            ("Decimal", new object[] { 0m, 0.00m, -0.0m, 1.5m }),
            ("Double", new object[] { 0.0, -0.0, double.NaN, double.Epsilon, 1.0 }),
            ("Bool", new object[] { false, true }),
            ("String", new object[] { null, "", " ", "x" }),
            ("Guid", new object[] { Guid.Empty, new Guid("6f1c2b8e-1d4a-4c3b-9e2f-000000000001") }),
            ("DateTime", new object[] { default(DateTime), new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2020, 1, 1) }),
            ("Short", new object[] { (short)0, (short)7 }),
            ("Float", new object[] { 0f, -0f, float.NaN, 1f }),
            ("Color", new object[] { NvColor.None, NvColor.Red }),
            ("NullableInt", new object[] { null, 0, 3 }),
            ("NullableGuid", new object[] { null, Guid.Empty, new Guid("6f1c2b8e-1d4a-4c3b-9e2f-000000000002") }),
        }.SelectMany(c => c.Values.Select(v => new object[] { c.Prop, v }));

        [Theory]
        [MemberData(nameof(Cases))]
        public void A_typed_getter_gives_the_one_rule_s_answer(string propName, object value)
        {
            var service = new PropertyService();
            service.LoadType(typeof(NvRow));
            var prop = service.Properties(typeof(NvRow).FullName).Single(p => p.PropertyName == propName);

            var row = new NvRow();
            typeof(NvRow).GetProperty(propName).SetValue(row, value);

            bool expected = !ValueHelper.IsNoValue(prop.GetValue(row), prop.PropertyType);
            Assert.Equal(expected, ValueHelper.SetsValue(prop, row));
        }

        [Fact]
        public void The_rule_is_null_or_the_type_s_default()
        {
            Assert.True(ValueHelper.IsNoValue(null, typeof(int)));
            Assert.True(ValueHelper.IsNoValue(0, typeof(int)));
            Assert.True(ValueHelper.IsNoValue(0.00m, typeof(decimal)));
            Assert.True(ValueHelper.IsNoValue(-0.0, typeof(double)));
            Assert.True(ValueHelper.IsNoValue("", typeof(string)));
            Assert.True(ValueHelper.IsNoValue(Guid.Empty, typeof(Guid)));
            Assert.False(ValueHelper.IsNoValue(double.NaN, typeof(double)));
            Assert.False(ValueHelper.IsNoValue(" ", typeof(string)));
            // A nullable type's default is null, so its 0 is a value.
            Assert.False(ValueHelper.IsNoValue(0, typeof(int?)));
        }
    }
}
