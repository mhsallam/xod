using System;
using System.Collections.Generic;
namespace Xod
{
    public interface IXodEngine: IDisposable
    {
        string Path { get; }
        bool IsNew { get; }
        bool LazyLoad { get; set; }

        event EventHandler<TriggerEventArgs> BeforeAction;
        event EventHandler<TriggerEventArgs> AfterAction;


        IEnumerable<object> Select(Type type, bool backward = false, string include = "*");
        IEnumerable<object> Query(Type type, Func<dynamic, bool> query, string include = "*");
        IEnumerable<object> QueryByExample(Type type, object example, string include = "*");
        IEnumerable<object> QueryByExample(Type type, object[] examples, string include = "*");
        object Find(Type type, Func<dynamic, bool> query, string include = "*");
        object FindLast(Type type, Func<dynamic, bool> query, string include = "*");
        object First(Type type, string include = "*");
        object Last(Type type, string include = "*");
        
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
        void ClearCache();
        void ClearCaches();
    }
}