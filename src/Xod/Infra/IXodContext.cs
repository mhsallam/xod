using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;

namespace Xod.Infra
{
    public interface IXodContext : IDisposable
    {
        event EventHandler<TriggerEventArgs> BeforeAction;
        event EventHandler<TriggerEventArgs> AfterAction;

        IEnumerable<T> Select<T>(string include = "*");
        IEnumerable<T> Select<T>(int skip, int take, bool backward = false, string include = "*");
        IEnumerable<T> SelectBackward<T>(string include = "*");
        IEnumerable<T> Query<T>(Expression<Func<T, bool>> query, string include = "*");
        IEnumerable<T> QueryDelegate<T>(Func<T, bool> query, string include = "*");
        IEnumerable<T> Query<T>(T example, string include = "*");
        IEnumerable<T> Query<T>(T[] examples, string include = "*");

        T Find<T>(Expression<Func<T, bool>> query = null, string include = "*");
        T FindDelegate<T>(Func<T, bool> query, string include = "*");
        T FindLast<T>(Func<T, bool> query = null, string include = "*");
        T First<T>(string include = "*");
        T Last<T>(string include = "*");
        T FirstMatch<T>(Func<T, bool> query, string include = "*");
        int Count<T>(Func<T, bool> query = null);
        bool Any<T>(Func<T, bool> query = null);

        object Insert(object item, bool lazyLoad = false);
        T Insert<T>(T item, bool lazyLoad = false);
        object Insert(Type type, object item, bool lazyLoad = false);
        int InsertRange<T>(IEnumerable<T> items, bool lazyLoad = false);
        object InsertOrUpdate(object item, bool lazyLoad = false, UpdateFilter filter = null);
        T InsertOrUpdate<T>(T item, bool lazyLoad = false, UpdateFilter filter = null);
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
        int DeleteWhere<T>(Func<T, bool> predicate);
        bool Drop<T>();
        bool Drop(Type type);
        bool DropAll();

        // Async APIs
        Task<IReadOnlyList<T>> SelectAsync<T>(string include = "*", CancellationToken cancellationToken = default);
        Task<IReadOnlyList<T>> QueryAsync<T>(Expression<Func<T, bool>> query, string include = "*", CancellationToken cancellationToken = default);
        Task<T> FindAsync<T>(Expression<Func<T, bool>> query, string include = "*", CancellationToken cancellationToken = default);
        Task<T> InsertAsync<T>(T item, bool lazyLoad = false, CancellationToken cancellationToken = default);
        Task<bool> UpdateAsync<T>(T item, UpdateFilter filter = null, CancellationToken cancellationToken = default);
        Task<bool> DeleteAsync<T>(T item, CancellationToken cancellationToken = default);

        // Transactions & Diagnostics
        IXodTransaction BeginTransaction();
        DatabaseStatistics GetStatistics();

        IEnumerable<Type> RegisteredTypes();
        void RegisterType(Type type);
        void RegisterType<T>();

        void Secure(string password);
        void Loose(string password);
        void ChangePassword(string currentPassword, string newPassword);
        void Flush();
        void ClearCache();
        void ClearCaches();
    }
}
