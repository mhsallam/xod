using System;
using System.Collections.Generic;
using System.Linq;

namespace Xod
{
    ///<summary>
    ///Xml based relational OOP database
    ///</summary>
    public class XodContext : Xod.Infra.IXodContext
    {
        private IXodEngine engine;

        public XodContext(string file, string password = null, DatabaseOptions options = null)
        {
            this.engine = new Xod.Engines.Xml.XmlEngine(file, password, options);
        }

        public XodContext(IXodEngine engine)
        {
            this.engine = engine;
        }


        #region IXodContext Implementation

        public event EventHandler<TriggerEventArgs> BeforeAction
        {
            add { this.engine.BeforeAction += value; }
            remove { this.engine.BeforeAction -= value; }
        }
        public event EventHandler<TriggerEventArgs> AfterAction
        {
            add { this.engine.AfterAction += value; }
            remove { this.engine.AfterAction -= value; }
        }

        public IEnumerable<T> Select<T>(string include = "*")
        {
            Type type = typeof(T);
            return this.engine.Select(type, false, include).Cast<T>();
        }
        public IEnumerable<T> SelectBackward<T>(string include = "*")
        {
            Type type = typeof(T);
            return this.engine.Select(type, true, include).Cast<T>();
        }
        public IEnumerable<T> Query<T>(Func<dynamic, bool> query, string include = "*")
        {
            Type type = typeof(T);
            return this.engine.Query(type, query as Func<object, bool>, include).Cast<T>();
        }
        public IEnumerable<T> Query<T>(T example, string include = "*")
        {
            Type type = typeof(T);
            return this.engine.QueryByExample(type, example, include).Cast<T>();
        }
        public IEnumerable<T> Query<T>(T[] examples, string include = "*")
        {
            Type type = typeof(T);
            return this.engine.QueryByExample(type, examples, include).Cast<T>();
        }

        public T Find<T>(Func<dynamic, bool> query = null, string include = "*")
        {
            Type type = typeof(T);
            return (T)this.engine.Find(type, query, include);
        }
        public T FindLast<T>(Func<dynamic, bool> query = null, string include = "*")
        {
            Type type = typeof(T);
            return (T)this.engine.FindLast(type, query as Func<object, bool>, include);
        }
        public T First<T>(string include = "*")
        {
            Type type = typeof(T);
            return (T)this.engine.First(type, include);
        }
        public T Last<T>(string include = "*")
        {
            Type type = typeof(T);
            return (T)this.engine.Last(type, include);
        }

        public object Insert(object item, bool lazyLoad = false)
        {
            return this.engine.Insert(item.GetActualType(), item, lazyLoad);
        }
        public object Insert<T>(T item, bool lazyLoad = false)
        {
            Type type = typeof(T);
            return this.engine.Insert(type, item, lazyLoad);
        }
        public object Insert(Type type, object item, bool lazyLoad = false)
        {
            return this.engine.Insert(type, item, lazyLoad);
        }
        public bool Update<T>(T item, T newItem, UpdateFilter filter = null)
        {
            Type type = typeof(T);
            return this.engine.Update(type, item, newItem, filter);
        }
        public bool Update(Type type, object item, object newItem, UpdateFilter filter = null)
        {
            return this.engine.Update(type, item, newItem, filter);
        }
        public bool Update(object item, object newItem, UpdateFilter filter = null)
        {
            return this.engine.Update(item.GetActualType(), item, newItem, filter);
        }
        public bool Update<T>(T item, UpdateFilter filter = null)
        {
            Type type = typeof(T);
            return this.engine.Update(type, item, filter);
        }
        public bool Update(Type type, object item, UpdateFilter filter = null)
        {
            return this.engine.Update(type, item, filter);
        }
        public bool Update(object item, UpdateFilter filter = null)
        {
            return this.engine.Update(item.GetActualType(), item, filter);
        }
        public object InsertOrUpdate(object item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            return this.engine.InsertOrUpdate(item.GetActualType(), item, lazyLoad, filter);
        }
        public object InsertOrUpdate<T>(T item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            Type type = typeof(T);
            return this.engine.InsertOrUpdate(type, item, lazyLoad, filter);
        }
        public object InsertOrUpdate(Type type, object item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            return this.engine.InsertOrUpdate(type, item, lazyLoad, filter);
        }
        public bool Delete<T>(T item)
        {
            return this.engine.Delete(typeof(T), item);
        }
        public bool Delete(Type type, object item)
        {
            return this.engine.Delete(type, item);
        }
        public bool Delete(object item)
        {
            Type type = item.GetActualType();
            return this.engine.Delete(type, item);
        }
        public bool Drop<T>()
        {
            Type type = typeof(T);
            return this.engine.DropType(type);
        }
        public bool Drop(Type type)
        {
            return this.engine.DropType(type);
        }
        public bool DropAll()
        {
            var types = this.engine.RegisteredTypes();
            foreach (var type in types)
            {
                if (!this.engine.DropType(type))
                    return false;
            }
            return true;
        }

        public void Secure(string password)
        {
            this.engine.Secure(password);
        }
        public void Loose(string password)
        {
            this.engine.Loose(password);
        }
        public void ChangePassword(string currentPassword, string newPassword)
        {
            this.engine.ChangePassword(currentPassword, newPassword);
        }

        public void RegisterType<T>()
        {
            Type type = typeof(T);
            this.engine.RegisterType(type);
        }
        public void RegisterType(Type type)
        {
            this.engine.RegisterType(type);
        }
        public IEnumerable<Type> RegisteredTypes()
        {
            return this.engine.RegisteredTypes();
        }

        public void ClearCache()
        {
            this.engine.ClearCache();
        }

        public void ClearCaches()
        {
            this.engine.ClearCaches();
        }

        public void Dispose()
        {
            this.engine.Dispose();
        }

        #endregion

    }
}