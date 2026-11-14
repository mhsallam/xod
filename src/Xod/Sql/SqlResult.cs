using System;
using System.Collections.Generic;
using System.Linq;

namespace Xod
{
    /// <summary>
    /// Where <see cref="XodContext.Sql(string, object[])"/> takes a table's column types from (PLAN-045 D1, PLAN-060).
    /// </summary>
    public enum SqlSchemaSource
    {
        /// <summary>A table whose class the program has is typed from the class; any other from the schema its
        /// table file records (the manifest).</summary>
        Classes = 0,

        /// <summary>Every table from the schema its table file records, as <c>xod query</c> reads a database
        /// without the application's classes. A table that records none is read with every column as text.</summary>
        Recorded = 1,
    }

    /// <summary>A column of a SQL result: its name, and its SQL type (<c>INTEGER</c>, <c>TEXT</c>, ...).</summary>
    public sealed class SqlColumn
    {
        internal SqlColumn(string name, string type)
        {
            Name = name;
            Type = type;
        }

        public string Name { get; }

        /// <summary>One of <c>INTEGER</c>, <c>DOUBLE</c>, <c>DECIMAL</c>, <c>TEXT</c>, <c>BOOLEAN</c>, <c>UUID</c>,
        /// <c>TIMESTAMP</c>, <c>TIMESTAMPTZ</c>, <c>DATE</c>, <c>TIME</c>, <c>INTERVAL</c>, <c>MAP</c>, <c>ARRAY</c>
        /// (PLAN-045 section 4).</summary>
        public string Type { get; }

        public override string ToString() => Name + " " + Type;
    }

    /// <summary>Something a query read differently from what is stored, with its code (PLAN-045 section 9): an
    /// encrypted column read without its key (<c>XQ008</c>), a column read as text (<c>XQ013</c>), a stored
    /// value that doesn't parse as its column's type (<c>XQ014</c>).</summary>
    public sealed class SqlWarning
    {
        internal SqlWarning(string code, string message)
        {
            Code = code;
            Message = message;
        }

        public string Code { get; }
        public string Message { get; }

        public override string ToString() => Code + ": " + Message;
    }

    /// <summary>
    /// The answer to a query: its columns and rows. Each value is the .NET value of its column's SQL type:
    /// <c>long</c> (INTEGER), <c>double</c>, <c>decimal</c>, <c>string</c> (TEXT), <c>bool</c>, <c>Guid</c>,
    /// <c>DateTime</c> (TIMESTAMP), <c>DateTimeOffset</c>, <c>DateOnly</c>, <c>TimeOnly</c>, <c>TimeSpan</c>
    /// (INTERVAL), a read-only dictionary with text keys (MAP), a read-only list (ARRAY), or null.
    /// </summary>
    public sealed class SqlResult
    {
        internal SqlResult(IReadOnlyList<SqlColumn> columns, IReadOnlyList<object[]> rows, IReadOnlyList<SqlWarning> warnings)
        {
            Columns = columns;
            Rows = rows;
            Warnings = warnings;
        }

        public IReadOnlyList<SqlColumn> Columns { get; }
        public IReadOnlyList<object[]> Rows { get; }
        public IReadOnlyList<SqlWarning> Warnings { get; }

        /// <summary>Each row as a new <typeparamref name="T"/>: a column goes to the writable property of its name
        /// (any case), converted exactly (<c>XQ005</c> when it can't be, a NULL included where the property can't
        /// hold one). A column without a property is left out, and a property without a column keeps its value.</summary>
        internal IReadOnlyList<T> As<T>() where T : new()
        {
            var properties = typeof(T).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                .ToList();
            var targets = new System.Reflection.PropertyInfo[Columns.Count];
            for (int i = 0; i < Columns.Count; i++)
            {
                var hits = properties.Where(p => string.Equals(p.Name, Columns[i].Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count > 1)
                    throw new XodSqlException("XQ003", $"{typeof(T).Name} has two properties named {Columns[i].Name}");
                targets[i] = hits.FirstOrDefault();
            }
            for (int i = 0; i < targets.Length; i++)
                for (int j = i + 1; j < targets.Length; j++)
                    if (targets[i] != null && targets[i] == targets[j])
                        throw new XodSqlException("XQ003", $"two columns go to {typeof(T).Name}.{targets[i].Name}");

            var result = new List<T>(Rows.Count);
            foreach (var row in Rows)
            {
                var item = new T();
                for (int i = 0; i < targets.Length; i++)
                {
                    var p = targets[i];
                    if (p == null) continue;
                    if (!Sql.SqlTypes.TryToClr(row[i], p.PropertyType, out object value))
                        throw new XodSqlException("XQ005", row[i] == null
                            ? $"column {Columns[i].Name} is NULL, which {typeof(T).Name}.{p.Name} ({p.PropertyType.Name}) can't hold"
                            : $"column {Columns[i].Name} ({Columns[i].Type}) can't be put in {typeof(T).Name}.{p.Name} ({p.PropertyType.Name}) exactly");
                    p.SetValue(item, value);
                }
                result.Add(item);
            }
            return result;
        }
    }

    /// <summary>
    /// A query XOD SQL refuses, with its stable code (PLAN-045 section 9): <c>XQ001</c> syntax, <c>XQ002</c> unknown
    /// table, <c>XQ003</c> ambiguous name, <c>XQ004</c> unknown column, <c>XQ005</c> type, <c>XQ006</c> aggregate,
    /// <c>XQ007</c> overflow, <c>XQ010</c> division by zero, <c>XQ011</c> a scalar subquery with more than one row,
    /// <c>XQ012</c> parameter. A syntax error has its line and column (from 1); others have 0.
    /// </summary>
    public class XodSqlException : XodException
    {
        public XodSqlException(string code, string message, int line = 0, int column = 0)
            : base(line > 0 ? $"{code}: {message} (line {line}, column {column})" : $"{code}: {message}")
        {
            Code = code;
            Line = line;
            Column = column;
        }

        public string Code { get; }
        public int Line { get; }
        public int Column { get; }
    }
}
