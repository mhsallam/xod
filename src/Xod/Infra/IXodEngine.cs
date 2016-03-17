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


        IEnumerable<object> Select(Type type, bool backward = false, bool lazyLoad = false);
        IEnumerable<object> Query(Type type, Func<dynamic, bool> query, bool lazyLoad = false);
        IEnumerable<object> QueryByExample(Type type, object example, bool lazyLoad = false);
        IEnumerable<object> QueryByExample(Type type, object[] examples, bool lazyLoad = false);
        object FirstMatch(Type type, Func<dynamic, bool> query, bool lazyLoad = false);
        object LastMatch(Type type, Func<dynamic, bool> query, bool lazyLoad = false);
        object First(Type type, bool lazyLoad = false);
        object Last(Type type, bool lazyLoad = false);
        
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

        void Dispose();
    }
}
