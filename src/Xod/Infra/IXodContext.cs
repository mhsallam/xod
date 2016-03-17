using System;
using System.Collections.Generic;
namespace Xod.Infra
{
    interface IXodContext: IDisposable
    {
        event EventHandler<TriggerEventArgs> BeforeAction;
        event EventHandler<TriggerEventArgs> AfterAction;

        IEnumerable<T> Select<T>(bool lazyLoad = false);
        IEnumerable<T> SelectBackward<T>(bool lazyLoad = false);
        IEnumerable<T> Query<T>(Func<dynamic, bool> query, bool lazyLoad = false);
        IEnumerable<T> Query<T>(T example, bool lazyLoad = false);
        IEnumerable<T> Query<T>(T[] examples, bool lazyLoad = false);

        T FirstMatch<T>(Func<dynamic, bool> query = null, bool lazyLoad = false);
        T LastMatch<T>(Func<dynamic, bool> query = null, bool lazyLoad = false);
        T First<T>(bool lazyLoad = false);
        T Last<T>(bool lazyLoad = false);
        
        object Insert(object item, bool lazyLoad = false);
        object Insert<T>(T item, bool lazyLoad = false);
        object Insert(Type type, object item, bool lazyLoad = false);
        object InsertOrUpdate(object item, bool lazyLoad = false, UpdateFilter filter = null);
        object InsertOrUpdate<T>(T item, bool lazyLoad = false, UpdateFilter filter = null);
        object InsertOrUpdate(Type type, object item, bool lazyLoad = false, UpdateFilter filter = null);
        bool Update(object item, UpdateFilter filter = null);
        bool Update(object item, object newItem, UpdateFilter filter = null);
        bool Update(Type type, object item, UpdateFilter filter = null);
        bool Update(Type type, object item, object newItem, UpdateFilter filter = null);
        bool Update<T>(T item, UpdateFilter filter = null);
        bool Update<T>(T item, T newItem, UpdateFilter filter = null);
        bool Delete(object item);
        bool Delete(Type type, object item);
        bool Delete<T>(T item);
        bool Drop<T>();
        bool Drop(Type type);
        bool DropAll();
        
        IEnumerable<Type> RegisteredTypes();
        void RegisterType(Type type);
        void RegisterType<T>();
        
        void Secure(string password);
        void Loose(string password);
        void ChangePassword(string currentPassword, string newPassword);
        void ClearCache();
        void ClearCaches();
        
        void Dispose();
    }
}
