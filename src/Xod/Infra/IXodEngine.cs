using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Xod.Infra;

namespace Xod
{
    public interface IXodEngine : IDisposable
    {
        string Path { get; }
        bool IsNew { get; }
        bool LazyLoad { get; set; }
        StorageFormat StorageFormat { get; }

        event EventHandler<TriggerEventArgs> BeforeAction;
        event EventHandler<TriggerEventArgs> AfterAction;

        IEnumerable<object> Select(Type type, bool backward = false, string include = "*");
        IEnumerable<object> Select(Type type, int skip, int take, bool backward = false, string include = "*");
        IEnumerable<object> Query(Type type, Func<dynamic, bool> query, string include = "*");
        IEnumerable<T> QueryExpression<T>(Expression<Func<T, bool>> expression, string include = "*");
        IEnumerable<object> QueryByExample(Type type, object example, string include = "*");
        IEnumerable<object> QueryByExample(Type type, object[] examples, string include = "*");
        object Find(Type type, Func<dynamic, bool> query, string include = "*");
        object FindLast(Type type, Func<dynamic, bool> query, string include = "*");
        object First(Type type, string include = "*");
        object Last(Type type, string include = "*");
        int Count(Type type);

        object Insert(Type type, object item, bool lazyLoad = false);
        int InsertRange(Type type, IEnumerable<object> items, bool lazyLoad = false);
        object InsertOrUpdate(Type type, object item, bool lazyLoad = false, UpdateFilter filter = null);

        bool Update(Type type, object item, object newItem, UpdateFilter filter = null);
        bool Update(Type type, object item, UpdateFilter filter = null);

        bool Delete(Type type, object item);

        bool DropType(Type type);

        /// <summary>
        /// Deletes the rows of <paramref name="type"/> the predicate matches, and returns how many were deleted. XOD's
        /// engines make it one write (ISS-176): a delete refused part way leaves every match. This default, for an
        /// engine with no such write, deletes the matches one by one.
        /// </summary>
        int DeleteWhere(Type type, Func<object, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            int deleted = 0;
            foreach (var match in Query(type, predicate, "*").ToList())
            {
                if (Delete(match.GetActualType(), match))
                    deleted++;
            }
            return deleted;
        }

        /// <summary>
        /// Drops every registered type, and returns whether each was dropped. XOD's engines make it one write
        /// (ISS-176): a drop refused part way leaves every table. This default, for an engine with no such write, drops
        /// the types one by one.
        /// </summary>
        bool DropAll()
        {
            bool allDropped = true;
            foreach (var type in RegisteredTypes().ToList())
            {
                if (!DropType(type))
                    allDropped = false;
            }
            return allDropped;
        }

        IXodTransaction BeginTransaction();
        DatabaseStatistics GetStatistics();

        void ChangePassword(string currentPassword, string newPassword);
        void Loose(string password);
        void Secure(string password);

        IEnumerable<Type> RegisteredTypes();
        void RegisterType(Type type);
        void Flush();
        void ClearCache();
        void ClearCaches();
    }
}
