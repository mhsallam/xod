using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xod.Helpers;

namespace Xod.Services
{
    /// <summary>
    /// Hands out autonumber values. Each (table, property) has one counter: the highest value the property
    /// has held, which starts from the table (<see cref="HighestUsedDelegate"/>: the larger of the mark the
    /// table records when rows are deleted, and its rows) and only rises, so a value isn't handed out again
    /// after the row that held it is deleted, in this context or after reopening (ISS-052). Not covered:
    /// an update that changes an autonumber value (ISS-059); two contexts writing one database at once
    /// without <c>DatabaseOptions.LockDatabase</c>; and a crash between a delete's page write and its
    /// mark write, since a batch flushes its files in no fixed order.
    /// </summary>
    internal class AutonumberService : IDisposable
    {
        private readonly object syncLock = new object();
        private readonly Dictionary<(Type Type, string Property), Counter> counters = new();
        private readonly PropertyService propertyService;
        private readonly ExceptionService exceptionService;

        /// <summary>The highest value the property has held in the table, or null for none.</summary>
        internal Func<Type, PropertyInfoItem, decimal?> HighestUsedDelegate { get; set; }

        private sealed class Counter
        {
            internal decimal? High;
        }

        internal AutonumberService(
            string path,
            PropertyService propertyService,
            IOService ioService,
            ExceptionService exceptionService)
        {
            this.propertyService = propertyService;
            this.exceptionService = exceptionService;
        }

        // The one place a counter starts: from the table, the first time this context needs it.
        private Counter CounterUnlocked(Type type, PropertyInfoItem prop)
        {
            var key = (type, prop.PropertyName);
            if (!counters.TryGetValue(key, out var counter))
            {
                counter = new Counter { High = HighestUsedDelegate?.Invoke(type, prop) };
                counters[key] = counter;
            }
            return counter;
        }

        private decimal Issue(Type type, PropertyInfoItem prop, decimal seed, decimal increment)
        {
            lock (syncLock)
            {
                var counter = CounterUnlocked(type, prop);
                decimal next = counter.High.HasValue ? counter.High.Value + increment : seed;
                if (next < seed) next = seed;
                counter.High = next;
                return next;
            }
        }

        // A row arrived with its own value: later numbers start above it.
        private void Observe(Type type, PropertyInfoItem prop, decimal value)
        {
            lock (syncLock)
            {
                var counter = CounterUnlocked(type, prop);
                if (!counter.High.HasValue || value > counter.High.Value)
                    counter.High = value;
            }
        }

        /// <summary>The highest value this context has handed out or seen for the property, without
        /// asking the table; null when it has done neither.</summary>
        internal decimal? KnownHigh(Type type, string propertyName)
        {
            lock (syncLock)
            {
                return counters.TryGetValue((type, propertyName), out var counter) ? counter.High : null;
            }
        }

        internal Dictionary<string, dynamic> Autonumber(Type type, object item)
        {
            var meta = propertyService.GetMetadata(type);
            var props = meta != null
                ? meta.AutonumberProperties
                : propertyService.Properties(type.FullName).Where(s => s.IsAutonumber).ToArray();

            for (int i = 0; i < props.Length; i++)
            {
                var prop = props[i];
                if (prop.ScalarKind == ScalarPropertyKind.Int32 && prop.IntGetter != null && prop.IntSetter != null)
                {
                    int cur = prop.IntGetter(item);
                    if (cur == 0)
                        prop.IntSetter(item, checked((int)Issue(type, prop, prop.IntSeed, prop.IntIncrement)));
                    else
                        Observe(type, prop, cur);
                    continue;
                }

                if (prop.ScalarKind == ScalarPropertyKind.Int64 && prop.LongGetter != null && prop.LongSetter != null)
                {
                    long cur = prop.LongGetter(item);
                    if (cur == 0L)
                        prop.LongSetter(item, checked((long)Issue(type, prop, prop.IntSeed, prop.IntIncrement)));
                    else
                        Observe(type, prop, cur);
                    continue;
                }

                if (prop.ScalarKind == ScalarPropertyKind.Guid && prop.GuidGetter != null && prop.GuidSetter != null)
                {
                    if (prop.GuidGetter(item) == Guid.Empty)
                        prop.GuidSetter(item, Guid.NewGuid());
                    continue;
                }

                AutonumberAnyType(type, item, prop);
            }

            return null;
        }

        private void AutonumberAnyType(Type type, object item, PropertyInfoItem prop)
        {
            Type underlying = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (!ValueHelper.IsNumeric(underlying) && underlying != typeof(Guid))
                exceptionService.Throw(new AutonumberDataTypeException());

            object currentVal = prop.GetValue(item);
            object defaultVal = ValueHelper.DefaultOf(prop.PropertyType);
            bool unset = currentVal == null || currentVal.Equals(defaultVal);

            if (underlying == typeof(Guid))
            {
                if (unset)
                    prop.SetValue(item, Guid.NewGuid());
                return;
            }

            if (unset)
            {
                decimal seed = Convert.ToDecimal(prop.IdentitySeed ?? 1, CultureInfo.InvariantCulture);
                decimal increment = Convert.ToDecimal(prop.IdentityIncrement ?? 1, CultureInfo.InvariantCulture);
                decimal next = Issue(type, prop, seed, increment);
                prop.SetValue(item, Convert.ChangeType(next, underlying, CultureInfo.InvariantCulture));
            }
            else
            {
                Observe(type, prop, Convert.ToDecimal(currentVal, CultureInfo.InvariantCulture));
            }
        }

        internal void ClearType(Type type)
        {
            lock (syncLock)
            {
                foreach (var key in counters.Keys.Where(k => k.Type == type).ToList())
                    counters.Remove(key);
            }
        }

        internal void Reset()
        {
            lock (syncLock)
            {
                counters.Clear();
            }
        }

        public void Dispose()
        {
            Reset();
        }
    }
}
