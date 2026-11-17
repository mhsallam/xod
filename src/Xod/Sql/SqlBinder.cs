using System;
using System.Collections.Generic;
using System.Linq;

namespace Xod.Sql
{
    /// <summary>
    /// Turns a parsed query into a bound one (PLAN-060 section 2.2): tables found, each name resolved to a column of
    /// a source (or of a path from one, or of an enclosing query's), every expression typed, a text literal converted
    /// to the type it meets, aggregates and grouping checked, and the conditions an index may answer picked out. Every
    /// error a query can have without reading a row is raised here, before any row is read.
    /// </summary>
    internal sealed partial class SqlBinder
    {
        private readonly ISqlDatabase db;
        private readonly Exec exec;
        private readonly object[] positional;
        private readonly IReadOnlyDictionary<string, object> named;

        public SqlBinder(ISqlDatabase db, Exec exec, object[] positional, IReadOnlyDictionary<string, object> named)
        {
            this.db = db;
            this.exec = exec;
            this.positional = positional ?? Array.Empty<object>();
            this.named = named;
        }

        /// <summary>Where names are looked up: one query's sources, and the queries around it.</summary>
        private sealed class Scope
        {
            public Scope Parent { get; init; }
            public BoundSelect Query { get; init; }
            /// <summary>False for a subquery in FROM: the query it is a source of can't be seen from it (it isn't
            /// LATERAL); the queries around that one can.</summary>
            public bool SeesParent { get; init; } = true;
            /// <summary>The sources a name may refer to (paths aren't: they are reached through their parent).</summary>
            public List<Source> Visible { get; } = new();
            public string Clause { get; set; }
            public bool AggregatesAllowed { get; set; }
            public bool InAggregate { get; set; }
        }

        public BoundSelect Bind(SelectStmt stmt) => BindQuery(stmt, null, true);

        private BoundSelect BindQuery(SelectStmt stmt, Scope parent, bool seesParent)
        {
            var q = new BoundSelect { Distinct = stmt.Distinct };
            var scope = new Scope { Parent = parent, Query = q, SeesParent = seesParent };

            scope.Clause = "FROM";
            foreach (var item in stmt.From)
                BindFrom(item, scope);

            if (stmt.Where != null)
            {
                scope.Clause = "WHERE";
                q.Where = Condition(Bind(stmt.Where, scope), "WHERE");
            }

            scope.Clause = "the select list";
            scope.AggregatesAllowed = true;
            foreach (var item in stmt.Items)
            {
                if (item.Star)
                {
                    Star(item, scope);
                    continue;
                }
                var e = Bind(item.Expr, scope);
                q.Output.Add(new OutputColumn { Name = item.Alias ?? OutputName(item.Expr, e), Expr = e });
            }
            scope.AggregatesAllowed = false;

            scope.Clause = "GROUP BY";
            foreach (var g in stmt.GroupBy)
                q.GroupBy.Add(GroupKey(g, scope));
            if (q.GroupBy.Count > 0)
                q.Grouped = true;

            if (stmt.Having != null)
            {
                scope.Clause = "HAVING";
                scope.AggregatesAllowed = true;
                q.Having = Condition(Bind(stmt.Having, scope), "HAVING");
                scope.AggregatesAllowed = false;
                q.Grouped = true;
            }

            scope.Clause = "ORDER BY";
            foreach (var o in stmt.OrderBy)
                q.Order.Add(OrderKey(o, scope));

            if (q.Grouped)
                CheckGrouping(q);

            q.Limit = Count(stmt.Limit, "LIMIT");
            q.Offset = Count(stmt.Offset, "OFFSET");

            PickIndexConditions(q);
            return q;
        }

        // ---- FROM ----

        private void BindFrom(FromItem item, Scope scope)
        {
            var q = scope.Query;
            Source src;
            Source parent = null;
            int parentDepth = 0;

            if (item.Subquery != null)
            {
                var sub = BindQuery(item.Subquery, scope, seesParent: false);
                int slot = q.Sources.Count;
                src = new Source { Kind = SourceKind.Derived, Slot = slot, Alias = item.Alias, Derived = sub, Join = item.Join, Owner = slot };
            }
            else if (item.Collection != null)
            {
                var found = FindSource(item.CollectionOf, scope)
                    ?? throw new XodSqlException("XQ004", $"no table or alias named {item.CollectionOf}");
                (parent, parentDepth) = found;
                if (parent.Schema == null)
                    throw new XodSqlException("XQ004", $"{parent.Alias} is a subquery: it has no collections");
                var rel = RelationNamed(parent.Schema, item.Collection);
                if (rel == null || !rel.IsCollection)
                    throw new XodSqlException("XQ004", $"{parent.Schema.ShortName} has no collection named {item.Collection}");
                var target = Target(rel);
                int slot = q.Sources.Count;
                src = new Source
                {
                    Kind = SourceKind.Collection,
                    Slot = slot,
                    Alias = item.Alias ?? rel.Name,
                    Schema = target,
                    Join = item.Join,
                    ParentSlot = parent.Slot,
                    ParentDepth = parentDepth,
                    Relation = rel,
                    RelationIndex = parent.Schema.Relations.IndexOf(rel),
                    Owner = slot,
                    ByKeys = KeysResolve(parent.Schema, target, rel),
                };
                MarkCorrelated(scope, parentDepth);
            }
            else
            {
                var table = db.FindTable(item.Table);
                int slot = q.Sources.Count;
                src = new Source
                {
                    Kind = SourceKind.Table,
                    Slot = slot,
                    Alias = item.Alias ?? item.Table,
                    AltName = item.Alias == null ? table.ShortName : null,
                    Schema = table,
                    Join = item.Join,
                    Owner = slot,
                };
            }

            if (scope.Visible.Any(v => SharesName(v, src)))
                throw new XodSqlException("XQ003", $"the name {src.Alias} is used twice in FROM: give one an alias");
            q.Sources.Add(src);
            scope.Visible.Add(src);

            BExpr on = src.Kind == SourceKind.Collection ? Link(src, parent, parentDepth) : null;
            if (item.On != null)
            {
                scope.Clause = "ON";
                var cond = Condition(Bind(item.On, scope), "ON");
                on = on == null ? cond : And(on, cond);
            }
            src.On = on;
        }

        /// <summary>The condition that joins a collection's rows to their parent's: the relationship's keys, or the
        /// item codes the parent holds.</summary>
        private static BExpr Link(Source child, Source parent, int parentDepth)
        {
            if (!child.ByKeys)
                return new BLinked { ChildSlot = child.Slot, ParentDepth = parentDepth, ParentSlot = parent.Slot, Relation = child.RelationIndex, Type = SqlType.Boolean };
            BExpr all = null;
            var rel = child.Relation;
            for (int i = 0; i < rel.LocalKeys.Length; i++)
            {
                var c = ColumnExpr(child, ColumnIndex(child, rel.RemoteKeys[i]), 0);
                var p = ColumnExpr(parent, ColumnIndex(parent, rel.LocalKeys[i]), parentDepth);
                var eq = new BCompare { Op = "=", Left = c, Right = p, Type = SqlType.Boolean };
                all = all == null ? eq : And(all, eq);
            }
            return all;
        }

        /// <summary>True when a relationship's keys are columns of both tables, of types whose equal values hash alike
        /// (the lookup by keys is a hashed one). Otherwise the path follows the link its row stores.</summary>
        private static bool KeysResolve(TableSchema from, TableSchema to, RelationSchema rel)
        {
            if (!rel.HasKeys) return false;
            for (int i = 0; i < rel.LocalKeys.Length; i++)
            {
                var local = from.Column(rel.LocalKeys[i]);
                var remote = to.Column(rel.RemoteKeys[i]);
                if (local == null || remote == null || !BoundSelect.HashableTogether(local.Type, remote.Type) || IsCollection(local.Type))
                    return false;
            }
            return true;
        }

        private TableSchema Target(RelationSchema rel) =>
            db.TableByFullName(rel.Table)
            ?? throw new XodSqlException("XQ002", $"the table {rel.Table}, where {rel.Name} leads, doesn't exist");

        private Source PathSource(Source parent, RelationSchema rel, BoundSelect owner)
        {
            if (parent.Paths.TryGetValue(rel.Name, out int existing))
                return owner.Sources[existing];
            var target = Target(rel);
            var path = new Source
            {
                Kind = SourceKind.Path,
                Slot = owner.Sources.Count,
                Alias = parent.Alias + "." + rel.Name,
                Schema = target,
                Join = JoinKind.Left,
                ParentSlot = parent.Slot,
                Relation = rel,
                RelationIndex = parent.Schema.Relations.IndexOf(rel),
                Owner = parent.Owner,
                ByKeys = KeysResolve(parent.Schema, target, rel),
            };
            owner.Sources.Add(path);
            parent.Paths[rel.Name] = path.Slot;
            return path;
        }

        // ---- names ----

        private static bool Names(Source s, string name) =>
            string.Equals(s.Alias, name, StringComparison.OrdinalIgnoreCase) ||
            (s.AltName != null && string.Equals(s.AltName, name, StringComparison.OrdinalIgnoreCase));

        private static bool SharesName(Source a, Source b) => Names(a, b.Alias) || (b.AltName != null && Names(a, b.AltName));

        private static (Source Source, int Depth)? FindSource(string name, Scope scope)
        {
            int depth = 0;
            bool visible = true;
            for (var s = scope; s != null; s = s.Parent, depth++)
            {
                if (visible)
                {
                    var src = s.Visible.FirstOrDefault(v => Names(v, name));
                    if (src != null) return (src, depth);
                }
                visible = s.SeesParent;
            }
            return null;
        }

        /// <summary>A name or a dotted path: a source's alias first, then a column (or, before a dot, a relation) of
        /// one source of this query, then the same in each enclosing query.</summary>
        private BExpr TryResolve(IReadOnlyList<string> parts, Scope scope)
        {
            int depth = 0;
            bool visible = true;
            for (var s = scope; s != null; s = s.Parent, depth++)
            {
                if (visible)
                {
                    var hit = ResolveIn(parts, s, depth, scope);
                    if (hit != null) return hit;
                }
                visible = s.SeesParent;
            }
            return null;
        }

        private BExpr ResolveIn(IReadOnlyList<string> parts, Scope s, int depth, Scope origin)
        {
            if (parts.Count >= 2)
            {
                var src = s.Visible.FirstOrDefault(v => Names(v, parts[0]));
                if (src != null)
                    return Walk(src, parts, 1, depth, s, origin);
            }
            var owners = s.Visible.Where(v => ColumnIndex(v, parts[0]) >= 0 || (parts.Count >= 2 && v.Schema != null && RelationNamed(v.Schema, parts[0]) != null)).ToList();
            if (owners.Count > 1)
                throw new XodSqlException("XQ003", $"{parts[0]} is ambiguous: {string.Join(" and ", owners.Select(o => o.Alias))} both have it; name the table");
            return owners.Count == 1 ? Walk(owners[0], parts, 0, depth, s, origin) : null;
        }

        private BExpr Walk(Source src, IReadOnlyList<string> parts, int i, int depth, Scope at, Scope origin)
        {
            var cur = src;
            for (; i < parts.Count - 1; i++)
            {
                var rel = cur.Schema == null ? null : RelationNamed(cur.Schema, parts[i]);
                if (rel == null)
                    throw new XodSqlException("XQ004", $"{Describe(cur)} has no relation named {parts[i]}");
                if (rel.IsCollection)
                    throw new XodSqlException("XQ004", $"{rel.Name} is a collection, not a path: join it (JOIN {cur.Alias}.{rel.Name})");
                cur = PathSource(cur, rel, at.Query);
            }
            string name = parts[parts.Count - 1];
            int index = ColumnIndex(cur, name);
            if (index < 0 && cur.Schema != null && cur.Schema.ClrType == null)
                index = Unlisted(cur, name);
            if (index < 0)
            {
                if (cur.Schema != null && RelationNamed(cur.Schema, name) != null)
                    throw new XodSqlException("XQ004", $"{name} is a relation of {cur.Schema.ShortName}, not a column: name one of its columns ({name}.Column)");
                throw new XodSqlException("XQ004", $"{Describe(cur)} has no column named {name}");
            }
            MarkCorrelated(origin, depth);
            if (depth > 0)
                at.Query.OuterUses.Add((ColumnExpr(cur, index, 0), at.Clause, at.InAggregate));
            return ColumnExpr(cur, index, depth);
        }

        private BExpr Resolve(NameExpr n, Scope scope)
        {
            var hit = TryResolve(n.Parts, scope);
            if (hit != null) return hit;
            if (n.Parts.Count == 1)
            {
                string name = n.Parts[0];
                // A column the schema of a table read from its manifest doesn't list, that its rows hold (E3).
                var holders = new List<(Source, ColumnSchema)>();
                foreach (var v in scope.Visible)
                    if (v.Schema != null && v.Schema.ClrType == null && db.UnlistedColumn(v.Schema, name) is ColumnSchema c)
                        holders.Add((v, c));
                if (holders.Count > 1)
                    throw new XodSqlException("XQ003", $"{name} is ambiguous: {string.Join(" and ", holders.Select(h => h.Item1.Alias))} both have it; name the table");
                if (holders.Count == 1)
                    return ColumnExpr(holders[0].Item1, AddUnlisted(holders[0].Item1, holders[0].Item2), 0);
                if (scope.Visible.Any(v => v.Schema != null && RelationNamed(v.Schema, name) != null))
                    throw new XodSqlException("XQ004", $"{name} is a relation, not a column: name one of its columns ({name}.Column)");
                throw new XodSqlException("XQ004", $"no column named {name}");
            }
            throw new XodSqlException("XQ004", $"no table, alias or column named {n.Parts[0]} (in {n})");
        }

        private int Unlisted(Source s, string name)
        {
            var c = db.UnlistedColumn(s.Schema, name);
            return c == null ? -1 : AddUnlisted(s, c);
        }

        private int AddUnlisted(Source s, ColumnSchema c)
        {
            s.Schema.Columns.Add(c);
            exec.Warn("XQ013", $"{s.Schema.ShortName}.{c.Name} isn't in the table's schema: read as text");
            return s.Schema.Columns.Count - 1;
        }

        /// <summary>A column's position in a source, or -1. Two that differ only by case are ambiguous (D2).</summary>
        private static int ColumnIndex(Source s, string name)
        {
            int found = -1;
            for (int i = 0, n = s.ColumnCount; i < n; i++)
            {
                if (!string.Equals(s.ColumnName(i), name, StringComparison.OrdinalIgnoreCase)) continue;
                if (found >= 0)
                    throw new XodSqlException("XQ003", $"{Describe(s)} has two columns named {name}");
                found = i;
            }
            return found;
        }

        private static RelationSchema RelationNamed(TableSchema t, string name)
        {
            var hits = t.Relations.Where(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count > 1)
                throw new XodSqlException("XQ003", $"{t.ShortName} has two relations named {name}");
            return hits.FirstOrDefault();
        }

        private static BColumn ColumnExpr(Source s, int index, int depth)
        {
            if (s.Derived != null)
            {
                var o = s.Derived.Output[index];
                return new BColumn { Depth = depth, Slot = s.Slot, Index = index, Name = o.Name, Type = o.Type, Elem = o.Expr.Elem };
            }
            var c = s.Schema.Columns[index];
            return new BColumn { Depth = depth, Slot = s.Slot, Index = index, Name = c.Name, Type = c.Type, Elem = c.ElementType, Info = c };
        }

        /// <summary>A reference <paramref name="depth"/> queries out makes each query in between depend on the row of
        /// the one around it: run again for each.</summary>
        private static void MarkCorrelated(Scope origin, int depth)
        {
            var s = origin;
            for (int k = 0; k < depth; k++, s = s.Parent)
                s.Query.Correlated = true;
        }

        private static string Describe(Source s) => s.Kind switch
        {
            SourceKind.Derived => "the subquery " + s.Alias,
            SourceKind.Path => s.Alias + " (" + s.Schema.ShortName + ")",
            _ => s.Schema.ShortName,
        };

        // ---- the select list, GROUP BY, ORDER BY, LIMIT ----

        private void Star(SelectItem item, Scope scope)
        {
            IEnumerable<Source> sources;
            if (item.Qualifier == null)
            {
                if (scope.Visible.Count == 0)
                    throw new XodSqlException("XQ004", "SELECT * needs a FROM");
                sources = scope.Visible.ToList();
            }
            else
            {
                var src = scope.Visible.FirstOrDefault(v => Names(v, item.Qualifier))
                    ?? throw new XodSqlException("XQ004", $"no table or alias named {item.Qualifier}");
                sources = new[] { src };
            }
            foreach (var s in sources)
                for (int i = 0, n = s.ColumnCount; i < n; i++)
                    scope.Query.Output.Add(new OutputColumn { Name = s.ColumnName(i), Expr = ColumnExpr(s, i, 0) });
        }

        /// <summary>A column's own name, a path's last name, an aggregate's or a function's name in lower case,
        /// otherwise <c>?column?</c> (PLAN-060 section 3).</summary>
        private static string OutputName(Expr ast, BExpr bound)
        {
            if (ast is NameExpr && bound is BColumn c) return c.Name;
            if (ast is FunctionExpr f && !f.Name.StartsWith("$", StringComparison.Ordinal)) return f.Name.ToLowerInvariant();
            return "?column?";
        }

        /// <summary>A GROUP BY item: a column of the sources first, then a select-list alias, or an ordinal.</summary>
        private BExpr GroupKey(Expr g, Scope s)
        {
            var q = s.Query;
            if (g is LiteralExpr { Type: SqlType.Integer } lit)
                return NotAggregate(OutputAt((long)lit.Value, q, "GROUP BY").Expr);
            if (g is NameExpr { Parts.Count: 1 } n)
            {
                var hit = TryResolve(n.Parts, s);
                if (hit != null) return hit;
                var outputs = q.Output.Where(x => string.Equals(x.Name, n.Parts[0], StringComparison.OrdinalIgnoreCase)).ToList();
                if (outputs.Count > 1)
                    throw new XodSqlException("XQ003", $"GROUP BY {n.Parts[0]} is ambiguous: two selected columns have that name");
                if (outputs.Count == 1)
                    return NotAggregate(outputs[0].Expr);
            }
            return Bind(g, s);
        }

        private static BExpr NotAggregate(BExpr e)
        {
            if (e.Descendants().Any(d => d is BAggregate))
                throw new XodSqlException("XQ006", "GROUP BY can't group by an aggregate");
            return e;
        }

        private static OutputColumn OutputAt(long ordinal, BoundSelect q, string clause)
        {
            if (ordinal < 1 || ordinal > q.Output.Count)
                throw new XodSqlException("XQ004", $"{clause} {ordinal}: the select list has {q.Output.Count} column{(q.Output.Count == 1 ? "" : "s")}");
            return q.Output[(int)ordinal - 1];
        }

        /// <summary>An ORDER BY item: a select-list name first, then an ordinal, then any expression of the sources
        /// (with DISTINCT, it must be a selected one).</summary>
        private OrderKey OrderKey(OrderItem o, Scope s)
        {
            var q = s.Query;
            bool nullsFirst = o.NullsFirst ?? o.Desc;
            int output = -1;
            if (o.Expr is LiteralExpr { Type: SqlType.Integer } lit)
            {
                output = q.Output.IndexOf(OutputAt((long)lit.Value, q, "ORDER BY"));
            }
            else if (o.Expr is NameExpr { Parts.Count: 1 } n)
            {
                var hits = q.Output.Select((x, i) => (x, i)).Where(p => string.Equals(p.x.Name, n.Parts[0], StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count > 1 && !hits.All(h => h.x.Expr.Same(hits[0].x.Expr)))
                    throw new XodSqlException("XQ003", $"ORDER BY {n.Parts[0]} is ambiguous: two selected columns have that name");
                if (hits.Count > 0)
                    output = hits[0].i;
            }
            if (output >= 0)
                return new OrderKey { Output = output, Desc = o.Desc, NullsFirst = nullsFirst };

            s.AggregatesAllowed = true;
            BExpr e;
            try
            {
                e = Bind(o.Expr, s);
            }
            finally
            {
                s.AggregatesAllowed = false;
            }
            if (IsCollection(e.Type))
                throw new XodSqlException("XQ005", $"a {Name(e.Type)} can't be ordered");
            int same = q.Output.FindIndex(x => x.Expr.Same(e));
            if (same >= 0)
                return new OrderKey { Output = same, Desc = o.Desc, NullsFirst = nullsFirst };
            if (q.Distinct)
                throw new XodSqlException("XQ006", "with SELECT DISTINCT, ORDER BY must name selected columns");
            return new OrderKey { Expr = e, Desc = o.Desc, NullsFirst = nullsFirst };
        }

        /// <summary>In a grouped query, what is computed once per group must be grouped or inside an aggregate.</summary>
        private static void CheckGrouping(BoundSelect q)
        {
            var exprs = q.Output.Select(o => o.Expr).Concat(q.Order.Where(o => o.Expr != null).Select(o => o.Expr)).ToList();
            if (q.Having != null) exprs.Add(q.Having);
            foreach (var e in exprs)
            {
                var bad = Uncovered(e, q.GroupBy);
                if (bad != null)
                    throw new XodSqlException("XQ006", $"{bad.Name} must be in GROUP BY or inside an aggregate");
            }
            // A subquery in those clauses runs once per group too: the columns of this query it reads (at any depth)
            // must be grouped, unless it sits inside an aggregate. In WHERE it runs per row, before grouping.
            foreach (var (column, clause, inAggregate) in q.OuterUses)
            {
                if (inAggregate || clause is not ("the select list" or "HAVING" or "ORDER BY"))
                    continue;
                if (!q.GroupBy.Any(g => g.Same(column)))
                    throw new XodSqlException("XQ006", $"{column.Name} must be in GROUP BY or inside an aggregate (a subquery reads it)");
            }
        }

        private static BColumn Uncovered(BExpr e, List<BExpr> groupBy)
        {
            if (groupBy.Any(g => g.Same(e))) return null;
            switch (e)
            {
                case BAggregate:
                    return null;
                case BColumn c:
                    return c.Depth == 0 ? c : null;
            }
            foreach (var child in e.Children)
            {
                var bad = Uncovered(child, groupBy);
                if (bad != null) return bad;
            }
            return null;
        }

        private BExpr Count(Expr e, string what)
        {
            if (e == null) return null;
            // LIMIT and OFFSET read no row: a number or a parameter.
            var scope = new Scope { Query = new BoundSelect(), Clause = what };
            var v = Coerce(Bind(e, scope), SqlType.Integer);
            if (v.Type != SqlType.Integer && v.Type != SqlType.Null)
                throw new XodSqlException("XQ005", $"{what} needs an INTEGER, not a {Name(v.Type)}");
            return v;
        }

        // ---- indexes ----

        /// <summary>The WHERE's top-level conditions <c>column op constant</c> and <c>column IN (constants)</c> on a
        /// key, unique or indexed column of a table whose class is known: an index may answer them. The query still
        /// checks the whole WHERE on every row it reads.</summary>
        private static void PickIndexConditions(BoundSelect q)
        {
            foreach (var c in BoundSelect.Conjuncts(q.Where))
            {
                switch (c)
                {
                    case BCompare { Op: "=" or "<" or "<=" or ">" or ">=" } cmp:
                        if (Indexable(q, cmp.Left, out var src, out var col) && cmp.Right is BConst)
                            src.IndexConditions.Add((col, cmp.Op, new List<BExpr> { cmp.Right }));
                        else if (Indexable(q, cmp.Right, out src, out col) && cmp.Left is BConst)
                            src.IndexConditions.Add((col, Flip(cmp.Op), new List<BExpr> { cmp.Left }));
                        break;
                    case BIn { Not: false } inList when inList.Items.All(i => i is BConst):
                        if (Indexable(q, inList.Operand, out var s2, out var c2))
                            s2.IndexConditions.Add((c2, "IN", inList.Items.ToList()));
                        break;
                }
            }
        }

        private static bool Indexable(BoundSelect q, BExpr e, out Source src, out ColumnSchema col)
        {
            src = null;
            col = null;
            if (e is not BColumn { Depth: 0 } c) return false;
            var s = q.Sources[c.Slot];
            if (s.Kind != SourceKind.Table || s.Schema.ClrType == null || s.Join == JoinKind.Left) return false;
            var cs = s.Schema.Columns[c.Index];
            if (cs.Key == 0 && !cs.Indexed && !cs.Unique) return false;
            src = s;
            col = cs;
            return true;
        }

        private static string Flip(string op) => op switch
        {
            "<" => ">",
            "<=" => ">=",
            ">" => "<",
            ">=" => "<=",
            _ => op,
        };

        // ---- expressions ----

        private BExpr Bind(Expr e, Scope s)
        {
            switch (e)
            {
                case LiteralExpr l:
                    return new BConst { Type = l.Type, Value = l.Value, FreeText = l.Untyped };
                case ParameterExpr p:
                    return Parameter(p);
                case NameExpr n:
                    return Resolve(n, s);
                case UnaryExpr u:
                    return Unary(u, s);
                case BinaryExpr b:
                    return Binary(b, s);
                case IsNullExpr n:
                    return new BIsNull { Operand = Bind(n.Operand, s), Not = n.Not, Type = SqlType.Boolean };
                case BetweenExpr b:
                {
                    var v = Bind(b.Operand, s);
                    var both = And(Comparison(">=", v, Bind(b.Low, s)), Comparison("<=", v, Bind(b.High, s)));
                    return b.Not ? Not(both) : both;
                }
                case InExpr i:
                    return In(i, s);
                case ExistsExpr x:
                    return new BExists { Query = BindQuery(x.Query, s, true), Type = SqlType.Boolean };
                case LikeExpr l:
                    return new BLike
                    {
                        Operand = Expect(Bind(l.Operand, s), SqlType.Text, l.IgnoreCase ? "ILIKE" : "LIKE"),
                        Pattern = Expect(Bind(l.Pattern, s), SqlType.Text, l.IgnoreCase ? "ILIKE" : "LIKE"),
                        Escape = l.Escape == null ? null : Expect(Bind(l.Escape, s), SqlType.Text, "ESCAPE"),
                        Not = l.Not,
                        IgnoreCase = l.IgnoreCase,
                        Type = SqlType.Boolean,
                    };
                case CaseExpr c:
                    return Case(c, s);
                case CastExpr c:
                {
                    var v = Bind(c.Operand, s);
                    if (!SqlValues.CanCast(v.Type, c.Type))
                        throw new XodSqlException("XQ005", $"a {Name(v.Type)} can't be cast to {Name(c.Type)}");
                    return new BCast { Operand = v, Type = c.Type };
                }
                case FunctionExpr f:
                    return Function(f, s);
                case SubscriptExpr x:
                    return Subscript(x, s);
                case SubqueryExpr q:
                {
                    var sub = BindQuery(q.Query, s, true);
                    var col = OneColumn(sub, "a value");
                    return new BScalarSubquery { Query = sub, Type = col.Type, Elem = col.Expr.Elem };
                }
                case QuantifiedExpr q:
                    return Quantified(q, s);
            }
            throw new InvalidOperationException("an expression the binder doesn't know: " + e.GetType().Name);
        }

        private BExpr Parameter(ParameterExpr p)
        {
            object v;
            if (p.Name == null)
            {
                if (p.Position >= positional.Length)
                    throw new XodSqlException("XQ012", $"no value for parameter {p.Position + 1} (?): {positional.Length} given");
                v = positional[p.Position];
            }
            else if (!TryNamed(p.Name, out v))
            {
                throw new XodSqlException("XQ012", $"no value for parameter @{p.Name}");
            }
            if (v == null || v is DBNull)
                return new BConst { Type = SqlType.Null };
            // The type of the value as given, before it is converted: a value of no SQL type has none.
            if (!SqlTypes.TryTypeOfValue(v, out var type))
                throw new XodSqlException("XQ012", $"parameter {(p.Name == null ? (p.Position + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "@" + p.Name)} is a {v.GetType().Name}, which no SQL type holds");
            object value = SqlTypes.ParameterValue(v);
            // A text value takes the type of what it meets, as a text literal does.
            return new BConst { Type = type, Value = value, FreeText = type == SqlType.Text };
        }

        private bool TryNamed(string name, out object value)
        {
            value = null;
            if (named == null) return false;
            if (named.TryGetValue(name, out value) || named.TryGetValue("@" + name, out value)) return true;
            foreach (var kv in named)
            {
                string key = kv.Key.StartsWith("@", StringComparison.Ordinal) ? kv.Key.Substring(1) : kv.Key;
                if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = kv.Value;
                    return true;
                }
            }
            return false;
        }

        private BExpr Unary(UnaryExpr u, Scope s)
        {
            var v = Bind(u.Operand, s);
            switch (u.Op)
            {
                case "NOT":
                    return Not(Condition(v, "NOT"));
                case "-":
                    if (v.Type != SqlType.Null && !SqlTypes.IsNumeric(v.Type) && v.Type != SqlType.Interval)
                        throw new XodSqlException("XQ005", $"a {Name(v.Type)} can't be negated");
                    return new BUnary { Op = "-", Operand = v, Type = v.Type };
                default:
                    if (v.Type != SqlType.Null && !SqlTypes.IsNumeric(v.Type) && v.Type != SqlType.Interval)
                        throw new XodSqlException("XQ005", $"unary + doesn't apply to a {Name(v.Type)}");
                    return v;
            }
        }

        private BExpr Binary(BinaryExpr b, Scope s)
        {
            var l = Bind(b.Left, s);
            var r = Bind(b.Right, s);
            switch (b.Op)
            {
                case "AND":
                case "OR":
                    return new BLogic { And = b.Op == "AND", Left = Condition(l, b.Op), Right = Condition(r, b.Op), Type = SqlType.Boolean };
                case "=":
                case "<>":
                case "!=":
                case "<":
                case "<=":
                case ">":
                case ">=":
                    return Comparison(b.Op, l, r);
                case "||":
                    if (IsCollection(l.Type) || IsCollection(r.Type))
                        throw new XodSqlException("XQ005", $"|| doesn't apply to a {Name(IsCollection(l.Type) ? l.Type : r.Type)}");
                    return new BConcat { Left = l, Right = r, Type = SqlType.Text };
                default:
                {
                    (l, r) = CoercePair(l, r);
                    var t = SqlValues.ArithmeticType(b.Op, l.Type, r.Type)
                        ?? throw new XodSqlException("XQ005", $"'{b.Op}' doesn't apply to a {Name(l.Type)} and a {Name(r.Type)}");
                    return new BArith { Op = b.Op, Left = l, Right = r, Type = t };
                }
            }
        }

        private static BExpr Comparison(string op, BExpr l, BExpr r)
        {
            (l, r) = CoercePair(l, r);
            if (!SqlValues.Comparable(l.Type, r.Type))
                throw new XodSqlException("XQ005", $"a {Name(l.Type)} can't be compared with a {Name(r.Type)}");
            return new BCompare { Op = op == "!=" ? "<>" : op, Left = l, Right = r, Type = SqlType.Boolean };
        }

        private BExpr In(InExpr i, Scope s)
        {
            var v = Bind(i.Operand, s);
            if (i.Query != null)
            {
                var sub = BindQuery(i.Query, s, true);
                var col = OneColumn(sub, "IN");
                v = Coerce(v, col.Type);
                if (!SqlValues.Comparable(v.Type, col.Type))
                    throw new XodSqlException("XQ005", $"a {Name(v.Type)} can't be compared with a {Name(col.Type)}");
                return new BInQuery { Operand = v, Query = sub, Not = i.Not, Type = SqlType.Boolean };
            }
            var items = i.Items.Select(x => Bind(x, s)).ToList();
            if (v is BConst { FreeText: true } && items.FirstOrDefault(x => x is not BConst { FreeText: true } && x.Type != SqlType.Null) is BExpr typed)
                v = Coerce(v, typed.Type);
            for (int k = 0; k < items.Count; k++)
            {
                items[k] = Coerce(items[k], v.Type);
                if (!SqlValues.Comparable(v.Type, items[k].Type))
                    throw new XodSqlException("XQ005", $"a {Name(v.Type)} can't be compared with a {Name(items[k].Type)}");
            }
            return new BIn { Operand = v, Items = items, Not = i.Not, Type = SqlType.Boolean };
        }

        private BExpr Case(CaseExpr c, Scope s)
        {
            BExpr operand = c.Operand == null ? null : Bind(c.Operand, s);
            var whens = new List<BExpr>();
            var results = new List<BExpr>();
            foreach (var (w, t) in c.Branches)
            {
                var when = Bind(w, s);
                if (operand == null)
                {
                    when = Condition(when, "WHEN");
                }
                else
                {
                    (operand, when) = CoercePair(operand, when);
                    if (!SqlValues.Comparable(operand.Type, when.Type))
                        throw new XodSqlException("XQ005", $"a {Name(operand.Type)} can't be compared with a {Name(when.Type)}");
                }
                whens.Add(when);
                results.Add(Bind(t, s));
            }
            BExpr els = c.Else == null ? null : Bind(c.Else, s);
            var all = els == null ? results : results.Append(els).ToList();
            var type = CommonType(all, "CASE");
            results = results.Select(r => Coerce(r, type)).ToList();
            els = els == null ? null : Coerce(els, type);
            return new BCase
            {
                Operand = operand,
                Branches = whens.Zip(results, (w, r) => (w, r)).ToList(),
                Else = els,
                Type = type,
                Elem = all.FirstOrDefault(x => x.Type == type)?.Elem ?? SqlType.Null,
            };
        }

        private BExpr Subscript(SubscriptExpr x, Scope s)
        {
            var target = Bind(x.Target, s);
            var index = Bind(x.Index, s);
            switch (target.Type)
            {
                case SqlType.Map:
                    if (IsCollection(index.Type))
                        throw new XodSqlException("XQ005", $"a MAP's key is a scalar, not a {Name(index.Type)}");
                    return new BSubscript { Target = target, Index = index, Type = target.Elem };
                case SqlType.Array:
                    index = Expect(index, SqlType.Integer, "an ARRAY subscript");
                    return new BSubscript { Target = target, Index = index, Type = target.Elem };
                case SqlType.Null:
                    return new BConst { Type = SqlType.Null };
            }
            throw new XodSqlException("XQ005", $"a {Name(target.Type)} can't be subscripted");
        }

        private BExpr Quantified(QuantifiedExpr q, Scope s)
        {
            var left = Bind(q.Left, s);
            string op = q.Op == "!=" ? "<>" : q.Op;
            if (q.Query != null)
            {
                var sub = BindQuery(q.Query, s, true);
                var col = OneColumn(sub, q.All ? "ALL" : "ANY");
                left = Coerce(left, col.Type);
                if (!SqlValues.Comparable(left.Type, col.Type))
                    throw new XodSqlException("XQ005", $"a {Name(left.Type)} can't be compared with a {Name(col.Type)}");
                return new BInQuery { Operand = left, Query = sub, Op = op, All = q.All, Type = SqlType.Boolean };
            }
            var array = Bind(q.Array, s);
            if (array.Type != SqlType.Array && array.Type != SqlType.Null)
                throw new XodSqlException("XQ005", $"{(q.All ? "ALL" : "ANY")} takes an ARRAY or a subquery, not a {Name(array.Type)}");
            left = Coerce(left, array.Elem);
            if (!SqlValues.Comparable(left.Type, array.Elem))
                throw new XodSqlException("XQ005", $"a {Name(left.Type)} can't be compared with an ARRAY of {Name(array.Elem)}");
            return new BQuantifiedArray { Left = left, Array = array, Op = op, All = q.All, Type = SqlType.Boolean };
        }

        private static OutputColumn OneColumn(BoundSelect sub, string what)
        {
            if (sub.Output.Count != 1)
                throw new XodSqlException("XQ005", $"a subquery used as {(what == "a value" ? what : "in " + what)} selects one column, not {sub.Output.Count}");
            return sub.Output[0];
        }

        private BExpr Function(FunctionExpr f, Scope s)
        {
            switch (f.Name)
            {
                case "COUNT":
                case "SUM":
                case "AVG":
                case "MIN":
                case "MAX":
                    return Aggregate(f, s);
            }
            if (f.Star || f.Distinct)
                throw new XodSqlException("XQ001", $"{f.Name} isn't an aggregate: * and DISTINCT don't apply to it");
            var args = f.Args.Select(a => Bind(a, s)).ToList();
            return ScalarFunction(f.Name, args);
        }

        private BExpr Aggregate(FunctionExpr f, Scope s)
        {
            if (!s.AggregatesAllowed)
                throw new XodSqlException("XQ006", $"{f.Name} is an aggregate: it isn't allowed in {s.Clause}");
            if (s.InAggregate)
                throw new XodSqlException("XQ006", $"an aggregate inside an aggregate ({f.Name})");
            s.Query.Grouped = true;
            if (f.Star)
            {
                if (f.Name != "COUNT")
                    throw new XodSqlException("XQ001", $"{f.Name}(*) isn't SQL: only COUNT(*)");
                return new BAggregate { Name = "COUNT", Type = SqlType.Integer };
            }
            if (f.Args.Count != 1)
                throw new XodSqlException("XQ001", $"{f.Name} takes one argument");
            BExpr arg;
            s.InAggregate = true;
            try
            {
                arg = Bind(f.Args[0], s);
            }
            finally
            {
                s.InAggregate = false;
            }
            SqlType type;
            switch (f.Name)
            {
                case "COUNT":
                    type = SqlType.Integer;
                    break;
                case "SUM":
                    type = arg.Type switch
                    {
                        SqlType.Integer or SqlType.Decimal or SqlType.Double or SqlType.Interval or SqlType.Null => arg.Type,
                        _ => throw new XodSqlException("XQ005", $"SUM doesn't apply to a {Name(arg.Type)}"),
                    };
                    break;
                case "AVG":
                    type = arg.Type switch
                    {
                        SqlType.Integer => SqlType.Double,
                        SqlType.Decimal or SqlType.Double or SqlType.Interval or SqlType.Null => arg.Type,
                        _ => throw new XodSqlException("XQ005", $"AVG doesn't apply to a {Name(arg.Type)}"),
                    };
                    break;
                default:
                    if (IsCollection(arg.Type))
                        throw new XodSqlException("XQ005", $"{f.Name} doesn't apply to a {Name(arg.Type)}");
                    type = arg.Type;
                    break;
            }
            return new BAggregate { Name = f.Name, Arg = arg, Distinct = f.Distinct, Type = type };
        }

        // ---- typing helpers ----

        private static bool IsCollection(SqlType t) => t == SqlType.Map || t == SqlType.Array;

        private static string Name(SqlType t) => SqlTypes.Name(t);

        private static BExpr And(BExpr a, BExpr b) => new BLogic { And = true, Left = a, Right = b, Type = SqlType.Boolean };

        private static BExpr Not(BExpr e) => new BUnary { Op = "NOT", Operand = e, Type = SqlType.Boolean };

        private static BExpr Condition(BExpr e, string where)
        {
            e = Coerce(e, SqlType.Boolean);
            if (e.Type != SqlType.Boolean && e.Type != SqlType.Null)
                throw new XodSqlException("XQ005", $"{where} needs a BOOLEAN, not a {Name(e.Type)}");
            return e;
        }

        /// <summary>An argument of one type (a text literal converted to it), or NULL.</summary>
        private static BExpr Expect(BExpr e, SqlType type, string what)
        {
            e = Coerce(e, type);
            if (e.Type != type && e.Type != SqlType.Null)
                throw new XodSqlException("XQ005", $"{what} takes a {Name(type)}, not a {Name(e.Type)}");
            return e;
        }

        /// <summary>A text literal (or text parameter) beside a value of another type takes that type (PLAN-060
        /// section 3); <c>XQ005</c> when its text isn't a value of it.</summary>
        private static BExpr Coerce(BExpr e, SqlType to)
        {
            if (e is not BConst { FreeText: true } c || to == SqlType.Text || to == SqlType.Null)
                return e;
            if (c.Value == null)
                return new BConst { Type = to };
            if (IsCollection(to) || !SqlLiterals.TryParse(to, (string)c.Value, out object v))
                throw new XodSqlException("XQ005", $"'{c.Value}' isn't a {Name(to)}");
            return new BConst { Type = to, Value = v };
        }

        private static (BExpr, BExpr) CoercePair(BExpr l, BExpr r)
        {
            bool lf = l is BConst { FreeText: true }, rf = r is BConst { FreeText: true };
            if (lf && !rf) l = Coerce(l, r.Type);
            else if (rf && !lf) r = Coerce(r, l.Type);
            return (l, r);
        }

        /// <summary>The type several values share (CASE's results, COALESCE's arguments): numbers widen, text
        /// literals take the others' type.</summary>
        private static SqlType CommonType(IReadOnlyList<BExpr> exprs, string what)
        {
            SqlType common = SqlType.Null;
            bool freeText = false;
            foreach (var e in exprs)
            {
                if (e is BConst { FreeText: true })
                {
                    freeText = true;
                    continue;
                }
                if (e.Type == SqlType.Null) continue;
                if (common == SqlType.Null) common = e.Type;
                else if (SqlTypes.IsNumeric(common) && SqlTypes.IsNumeric(e.Type)) common = SqlTypes.Numeric(common, e.Type);
                else if (common != e.Type)
                    throw new XodSqlException("XQ005", $"{what} mixes a {Name(common)} and a {Name(e.Type)}");
            }
            return common == SqlType.Null && freeText ? SqlType.Text : common;
        }
    }
}
