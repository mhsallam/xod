using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xod.Helpers;

namespace Xod.Services
{
    //shared service: thread-safe for multiple databases and connections
    internal class AutonumberService : ICachedList<AutonumberCache>
    {
        private static readonly object locker = new object();
        private static Dictionary<string, object> lockers = null;
        private static Dictionary<string, List<AutonumberCache>> items = null;
        private static int instances = 0;

        string path = null;

        PropertyService propertyService = null;
        IOService ioService = null;
        ExceptionService exceptionService = null;
        Func<Type, object> findLastDelegate = null;

        internal Func<Type, object> FindLastDelegate
        {
            set { findLastDelegate = value; }
        }

        internal AutonumberService(string path, PropertyService propertyService, IOService ioService, ExceptionService exceptionService)
        {
            this.path = path.ToLower();
            this.propertyService = propertyService;
            this.ioService = ioService;
            this.exceptionService = exceptionService;

            //double-checking lock pattern; optimized thread-safe
            if (items == null)
            {
                lock (locker)
                {
                    if (items == null)
                    {
                        items = new Dictionary<string, List<AutonumberCache>>();
                        lockers = new Dictionary<string, object>();
                    }
                }
            }

            if (!items.ContainsKey(this.path))
            {
                lock (locker)
                {
                    if (!items.ContainsKey(this.path))
                    {
                        items.Add(this.path, new List<AutonumberCache>());
                        lockers.Add(this.path, new object());
                    }
                }
            }
            instances++;
        }

        public List<AutonumberCache> GetItems()
        {
            //no need for lock(): this is index-based item fitching, and the
            //item existance is garanteed through the life of this service instance
            return items[this.path];
        }

        private bool Contains(Type type, string propertyName)
        {
            //no new item should be added while querying
            lock (GetLock())
            {
                return this.GetItems().Any(s => s != null && s.Type == type && s.PropertyName == propertyName);
            }
        }

        private object GetLock()
        {
            //no need for lock(): this is index-based item fitching, and the
            //item existance is garanteed through the life of this service instance
            return lockers[this.path];
        }
        
        private dynamic GetNext(Type type, string propertyName, dynamic increment)
        {
            var variable = this.GetItems().FirstOrDefault(s => s.Type == type && s.PropertyName == propertyName);
            if (variable != null)
            {
                variable.Value = variable.Method.Invoke(
                        null,
                        new object[] {
                            new object[]
                            {
                                variable.Value,
                                increment
                            }});
                return variable.Value;
            }

            return Xod.Helpers.ValueHelper.DefaultOf(type.GetProperty(propertyName).PropertyType);
        }

        //no need for lock, it has been called by lock-enabled function
        private void Set(Type type, string propertyName, MethodInfo method, dynamic value)
        {
            if (this.Contains(type, propertyName))
            {
                this.Update(type, propertyName, value);
            }
            else
            {
                lock (GetLock())
                {
                    this.GetItems().Add(new AutonumberCache()
                    {
                        PropertyName = propertyName,
                        Value = value,
                        Type = type,
                        Method = method
                    });
                }
            }
        }

        //no need for lock, it is been called by lock-enabled function
        //plus no add or remove items operations is in here
        private void Update(Type type, string propertyName, dynamic value)
        {
            var variable = GetItems().FirstOrDefault(s => s.Type == type && s.PropertyName == propertyName);
            if (variable != null)
                variable.Value = value;
        }

        private dynamic NextNumber(Type type, PropertyInfoItem prop)
        {
            dynamic defValue = ValueHelper.DefaultOf(prop.PropertyType);
            if (!prop.IsAutonumber)
                return defValue;

            dynamic value = defValue;
            
            lock (GetLock())
            {
                if (this.Contains(type, prop.PropertyName))
                {
                    value = this.GetNext(type, prop.PropertyName, prop.IdentityIncrement);
                    if (value < prop.IdentitySeed)
                    {
                        value = prop.IdentitySeed;
                        this.Update(type, prop.PropertyName, value);
                    }
                }
                else if (this.findLastDelegate != null)
                {
                    MethodInfo numOpsSumGenericMethod = typeof(NumericOperations)
                        .GetMethod("Sum")
                        .MakeGenericMethod(new Type[] { prop.PropertyType });

                    var last = this.findLastDelegate(type);
                    if (last == null)
                        value = prop.IdentitySeed;
                    else
                    {
                        value = numOpsSumGenericMethod.Invoke(
                            null,
                            new object[] {
                            new object[]
                            {
                                prop.Property.GetValue(last),
                                prop.IdentityIncrement
                            }});
                    }
                    this.Set(type, prop.PropertyName, numOpsSumGenericMethod, value);
                }
            }
            return value;
        }

        internal Dictionary<string, dynamic> Autonumber(Type type, object item)
        {
            Dictionary<string, dynamic> autonumbers = new Dictionary<string, dynamic>();
            List<string> autonumberedProps = new List<string>();
            var autonumberProps = this.propertyService.Properties(type.FullName).Where(s => s.IsAutonumber);
            foreach (var autonumberProp in autonumberProps)
            {
                if (!ValueHelper.IsNumeric(autonumberProp.PropertyType) && !autonumberProp.PropertyType.Equals(typeof(Guid)))
                    exceptionService.Throw(new AutonumberDataTypeException());

                if (ValueHelper.DefaultOf(autonumberProp.PropertyType).Equals(autonumberProp.Property.GetValue(item)))
                {
                    dynamic next = null;
                    if (!autonumberProp.PropertyType.Equals(typeof(Guid)))
                    {
                        //lock() is implemeneted in NextNumber()
                        next = NextNumber(type, autonumberProp);
                        autonumberProp.Property.SetValue(item, next);
                        autonumberedProps.Add(autonumberProp.Property.Name);
                    }
                    else
                    {
                        next = Guid.NewGuid();
                        autonumberProp.Property.SetValue(item, next);
                    }
                    autonumbers.Add(autonumberProp.PropertyName, next);
                }
            }

            return autonumbers;
        }

        internal void ClearType(Type type)
        {
            //lock (lockers[this.path])
            //no read or add operations should be allowed when removing items
            lock (GetLock())
            {
                GetItems().RemoveAll(s => s.Type == type);
            }
        }

        //if there is no instances from this service clear shared resources
        public void Dispose()
        {
            instances--;

            if(instances <= 0)
            {
                lock (locker)
                {
                    lockers = null;
                    items = null;
                    instances = 0;
                }
            }
        }
    }
}
