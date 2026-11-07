using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Xod.Helpers;

namespace Xod.Services
{
    internal class AutonumberService : IDisposable
    {
        private readonly object syncLock = new object();
        private readonly List<AutonumberCache> items = new List<AutonumberCache>();
        private readonly PropertyService propertyService;
        private readonly ExceptionService exceptionService;

        internal Func<Type, object> FindLastDelegate { get; set; }

        internal AutonumberService(
            string path,
            PropertyService propertyService,
            IOService ioService,
            ExceptionService exceptionService)
        {
            this.propertyService = propertyService;
            this.exceptionService = exceptionService;
        }

        private AutonumberCache FindCacheUnlocked(Type type, string propName)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var c = items[i];
                if (c.Type == type && string.Equals(c.PropertyName, propName, StringComparison.Ordinal))
                    return c;
            }
            return null;
        }

        private int NextInt32(Type type, PropertyInfoItem prop)
        {
            lock (syncLock)
            {
                var cached = FindCacheUnlocked(type, prop.PropertyName);
                int seedInt = prop.IntSeed;
                int incInt = prop.IntIncrement;

                if (cached != null)
                {
                    int nextInt = (int)cached.Int64Value + incInt;
                    if (nextInt < seedInt) nextInt = seedInt;
                    cached.Int64Value = nextInt;
                    cached.Value = nextInt;
                    return nextInt;
                }

                int initInt = seedInt;
                if (FindLastDelegate != null)
                {
                    var last = FindLastDelegate(type);
                    if (last != null)
                    {
                        int lastInt = prop.IntGetter != null ? prop.IntGetter(last) : (prop.GetValue(last) is int li ? li : 0);
                        if (lastInt != 0)
                            initInt = lastInt + incInt;
                    }
                }

                items.Add(new AutonumberCache
                {
                    Type = type,
                    PropertyName = prop.PropertyName,
                    Int64Value = initInt,
                    Value = initInt
                });
                return initInt;
            }
        }

        private long NextInt64(Type type, PropertyInfoItem prop)
        {
            lock (syncLock)
            {
                var cached = FindCacheUnlocked(type, prop.PropertyName);
                long seedLong = prop.IntSeed;
                long incLong = prop.IntIncrement;

                if (cached != null)
                {
                    long nextLong = cached.Int64Value + incLong;
                    if (nextLong < seedLong) nextLong = seedLong;
                    cached.Int64Value = nextLong;
                    cached.Value = nextLong;
                    return nextLong;
                }

                long initLong = seedLong;
                if (FindLastDelegate != null)
                {
                    var last = FindLastDelegate(type);
                    if (last != null)
                    {
                        long lastLong = prop.LongGetter != null ? prop.LongGetter(last) : (prop.GetValue(last) is long ll ? ll : 0L);
                        if (lastLong != 0L)
                            initLong = lastLong + incLong;
                    }
                }

                items.Add(new AutonumberCache
                {
                    Type = type,
                    PropertyName = prop.PropertyName,
                    Int64Value = initLong,
                    Value = initLong
                });
                return initLong;
            }
        }

        private dynamic NextNumber(Type type, PropertyInfoItem prop)
        {
            dynamic defValue = ValueHelper.DefaultOf(prop.PropertyType);
            if (!prop.IsAutonumber)
                return defValue;

            Type underlyingType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            if (underlyingType == typeof(int))
                return NextInt32(type, prop);
            if (underlyingType == typeof(long))
                return NextInt64(type, prop);

            lock (syncLock)
            {
                var cached = FindCacheUnlocked(type, prop.PropertyName);
                int seedInt = prop.IdentitySeed ?? 1;
                int incInt = prop.IdentityIncrement ?? 1;

                MethodInfo sumMethod = typeof(NumericOperations)
                    .GetMethod(nameof(NumericOperations.Sum))
                    .MakeGenericMethod(underlyingType);

                dynamic seed = Convert.ChangeType(seedInt, underlyingType, CultureInfo.InvariantCulture);
                dynamic increment = Convert.ChangeType(incInt, underlyingType, CultureInfo.InvariantCulture);

                if (cached != null)
                {
                    dynamic nextVal = sumMethod.Invoke(null, new object[] { new object[] { cached.Value, increment } });
                    if (ValueHelper.CompareValues(nextVal, seed) < 0)
                        nextVal = seed;

                    cached.Value = nextVal;
                    return nextVal;
                }

                dynamic initialVal = seed;
                if (FindLastDelegate != null)
                {
                    var last = FindLastDelegate(type);
                    if (last != null)
                    {
                        object lastPropVal = prop.GetValue(last);
                        if (lastPropVal != null)
                        {
                            initialVal = sumMethod.Invoke(null, new object[] { new object[] { lastPropVal, increment } });
                        }
                    }
                }

                items.Add(new AutonumberCache
                {
                    Type = type,
                    PropertyName = prop.PropertyName,
                    Value = initialVal,
                    Method = sumMethod
                });

                return initialVal;
            }
        }

        internal Dictionary<string, dynamic> Autonumber(Type type, object item)
        {
            var meta = propertyService.GetMetadata(type);
            if (meta != null)
            {
                var autoArr = meta.AutonumberProperties;
                for (int i = 0; i < autoArr.Length; i++)
                {
                    var autonumberProp = autoArr[i];
                    if (autonumberProp.ScalarKind == ScalarPropertyKind.Int32 &&
                        autonumberProp.IntGetter != null &&
                        autonumberProp.IntSetter != null)
                    {
                        int cur = autonumberProp.IntGetter(item);
                        if (cur == 0)
                        {
                            int next = NextInt32(type, autonumberProp);
                            autonumberProp.IntSetter(item, next);
                        }
                        else
                        {
                            lock (syncLock)
                            {
                                var cached = FindCacheUnlocked(type, autonumberProp.PropertyName);
                                if (cached == null)
                                {
                                    items.Add(new AutonumberCache
                                    {
                                        Type = type,
                                        PropertyName = autonumberProp.PropertyName,
                                        Int64Value = cur,
                                        Value = cur
                                    });
                                }
                                else if (cur > cached.Int64Value)
                                {
                                    cached.Int64Value = cur;
                                    cached.Value = cur;
                                }
                            }
                        }
                        continue;
                    }

                    if (autonumberProp.ScalarKind == ScalarPropertyKind.Int64 &&
                        autonumberProp.LongGetter != null &&
                        autonumberProp.LongSetter != null)
                    {
                        long cur = autonumberProp.LongGetter(item);
                        if (cur == 0L)
                        {
                            long next = NextInt64(type, autonumberProp);
                            autonumberProp.LongSetter(item, next);
                        }
                        else
                        {
                            lock (syncLock)
                            {
                                var cached = FindCacheUnlocked(type, autonumberProp.PropertyName);
                                if (cached == null)
                                {
                                    items.Add(new AutonumberCache
                                    {
                                        Type = type,
                                        PropertyName = autonumberProp.PropertyName,
                                        Int64Value = cur,
                                        Value = cur
                                    });
                                }
                                else if (cur > cached.Int64Value)
                                {
                                    cached.Int64Value = cur;
                                    cached.Value = cur;
                                }
                            }
                        }
                        continue;
                    }

                    if (autonumberProp.ScalarKind == ScalarPropertyKind.Guid &&
                        autonumberProp.GuidGetter != null &&
                        autonumberProp.GuidSetter != null)
                    {
                        Guid cur = autonumberProp.GuidGetter(item);
                        if (cur == Guid.Empty)
                        {
                            autonumberProp.GuidSetter(item, Guid.NewGuid());
                        }
                        continue;
                    }

                    AutonumberFallback(type, item, autonumberProp);
                }
                return null;
            }

            foreach (var autonumberProp in propertyService.Properties(type.FullName).Where(s => s.IsAutonumber))
            {
                AutonumberFallback(type, item, autonumberProp);
            }

            return null;
        }

        private void AutonumberFallback(Type type, object item, PropertyInfoItem autonumberProp)
        {
            Type underlying = Nullable.GetUnderlyingType(autonumberProp.PropertyType) ?? autonumberProp.PropertyType;
            if (!ValueHelper.IsNumeric(underlying) && underlying != typeof(Guid))
                exceptionService.Throw(new AutonumberDataTypeException());

            object currentVal = autonumberProp.GetValue(item);
            object defaultVal = ValueHelper.DefaultOf(autonumberProp.PropertyType);

            if (currentVal == null || currentVal.Equals(defaultVal))
            {
                if (underlying != typeof(Guid))
                {
                    dynamic next = NextNumber(type, autonumberProp);
                    autonumberProp.SetValue(item, next);
                }
                else
                {
                    autonumberProp.SetValue(item, Guid.NewGuid());
                }
            }
            else if (ValueHelper.IsNumeric(underlying))
            {
                lock (syncLock)
                {
                    var cached = FindCacheUnlocked(type, autonumberProp.PropertyName);
                    if (cached == null)
                    {
                        long i64 = currentVal is int i ? i : (currentVal is long l ? l : 0L);
                        items.Add(new AutonumberCache
                        {
                            Type = type,
                            PropertyName = autonumberProp.PropertyName,
                            Int64Value = i64,
                            Value = currentVal
                        });
                    }
                    else if (ValueHelper.CompareValues(currentVal, cached.Value) > 0)
                    {
                        if (currentVal is int i) cached.Int64Value = i;
                        else if (currentVal is long l) cached.Int64Value = l;
                        cached.Value = currentVal;
                    }
                }
            }
        }

        internal void ClearType(Type type)
        {
            lock (syncLock)
            {
                items.RemoveAll(s => s.Type == type);
            }
        }

        internal void Reset()
        {
            lock (syncLock)
            {
                items.Clear();
            }
        }

        public void Dispose()
        {
            Reset();
        }
    }
}
