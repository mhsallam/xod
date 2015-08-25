using System;
using System.Collections.Generic;
namespace AcesDevelopers.Xod
{
    public interface IXodEngine
    {
        string Path { get; }
        bool IsNew { get; }
        bool LazyLoad { get; set; }

        event EventHandler<TriggerEventArgs> BeforeAction;
        event EventHandler<TriggerEventArgs> AfterAction;

        IEnumerable<object> Select(Type type, bool lazyLoad = false);

        object Insert(Type type, object item, bool lazyLoad = false);
        object InsertOrUpdate(Type type, object item, bool lazyLoad = false, UpdateFilter filter = null);

        bool Update(Type type, object item, object newItem, UpdateFilter filter = null);
        bool Update(Type type, object item, UpdateFilter filter = null);

        bool Delete(Type type, object item);

        bool DropType(Type type);

        void ChangePassword(string currentPassword, string newPassword);
        void Loose(string password);
        void Secure(string password);

        IEnumerable<Type> RegisteredTypes();
        void RegisterType(Type type);

        void Dispose();
    }
}
