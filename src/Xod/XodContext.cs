using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Xod.Engines.Binary;
using Xod.Engines.Json;
using Xod.Engines.Xml;
using Xod.Infra;

namespace Xod
{
    /// <summary>
    /// XOD++ (ZOD) Object-Oriented Relational Embedded Database Context.
    /// Supports XML, JSON and Binary storage engines, primary, unique and secondary indexes, transactions, SQL SELECT and AES-256-GCM property encryption.
    /// </summary>
    public class XodContext : IXodContext
    {
        private readonly IXodEngine engine;

        public XodContext(string file, string password = null, DatabaseOptions options = null)
        {
            // A format given is the one written; otherwise the engine takes the database's own (ISS-093: a database
            // converted to Binary was written in the encoding its extension picks).
            if (options == null || !options.StorageFormatSet)
            {
                this.engine = new XmlEngine(file, password, options);
                return;
            }
            this.engine = options.StorageFormat switch
            {
                StorageFormat.Json => new JsonEngine(file, password, options),
                StorageFormat.Binary => new BinaryEngine(file, password, options),
                _ => new XmlEngine(file, password, options)
            };
        }

        public XodContext(IXodEngine engine)
        {
            this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public IXodEngine Engine => engine;
        public StorageFormat StorageFormat => engine.StorageFormat;

        #region IXodContext Implementation

        public event EventHandler<TriggerEventArgs> BeforeAction
        {
            add { engine.BeforeAction += value; }
            remove { engine.BeforeAction -= value; }
        }

        public event EventHandler<TriggerEventArgs> AfterAction
        {
            add { engine.AfterAction += value; }
            remove { engine.AfterAction -= value; }
        }

        public IEnumerable<T> Select<T>(string include = "*")
        {
            return engine.Select(typeof(T), false, include).Cast<T>();
        }

        public IEnumerable<T> Select<T>(int skip, int take, bool backward = false, string include = "*")
        {
            return engine.Select(typeof(T), skip, take, backward, include).Cast<T>();
        }

        public IEnumerable<T> SelectBackward<T>(string include = "*")
        {
            return engine.Select(typeof(T), true, include).Cast<T>();
        }

        public IEnumerable<T> Query<T>(Expression<Func<T, bool>> query, string include = "*")
        {
            return engine.QueryExpression(query, include);
        }

        public IEnumerable<T> QueryDelegate<T>(Func<T, bool> query, string include = "*")
        {
            if (query == null)
                return Select<T>(include);

            return engine.Query(typeof(T), obj => obj is T typed && query(typed), include).Cast<T>();
        }

        public IEnumerable<T> Query<T>(T example, string include = "*")
        {
            return engine.QueryByExample(typeof(T), example, include).Cast<T>();
        }

        public IEnumerable<T> Query<T>(T[] examples, string include = "*")
        {
            return engine.QueryByExample(typeof(T), examples?.Cast<object>().ToArray(), include).Cast<T>();
        }

        public T Find<T>(Expression<Func<T, bool>> query = null, string include = "*")
        {
            if (query == null)
                return First<T>(include);

            return engine.QueryExpression(query, include).FirstOrDefault();
        }

        public T FindDelegate<T>(Func<T, bool> query, string include = "*")
        {
            Func<dynamic, bool> wrapped = query != null ? (obj => obj is T typed && query(typed)) : null;
            return (T)engine.Find(typeof(T), wrapped, include);
        }

        public T FirstMatch<T>(Func<T, bool> query, string include = "*")
        {
            return FindDelegate(query, include);
        }

        public T FindLast<T>(Func<T, bool> query = null, string include = "*")
        {
            Func<dynamic, bool> wrapped = query != null ? (obj => obj is T typed && query(typed)) : null;
            return (T)engine.FindLast(typeof(T), wrapped, include);
        }

        public T First<T>(string include = "*")
        {
            return (T)engine.First(typeof(T), include);
        }

        public T Last<T>(string include = "*")
        {
            return (T)engine.Last(typeof(T), include);
        }

        public int Count<T>(Func<T, bool> query = null)
        {
            return query == null ? engine.Count(typeof(T)) : QueryDelegate(query, "").Count();
        }

        public bool Any<T>(Func<T, bool> query = null)
        {
            return query == null ? First<T>("") != null : FindDelegate(query, "") != null;
        }

        public object Insert(object item, bool lazyLoad = false)
        {
            return engine.Insert(item.GetActualType(), item, lazyLoad);
        }

        public T Insert<T>(T item, bool lazyLoad = false)
        {
            engine.Insert(typeof(T), item, lazyLoad);
            return item;
        }

        public object Insert(Type type, object item, bool lazyLoad = false)
        {
            return engine.Insert(type, item, lazyLoad);
        }

        public int InsertRange<T>(IEnumerable<T> items, bool lazyLoad = false)
        {
            if (items == null) return 0;
            return engine.InsertRange(typeof(T), items.Cast<object>(), lazyLoad);
        }

        public bool Update<T>(T item, T newItem, UpdateFilter filter = null)
        {
            return engine.Update(typeof(T), item, newItem, filter);
        }

        public bool Update(Type type, object item, object newItem, UpdateFilter filter = null)
        {
            return engine.Update(type, item, newItem, filter);
        }

        public bool Update(object item, object newItem, UpdateFilter filter = null)
        {
            return engine.Update(item.GetActualType(), item, newItem, filter);
        }

        public bool Update<T>(T item, UpdateFilter filter = null)
        {
            return engine.Update(typeof(T), item, filter);
        }

        public bool Update(Type type, object item, UpdateFilter filter = null)
        {
            return engine.Update(type, item, filter);
        }

        public bool Update(object item, UpdateFilter filter = null)
        {
            return engine.Update(item.GetActualType(), item, filter);
        }

        public object InsertOrUpdate(object item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            return engine.InsertOrUpdate(item.GetActualType(), item, lazyLoad, filter);
        }

        public T InsertOrUpdate<T>(T item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            engine.InsertOrUpdate(typeof(T), item, lazyLoad, filter);
            return item;
        }

        public object InsertOrUpdate(Type type, object item, bool lazyLoad = false, UpdateFilter filter = null)
        {
            return engine.InsertOrUpdate(type, item, lazyLoad, filter);
        }

        public bool Delete<T>(T item)
        {
            return engine.Delete(typeof(T), item);
        }

        public bool Delete(Type type, object item)
        {
            return engine.Delete(type, item);
        }

        public bool Delete(object item)
        {
            return engine.Delete(item.GetActualType(), item);
        }

        public int DeleteWhere<T>(Func<T, bool> predicate)
        {
            if (predicate == null) throw new ArgumentNullException(nameof(predicate));
            return engine.DeleteWhere(typeof(T), item => item is T typed && predicate(typed));
        }

        public bool Drop<T>()
        {
            return engine.DropType(typeof(T));
        }

        public bool Drop(Type type)
        {
            return engine.DropType(type);
        }

        public bool DropAll()
        {
            return engine.DropAll();
        }

        public Task<IReadOnlyList<T>> SelectAsync<T>(string include = "*", CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<T>>(() => Select<T>(include).ToList(), cancellationToken);
        }

        public Task<IReadOnlyList<T>> QueryAsync<T>(Expression<Func<T, bool>> query, string include = "*", CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<T>>(() => Query(query, include).ToList(), cancellationToken);
        }

        public Task<T> FindAsync<T>(Expression<Func<T, bool>> query, string include = "*", CancellationToken cancellationToken = default)
        {
            return Task.Run(() => Find(query, include), cancellationToken);
        }

        public Task<T> InsertAsync<T>(T item, bool lazyLoad = false, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => Insert(item, lazyLoad), cancellationToken);
        }

        public Task<bool> UpdateAsync<T>(T item, UpdateFilter filter = null, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => Update(item, filter), cancellationToken);
        }

        public Task<bool> DeleteAsync<T>(T item, CancellationToken cancellationToken = default)
        {
            return Task.Run(() => Delete(item), cancellationToken);
        }

        /// <summary>
        /// Runs a XOD SQL query (PLAN-045): a <c>SELECT</c>, or <c>EXPLAIN SELECT</c>. Values are passed as
        /// parameters, never built into the text: <c>?</c> takes <paramref name="args"/> in order.
        /// </summary>
        /// <exception cref="XodSqlException">The query doesn't parse, names what the database doesn't have, or
        /// fails while it runs; <see cref="XodSqlException.Code"/> says which.</exception>
        public SqlResult Sql(string sql, params object[] args)
        {
            return SqlEngine().Sql(sql, args, null);
        }

        /// <summary>Runs a XOD SQL query whose parameters are named (<c>@name</c>), with their values by name
        /// (without the <c>@</c>, in any case).</summary>
        public SqlResult Sql(string sql, IReadOnlyDictionary<string, object> args)
        {
            return SqlEngine().Sql(sql, null, args);
        }

        /// <summary>Runs a XOD SQL query and makes a <typeparamref name="T"/> of each row: each column sets the
        /// property of its name (in any case), converted to the property's type.</summary>
        public IReadOnlyList<T> Sql<T>(string sql, params object[] args) where T : new()
        {
            return Sql(sql, args).As<T>();
        }

        /// <summary>Runs a XOD SQL query with named parameters and makes a <typeparamref name="T"/> of each row.</summary>
        public IReadOnlyList<T> Sql<T>(string sql, IReadOnlyDictionary<string, object> args) where T : new()
        {
            return Sql(sql, args).As<T>();
        }

        private XmlEngine SqlEngine() =>
            engine as XmlEngine ?? throw new NotSupportedException("SQL runs on XOD's own engines (XML, JSON, Binary).");

        /// <summary>
        /// Begins a transaction, which belongs to the code that calls this: its thread, and what it awaits or runs with
        /// <c>Task.Run</c> (the <c>*Async</c> methods among them). Its reads and writes are the transaction's: they see
        /// its writes. A read or a write anywhere else waits for the transaction to end, up to
        /// <see cref="DatabaseOptions.TransactionTimeoutMs"/>, and then goes on, or throws
        /// <see cref="TransactionException"/> with nothing done: it never sees, or joins, what the transaction has not
        /// committed. A second transaction, begun elsewhere, waits the same way, and so do <see cref="GetStatistics"/>,
        /// <c>Flush</c>, <c>ClearCache</c>, <c>DropType</c> and a password change. So keep transactions short: a long one
        /// holds up every other thread's reads.
        /// <para>
        /// Begun inside an async method, a transaction belongs to that method, not to its caller (an <c>AsyncLocal</c>
        /// set there doesn't reach the caller), so the caller's reads and writes wait for it: begin it where its work is
        /// done. Dispose it: one never disposed keeps every other read and write waiting until the context closes.
        /// </para>
        /// </summary>
        public IXodTransaction BeginTransaction()
        {
            return engine.BeginTransaction();
        }

        public DatabaseStatistics GetStatistics()
        {
            return engine.GetStatistics();
        }

        public void Secure(string password)
        {
            engine.Secure(password);
        }

        public void Loose(string password)
        {
            engine.Loose(password);
        }

        public void ChangePassword(string currentPassword, string newPassword)
        {
            engine.ChangePassword(currentPassword, newPassword);
        }

        public void RegisterType<T>()
        {
            engine.RegisterType(typeof(T));
        }

        public void RegisterType(Type type)
        {
            engine.RegisterType(type);
        }

        public IEnumerable<Type> RegisteredTypes()
        {
            return engine.RegisteredTypes();
        }

        public void Flush()
        {
            engine.Flush();
        }

        public void ClearCache()
        {
            engine.ClearCache();
        }

        public void ClearCaches()
        {
            engine.ClearCaches();
        }

        public void Dispose()
        {
            engine.Dispose();
        }

        #endregion
    }
}
