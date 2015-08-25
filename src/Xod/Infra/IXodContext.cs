using System;
using System.Collections.Generic;
namespace AcesDevelopers.Xod.Infra
{
    interface IXodContext
    {
        event EventHandler<TriggerEventArgs> BeforeAction;
        event EventHandler<TriggerEventArgs> AfterAction;

        IEnumerable<T> Select<T>(bool lazyLoad = false);
        IEnumerable<object> Select(Type type, bool lazyLoad = false);
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
        
        void Dispose();
    }
}
