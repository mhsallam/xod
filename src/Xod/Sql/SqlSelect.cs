using System;
using System.Collections.Generic;
using System.Linq;

namespace Xod.Sql
{
    internal enum SourceKind { Table, Derived, Collection, Path }

    /// <summary>One source of a query's rows: a table, a subquery in FROM, a children collection, or (hidden) the
    /// row a to-one path leads to. Each has a slot in the query's rows.</summary>
    internal sealed class Source
    {
        public SourceKind Kind { get; init; }
        public int Slot { get; init; }
        /// <summary>The name the query refers to it by: its alias, or the table's name as written.</summary>
        public string Alias { get; init; }
        /// <summary>For a table without an alias: its short name, which also refers to it.</summary>
        public string AltName { get; init; }
        public TableSchema Schema { get; init; }
        public BoundSelect Derived { get; init; }
        public JoinKind Join { get; init; }
        public BExpr On { get; set; }
        /// <summary>A path's or a collection's parent: its slot, how many queries out it is, and the relation.</summary>
        public int ParentSlot { get; init; } = -1;
        public int ParentDepth { get; init; }
        public RelationSchema Relation { get; init; }
        public int RelationIndex { get; init; } = -1;
        /// <summary>A path or collection joined on the relationship's keys (both tables have them); otherwise on the
        /// item codes the parent row holds.</summary>
        public bool ByKeys { get; init; }
        /// <summary>The base source a path hangs from (itself for a base source), for join ordering.</summary>
        public int Owner { get; init; }
        public bool Hidden => Kind == SourceKind.Path;
        /// <summary>Paths from this source already given a slot, by relation name.</summary>
        public Dictionary<string, int> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Conditions of the WHERE an index may answer for this table (set when the query is bound).</summary>
        public List<(ColumnSchema Column, string Op, List<BExpr> Values)> IndexConditions { get; } = new();

        public int ColumnCount => Derived != null ? Derived.Output.Count : Schema.Columns.Count;

        public string ColumnName(int i) => Derived != null ? Derived.Output[i].Name : Schema.Columns[i].Name;

        public string Describe() => Kind switch
        {
            SourceKind.Derived => $"subquery {Alias}",
            _ => Schema.FullName == Alias || Schema.ShortName == Alias ? Schema.ShortName : $"{Schema.ShortName} {Alias}",
        };
    }

    internal sealed class OutputColumn
    {
        public string Name { get; init; }
        public BExpr Expr { get; init; }
        public SqlType Type => Expr.Type;
    }

    internal sealed class OrderKey
    {
        /// <summary>Evaluated where the select list is (a row, or a group); or null when <see cref="Output"/> names a
        /// column of the result.</summary>
        public BExpr Expr { get; init; }
        public int Output { get; init; } = -1;
        public bool Desc { get; init; }
        public bool NullsFirst { get; init; }
    }

    /// <summary>
    /// A bound query, ready to run: its sources (slots), WHERE, grouping, select list, order and limits. Rows are
    /// joined source by source in FROM order (hash on equality keys where the ON allows, nested otherwise), each path
    /// filled in as soon as its parent row is, then filtered, grouped, projected, made distinct, ordered (stably, so
    /// ties keep the order rows came in: stored order, D5) and cut.
    /// </summary>
    internal sealed class BoundSelect
    {
        public List<Source> Sources { get; } = new();
        public BExpr Where { get; set; }
        public List<BExpr> GroupBy { get; } = new();
        public bool Grouped { get; set; }
        public BExpr Having { get; set; }
        public List<OutputColumn> Output { get; } = new();
        public List<OrderKey> Order { get; } = new();
        public bool Distinct { get; set; }
        public BExpr Limit { get; set; }
        public BExpr Offset { get; set; }
        /// <summary>True when it reads a column of an enclosing query, so it runs again for each of that query's rows.</summary>
        public bool Correlated { get; set; }
        /// <summary>This query's columns its subqueries read (as this query sees them), the clause of this query each
        /// subquery sits in, and whether inside an aggregate: a grouped query checks them as its own.</summary>
        public List<(BColumn Column, string Clause, bool InAggregate)> OuterUses { get; } = new();

        private readonly object onceKey = new();

        /// <summary>The result rows, run once per query when uncorrelated.</summary>
        public IReadOnlyList<object[]> Rows(Env outer)
        {
            if (!Correlated)
                return outer.Exec.Once(onceKey, () => Run(outer));
            return Run(outer);
        }

        public IReadOnlyList<object[]> Run(Env outer)
        {
            var exec = outer.Exec;
            var env = new Env { Outer = outer, Exec = exec };
            int n = Sources.Count;

            // FROM: one combination of rows per result row so far.
            var rows = new List<SqlRow[]> { new SqlRow[n] };
            foreach (var source in Sources.Where(s => !s.Hidden))
                rows = Join(rows, source, env);

            // WHERE
            if (Where != null)
                rows = rows.Where(r => { env.Row = r; return Where.Truth(env) == true; }).ToList();

            // GROUP BY and HAVING, then the select list (with the order keys beside it).
            var produced = new List<(object[] Values, object[] Keys)>();
            if (Grouped)
            {
                var groups = new Dictionary<SqlValues.RowKey, List<SqlRow[]>>();
                var order = new List<List<SqlRow[]>>();
                if (GroupBy.Count == 0)
                {
                    order.Add(rows);
                }
                else
                {
                    foreach (var r in rows)
                    {
                        env.Row = r;
                        var key = new SqlValues.RowKey(GroupBy.Select(g => g.Eval(env)).ToArray());
                        if (!groups.TryGetValue(key, out var list))
                        {
                            groups[key] = list = new List<SqlRow[]>();
                            order.Add(list);
                        }
                        list.Add(r);
                    }
                }
                foreach (var group in order)
                {
                    env.Group = group;
                    env.Row = group.Count > 0 ? group[0] : new SqlRow[n];
                    if (Having != null && Having.Truth(env) != true) continue;
                    produced.Add(Project(env));
                }
                env.Group = null;
            }
            else
            {
                foreach (var r in rows)
                {
                    env.Row = r;
                    produced.Add(Project(env));
                }
            }

            if (Distinct)
            {
                var seen = new HashSet<SqlValues.RowKey>();
                produced = produced.Where(p => seen.Add(new SqlValues.RowKey(p.Values))).ToList();
            }

            if (Order.Count > 0)
            {
                var indexed = produced.Select((p, i) => (p, i)).ToList();
                indexed.Sort((a, b) =>
                {
                    for (int k = 0; k < Order.Count; k++)
                    {
                        var key = Order[k];
                        object x = key.Output >= 0 ? a.p.Values[key.Output] : a.p.Keys[k];
                        object y = key.Output >= 0 ? b.p.Values[key.Output] : b.p.Keys[k];
                        int c;
                        if (x == null || y == null)
                            c = x == null && y == null ? 0 : (x == null) == key.NullsFirst ? -1 : 1;
                        else
                            c = key.Desc ? -SqlValues.Compare(x, y) : SqlValues.Compare(x, y);
                        if (c != 0) return c;
                    }
                    return a.i.CompareTo(b.i);
                });
                produced = indexed.Select(x => x.p).ToList();
            }

            long offset = Count(Offset, env, "OFFSET") ?? 0;
            long? limit = Count(Limit, env, "LIMIT");
            IEnumerable<(object[] Values, object[] Keys)> cut = produced;
            if (offset > 0) cut = cut.Skip((int)Math.Min(offset, int.MaxValue));
            if (limit.HasValue) cut = cut.Take((int)Math.Min(limit.Value, int.MaxValue));
            return cut.Select(p => p.Values).ToList();
        }

        private (object[] Values, object[] Keys) Project(Env env)
        {
            var values = new object[Output.Count];
            for (int i = 0; i < values.Length; i++)
                values[i] = SqlValues.Widen(Output[i].Expr.Eval(env), Output[i].Type);
            object[] keys = null;
            if (Order.Count > 0)
            {
                keys = new object[Order.Count];
                for (int k = 0; k < keys.Length; k++)
                    if (Order[k].Expr != null) keys[k] = Order[k].Expr.Eval(env);
            }
            return (values, keys);
        }

        private static long? Count(BExpr e, Env env, string what)
        {
            if (e == null) return null;
            object v = e.Eval(env);
            if (v == null) return null;
            long n = (long)v;
            if (n < 0) throw new XodSqlException("XQ005", $"{what} is negative");
            return n;
        }

        // ---- FROM ----

        /// <summary>The rows a source offers before any join condition: a table's rows (only those an index names
        /// when the WHERE allows), a subquery's, or a collection's table.</summary>
        private IReadOnlyList<SqlRow> Candidates(Source s, Env env)
        {
            var exec = env.Exec;
            switch (s.Kind)
            {
                case SourceKind.Derived:
                    return s.Derived.Rows(env).Select(r => new SqlRow { Values = r }).ToList();
                case SourceKind.Table when s.IndexConditions.Count > 0:
                    // The conditions' values are constants: one index read per query.
                    return exec.Once(s, () =>
                    {
                        var codes = IndexCodes(s, env);
                        return codes == null ? exec.Rows(s.Schema) : exec.Db.Rows(s.Schema, codes);
                    });
                default:
                    return exec.Rows(s.Schema);
            }
        }

        /// <summary>The item codes the index gives for the source's conditions, or null to read the table.</summary>
        internal IReadOnlyCollection<string> IndexCodes(Source s, Env env)
        {
            var conditions = new List<IndexCondition>();
            foreach (var (column, op, values) in s.IndexConditions)
            {
                var vals = values.Select(v => v.Eval(env)).ToList();
                conditions.Add(new IndexCondition { Column = column, Op = op, Values = vals });
            }
            return env.Exec.Db.IndexCandidates(s.Schema, conditions);
        }

        private List<SqlRow[]> Join(List<SqlRow[]> rows, Source s, Env env)
        {
            var candidates = Candidates(s, env);
            var result = new List<SqlRow[]>();

            if (s.Join == JoinKind.First || s.Join == JoinKind.Cross)
            {
                foreach (var r in rows)
                {
                    foreach (var c in candidates)
                    {
                        var combo = (SqlRow[])r.Clone();
                        combo[s.Slot] = c;
                        FillPaths(combo, s.Slot, env);
                        if (s.On != null)
                        {
                            env.Row = combo;
                            if (s.On.Truth(env) != true) continue;
                        }
                        result.Add(combo);
                    }
                }
                return result;
            }

            // INNER and LEFT: by hash on the ON's equality keys when it has them, otherwise every pair.
            var hash = HashKeys(s);
            Dictionary<SqlValues.RowKey, List<SqlRow>> table = null;
            if (hash.Count > 0)
            {
                table = new Dictionary<SqlValues.RowKey, List<SqlRow>>();
                var probe = new SqlRow[Sources.Count];
                foreach (var c in candidates)
                {
                    probe[s.Slot] = c;
                    env.Row = probe;
                    var values = hash.Select(h => h.Inner.Eval(env)).ToArray();
                    if (values.Any(v => v == null)) continue;
                    var key = new SqlValues.RowKey(values);
                    if (!table.TryGetValue(key, out var list)) table[key] = list = new List<SqlRow>();
                    list.Add(c);
                }
            }

            foreach (var r in rows)
            {
                IEnumerable<SqlRow> matches = candidates;
                if (table != null)
                {
                    env.Row = r;
                    var values = hash.Select(h => h.Outer.Eval(env)).ToArray();
                    matches = values.Any(v => v == null) ? Array.Empty<SqlRow>()
                        : table.TryGetValue(new SqlValues.RowKey(values), out var list) ? list : Array.Empty<SqlRow>();
                }
                bool matched = false;
                foreach (var c in matches)
                {
                    var combo = (SqlRow[])r.Clone();
                    combo[s.Slot] = c;
                    FillPaths(combo, s.Slot, env);
                    env.Row = combo;
                    if (s.On != null && s.On.Truth(env) != true) continue;
                    matched = true;
                    result.Add(combo);
                }
                if (!matched && s.Join == JoinKind.Left)
                {
                    var combo = (SqlRow[])r.Clone();
                    FillPaths(combo, s.Slot, env);
                    result.Add(combo);
                }
            }
            return result;
        }

        private readonly Dictionary<int, List<(BExpr Inner, BExpr Outer)>> hashKeys = new();

        /// <summary>The ON's conjuncts <c>a = b</c> where one side reads only the joined source's row and the other
        /// only rows already joined (or enclosing queries'), with types whose equal values have equal keys.</summary>
        private List<(BExpr Inner, BExpr Outer)> HashKeys(Source s)
        {
            if (hashKeys.TryGetValue(s.Slot, out var known)) return known;
            var keys = new List<(BExpr, BExpr)>();
            foreach (var conjunct in Conjuncts(s.On))
            {
                if (conjunct is not BCompare { Op: "=" } eq) continue;
                if (!HashableTogether(eq.Left.Type, eq.Right.Type)) continue;
                if (ReadsOnly(eq.Left, slot => slot == s.Slot) && ReadsOnly(eq.Right, slot => Sources[slot].Owner < s.Slot && slot != s.Slot))
                    keys.Add((eq.Left, eq.Right));
                else if (ReadsOnly(eq.Right, slot => slot == s.Slot) && ReadsOnly(eq.Left, slot => Sources[slot].Owner < s.Slot && slot != s.Slot))
                    keys.Add((eq.Right, eq.Left));
            }
            hashKeys[s.Slot] = keys;
            return keys;
        }

        /// <summary>True when values of the two types that compare equal also hash alike (KeyOf), so a lookup by
        /// hashed key finds what the comparison would: not a DOUBLE against an INTEGER (1.0 = 1, hashed apart).</summary>
        internal static bool HashableTogether(SqlType a, SqlType b)
        {
            if (a == SqlType.Double || b == SqlType.Double) return a == b;
            if (SqlTypes.IsNumeric(a) && SqlTypes.IsNumeric(b)) return true;
            return a == b && a != SqlType.Map && a != SqlType.Array && a != SqlType.Null;
        }

        /// <summary>True when the expression reads columns of this query only from slots the test accepts (and at
        /// least one), with no subquery or aggregate.</summary>
        private static bool ReadsOnly(BExpr e, Func<int, bool> accept)
        {
            bool any = false;
            foreach (var d in e.Descendants())
            {
                switch (d)
                {
                    case BColumn c when c.Depth == 0:
                        if (!accept(c.Slot)) return false;
                        any = true;
                        break;
                    case BLinked:
                    case BAggregate:
                    case BScalarSubquery:
                    case BExists:
                    case BInQuery:
                        return false;
                }
            }
            return any;
        }

        public static IEnumerable<BExpr> Conjuncts(BExpr e)
        {
            if (e == null) yield break;
            if (e is BLogic { And: true } and)
            {
                foreach (var c in Conjuncts(and.Left)) yield return c;
                foreach (var c in Conjuncts(and.Right)) yield return c;
            }
            else yield return e;
        }

        /// <summary>The rows the paths from a slot lead to, once its row is known (NULL when it is NULL or the path
        /// leads nowhere), and the paths from those in turn.</summary>
        private void FillPaths(SqlRow[] combo, int slot, Env env)
        {
            var parent = Sources[slot];
            if (parent.Paths.Count == 0) return;
            foreach (int pathSlot in parent.Paths.Values)
            {
                var path = Sources[pathSlot];
                var from = combo[slot];
                combo[pathSlot] = from == null ? null : Lookup(path, parent.Schema, from, env.Exec);
                FillPaths(combo, pathSlot, env);
            }
        }

        private SqlRow Lookup(Source path, TableSchema parentSchema, SqlRow from, Exec exec)
        {
            var rel = path.Relation;
            if (path.ByKeys)
            {
                var local = rel.LocalKeys.Select(k => from.Values[parentSchema.Columns.FindIndex(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))]).ToArray();
                if (local.Any(v => v == null)) return null;
                var index = exec.Once((path.Schema.FullName, "key", string.Join(",", rel.RemoteKeys)), () =>
                {
                    var positions = rel.RemoteKeys.Select(k => path.Schema.Columns.FindIndex(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))).ToArray();
                    var map = new Dictionary<SqlValues.RowKey, SqlRow>();
                    if (positions.Any(p => p < 0)) return map;
                    foreach (var row in exec.Rows(path.Schema))
                    {
                        var key = new SqlValues.RowKey(positions.Select(p => row.Values[p]).ToArray());
                        map.TryAdd(key, row);
                    }
                    return map;
                });
                return index.TryGetValue(new SqlValues.RowKey(local), out var hit) ? hit : null;
            }
            var codes = from.Links?[path.RelationIndex];
            if (codes == null || codes.Length == 0) return null;
            var byCode = exec.Once((path.Schema.FullName, "code"), () =>
            {
                var map = new Dictionary<string, SqlRow>(StringComparer.Ordinal);
                foreach (var row in exec.Rows(path.Schema))
                    if (row.Code != null) map.TryAdd(row.Code, row);
                return map;
            });
            return byCode.TryGetValue(codes[0], out var found) ? found : null;
        }

        // ---- EXPLAIN ----

        public void Explain(Env outer, List<string> lines, string indent)
        {
            var env = new Env { Outer = outer, Exec = outer.Exec, Row = new SqlRow[Sources.Count] };
            foreach (var s in Sources)
            {
                string how;
                switch (s.Kind)
                {
                    case SourceKind.Derived:
                        lines.Add($"{indent}{JoinWord(s)}subquery {s.Alias}:");
                        s.Derived.Explain(env, lines, indent + "  ");
                        continue;
                    case SourceKind.Path:
                        how = s.ByKeys
                            ? $"path {Sources[s.ParentSlot].Alias}.{s.Relation.Name}: lookup by {string.Join(", ", s.Relation.RemoteKeys)}"
                            : $"path {Sources[s.ParentSlot].Alias}.{s.Relation.Name}: lookup by item code";
                        lines.Add($"{indent}{s.Schema.ShortName} ({how})");
                        continue;
                    case SourceKind.Collection:
                        how = s.ByKeys
                            ? $"the {s.Relation.Name} of {Parent(s)}, by {string.Join(", ", s.Relation.RemoteKeys)}"
                            : $"the {s.Relation.Name} of {Parent(s)}, by item code";
                        lines.Add($"{indent}{JoinWord(s)}{s.Describe()}: scan, {how}");
                        continue;
                }
                if (s.IndexConditions.Count > 0 && IndexCodes(s, env) is IReadOnlyCollection<string> codes)
                {
                    string conditions = string.Join(" AND ", s.IndexConditions.Select(c => $"{c.Column.Name} {c.Op}"));
                    lines.Add($"{indent}{JoinWord(s)}{s.Describe()}: index on {conditions} ({codes.Count} row{(codes.Count == 1 ? "" : "s")})");
                }
                else
                {
                    lines.Add($"{indent}{JoinWord(s)}{s.Describe()}: scan");
                }
            }
            foreach (var sub in Subqueries())
            {
                lines.Add($"{indent}subquery{(sub.Correlated ? " (for each row)" : "")}:");
                sub.Explain(env, lines, indent + "  ");
            }
        }

        private string Parent(Source s) => s.ParentDepth == 0 ? Sources[s.ParentSlot].Alias : "the enclosing query's row";

        private static string JoinWord(Source s) => s.Join switch
        {
            JoinKind.Inner => "join ",
            JoinKind.Left => "left join ",
            JoinKind.Cross => "cross join ",
            _ => "",
        };

        private IEnumerable<BoundSelect> Subqueries()
        {
            var exprs = new List<BExpr>();
            if (Where != null) exprs.Add(Where);
            if (Having != null) exprs.Add(Having);
            exprs.AddRange(Output.Select(o => o.Expr));
            exprs.AddRange(Sources.Where(s => s.On != null).Select(s => s.On));
            foreach (var e in exprs)
                foreach (var d in e.Descendants())
                {
                    if (d is BScalarSubquery q) yield return q.Query;
                    else if (d is BExists x) yield return x.Query;
                    else if (d is BInQuery i) yield return i.Query;
                }
        }
    }
}
