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
    /// after the row that held it is deleted, in this context or after reopening (ISS-052), or after an update
    /// changes it (ISS-059, XmlEngine.RecordAutonumberChanges). Not covered: two contexts writing one database at
    /// once without <c>DatabaseOptions.LockDatabase</c>; a crash between a delete's or an update's page write and
    /// its mark write, since a batch flushes its files in no fixed order; and a crash after a rollback and before
    /// the next flush, which records the rolled-back numbers in the marks (ISS-083, XmlEngine.RecordCountersInMarks).
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

        /// <summary>An autonumber an item leaves unset: no value, by the one rule (ValueHelper.IsNoValue: 0, an empty
        /// Guid, or null).</summary>
        internal static bool IsUnset(PropertyInfoItem prop, object value) =>
            ValueHelper.IsNoValue(value, prop.PropertyType);

        /// <summary>The whole numbers an autonumber of type <paramref name="t"/> holds, each exactly: its
        /// MinValue to MaxValue, but a float or a double only to 2^24 or 2^53 either way (beyond, two numbers
        /// would be stored as one).</summary>
        internal static void WholeRange(Type t, out decimal min, out decimal max)
        {
            if (t == typeof(float))
            {
                min = -16_777_216m;
                max = 16_777_216m;
                return;
            }
            if (t == typeof(double))
            {
                min = -9_007_199_254_740_992m;
                max = 9_007_199_254_740_992m;
                return;
            }
            min = Convert.ToDecimal(t.GetField("MinValue").GetValue(null), CultureInfo.InvariantCulture);
            max = Convert.ToDecimal(t.GetField("MaxValue").GetValue(null), CultureInfo.InvariantCulture);
        }

        // A row arrived with its own value (an insert that gave one, an update that changed one): later numbers
        // start above it.
        internal void Observe(Type type, PropertyInfoItem prop, decimal value)
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
                    if (!ValueHelper.SetsValue(prop, item))
                        prop.IntSetter(item, checked((int)Issue(type, prop, prop.Seed, prop.Increment)));
                    else
                        Observe(type, prop, prop.IntGetter(item));
                    continue;
                }

                if (prop.ScalarKind == ScalarPropertyKind.Int64 && prop.LongGetter != null && prop.LongSetter != null)
                {
                    if (!ValueHelper.SetsValue(prop, item))
                        prop.LongSetter(item, checked((long)Issue(type, prop, prop.Seed, prop.Increment)));
                    else
                        Observe(type, prop, prop.LongGetter(item));
                    continue;
                }

                if (prop.ScalarKind == ScalarPropertyKind.Guid && prop.GuidGetter != null && prop.GuidSetter != null)
                {
                    if (!ValueHelper.SetsValue(prop, item))
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
            if (!ValueHelper.IsAutonumberType(underlying))
                exceptionService.Throw(new AutonumberDataTypeException());

            object currentVal = prop.GetValue(item);
            bool unset = IsUnset(prop, currentVal);

            if (ValueHelper.IsGuid(underlying))
            {
                if (unset)
                    prop.SetValue(item, Guid.NewGuid());
                return;
            }

            if (unset)
            {
                decimal next = Issue(type, prop, prop.Seed, prop.Increment);
                // Past the type's whole numbers no number is handed out, as an int's checked cast refuses one
                // past its range: a float or double would store it as a number already handed out.
                WholeRange(underlying, out _, out decimal max);
                if (next > max)
                    throw new OverflowException($"The next autonumber of {type.Name}.{prop.PropertyName}, {next}, is beyond the whole numbers a {underlying.Name} holds exactly ({max}).");
                prop.SetValue(item, Convert.ChangeType(next, underlying, CultureInfo.InvariantCulture));
            }
            else
            {
                Observe(type, prop, Convert.ToDecimal(currentVal, CultureInfo.InvariantCulture));
            }
        }

        /// <summary>The types with a counter that has handed out or seen a number.</summary>
        internal List<Type> CountedTypes()
        {
            lock (syncLock)
            {
                return counters.Where(c => c.Value.High.HasValue).Select(c => c.Key.Type).Distinct().ToList();
            }
        }

        /// <summary>The counters' highs now, for <see cref="RaiseTo"/>: what a transaction starts from.</summary>
        internal Dictionary<(Type Type, string Property), decimal> Highs()
        {
            lock (syncLock)
            {
                return counters.Where(c => c.Value.High.HasValue).ToDictionary(c => c.Key, c => c.Value.High.Value);
            }
        }

        /// <summary>
        /// After a rollback: no counter stays below what it was when the transaction began. A counter only rises,
        /// but a drop resets its table's (<see cref="ClearType"/>); the rollback brings the table back, and with it
        /// the numbers its rows hold. Numbers the transaction handed out stay handed out (ISS-083).
        /// </summary>
        internal void RaiseTo(Dictionary<(Type Type, string Property), decimal> highs)
        {
            if (highs == null)
                return;
            lock (syncLock)
            {
                foreach (var (key, high) in highs)
                {
                    if (!counters.TryGetValue(key, out var counter))
                        counters[key] = counter = new Counter();
                    if (!counter.High.HasValue || counter.High.Value < high)
                        counter.High = high;
                }
            }
        }

        /// <summary>A drop: the table is gone, so its numbering starts again from the seed.</summary>
        // ISS-178: while a write statement is under way in direct mode, the counters it cleared (a drop), with the highs
        // they had, given back if it fails (UndoStatement); null when it cleared none. Clearing is the one change that
        // lowers a counter: every other raises it, and a failed statement leaves those raised (ISS-083).
        private bool inStatement;
        private Dictionary<(Type Type, string Property), decimal> statementCleared;

        /// <summary>A write statement starts in direct mode (XmlEngine.WriteStatement, with its savepoint).</summary>
        internal void BeginStatement()
        {
            lock (syncLock)
            {
                inStatement = true;
                statementCleared = null;
            }
        }

        /// <summary>The statement succeeded: what it cleared stays cleared.</summary>
        internal void EndStatement()
        {
            lock (syncLock)
            {
                inStatement = false;
                statementCleared = null;
            }
        }

        /// <summary>
        /// The statement failed, and its undo puts its tables back: the counters it cleared are raised back to the highs
        /// they had. A number a failed statement took is held only here until the next flush records it in the table's
        /// mark, so the table put back doesn't hold it. Raising never lowers a counter the statement raised since.
        /// </summary>
        internal void UndoStatement()
        {
            Dictionary<(Type Type, string Property), decimal> cleared;
            lock (syncLock)
            {
                cleared = statementCleared;
                inStatement = false;
                statementCleared = null;
            }
            RaiseTo(cleared);
        }

        internal void ClearType(Type type)
        {
            lock (syncLock)
            {
                foreach (var key in counters.Keys.Where(k => k.Type == type).ToList())
                {
                    if (inStatement && counters[key].High is decimal high)
                        (statementCleared ??= new Dictionary<(Type Type, string Property), decimal>()).TryAdd(key, high);
                    counters.Remove(key);
                }
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
