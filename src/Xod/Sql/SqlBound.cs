using System;
using System.Collections.Generic;
using System.Linq;

namespace Xod.Sql
{
    /// <summary>A row of a table as SQL reads it: its item code (<c>page.row</c>), its columns' values in the
    /// schema's order, and for each relation linked by item codes rather than keys, the codes it holds.</summary>
    internal sealed class SqlRow
    {
        public string Code { get; init; }
        public object[] Values { get; init; }
        /// <summary>Per relation of the schema: the item codes the row holds for it (null where keys link them).</summary>
        public string[][] Links { get; init; }
    }

    /// <summary>A condition an index may answer: <c>column op value(s)</c> (op <c>=</c>, <c>IN</c>, <c>&lt;</c>,
    /// <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>).</summary>
    internal sealed class IndexCondition
    {
        public ColumnSchema Column { get; init; }
        public string Op { get; init; }
        public List<object> Values { get; init; }
    }

    /// <summary>What SQL needs from a database: its tables, their rows and indexes (XmlEngine.Sql).</summary>
    internal interface ISqlDatabase
    {
        /// <summary>The table a query names: by full name, or by short name when that is unique (D2: any case).
        /// Throws XQ002 or XQ003.</summary>
        TableSchema FindTable(string name);

        /// <summary>A table by its full name (where a relation leads), or null.</summary>
        TableSchema TableByFullName(string fullName);

        /// <summary>Every row, in stored order (D5); or only the rows whose item codes are given, still in stored order.</summary>
        IReadOnlyList<SqlRow> Rows(TableSchema table, IReadOnlyCollection<string> onlyCodes);

        /// <summary>The item codes an index gives for these conditions on one table (all of them hold), or null when
        /// no index answers them. Never fewer rows than match: the query checks every row it gets.</summary>
        IReadOnlyCollection<string> IndexCandidates(TableSchema table, IReadOnlyList<IndexCondition> conditions);

        /// <summary>A column of a table read from its manifest that the manifest doesn't list but rows hold (E3): a
        /// text column, or null when no row holds it.</summary>
        ColumnSchema UnlistedColumn(TableSchema table, string name);
    }

    /// <summary>One run of one query: its parameters, its clock, the tables it has read, its warnings.</summary>
    internal sealed class Exec
    {
        public ISqlDatabase Db { get; init; }
        public DateTimeOffset Now { get; init; }
        public List<SqlWarning> Warnings { get; } = new();
        private readonly HashSet<string> warned = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<SqlRow>> tables = new(StringComparer.Ordinal);
        private readonly Dictionary<object, object> cache = new();

        public void Warn(string code, string message)
        {
            if (warned.Add(code + "\n" + message))
                Warnings.Add(new SqlWarning(code, message));
        }

        public IReadOnlyList<SqlRow> Rows(TableSchema table)
        {
            if (!tables.TryGetValue(table.FullName, out var rows))
                tables[table.FullName] = rows = Db.Rows(table, null);
            return rows;
        }

        /// <summary>A value computed once per query (an uncorrelated subquery's rows, a lookup).</summary>
        public T Once<T>(object key, Func<T> make)
        {
            if (!cache.TryGetValue(key, out object v))
                cache[key] = v = make();
            return (T)v;
        }
    }

    /// <summary>Where an expression is evaluated: the current row of each source of its query (a slot per source),
    /// the current group's rows in a grouped query, and the enclosing query's place for a correlated reference.</summary>
    internal sealed class Env
    {
        public Env Outer { get; init; }
        public SqlRow[] Row { get; set; }
        public List<SqlRow[]> Group { get; set; }
        public Exec Exec { get; init; }

        public Env At(int depth)
        {
            var e = this;
            for (int i = 0; i < depth; i++) e = e.Outer;
            return e;
        }
    }

    // ---- bound expressions: typed, names resolved ----

    internal abstract class BExpr
    {
        public SqlType Type { get; set; }
        /// <summary>The type of a MAP's values or an ARRAY's items.</summary>
        public SqlType Elem { get; set; }
        public abstract object Eval(Env env);
        public virtual IEnumerable<BExpr> Children => Enumerable.Empty<BExpr>();
        /// <summary>Structurally the same expression (GROUP BY matching).</summary>
        public abstract bool Same(BExpr other);

        public bool? Truth(Env env) => (bool?)Eval(env);

        public static bool Same(IReadOnlyList<BExpr> a, IReadOnlyList<BExpr> b) =>
            a.Count == b.Count && a.Zip(b).All(p => p.First.Same(p.Second));

        public IEnumerable<BExpr> Descendants()
        {
            yield return this;
            foreach (var c in Children)
                foreach (var d in c.Descendants())
                    yield return d;
        }
    }

    /// <summary>A literal or a parameter's value. <see cref="FreeText"/>: text that takes the type of what it meets.</summary>
    internal sealed class BConst : BExpr
    {
        public object Value { get; init; }
        public bool FreeText { get; init; }
        public override object Eval(Env env) => Value;
        public override bool Same(BExpr other) => other is BConst c && c.Type == Type && Equals(SqlValues.KeyOf(c.Value), SqlValues.KeyOf(Value));
    }

    internal sealed class BColumn : BExpr
    {
        public int Depth { get; init; }
        public int Slot { get; init; }
        public int Index { get; init; }
        public string Name { get; init; }
        /// <summary>The table column it reads (its crypto method, for VERIFY_HASH); null for a subquery's.</summary>
        public ColumnSchema Info { get; init; }

        public override object Eval(Env env)
        {
            var row = env.At(Depth).Row[Slot];
            return row?.Values[Index];
        }

        public override bool Same(BExpr other) => other is BColumn c && c.Depth == Depth && c.Slot == Slot && c.Index == Index;
    }

    internal sealed class BUnary : BExpr
    {
        public string Op { get; init; }
        public BExpr Operand { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Operand };

        public override object Eval(Env env)
        {
            object v = Operand.Eval(env);
            return Op switch
            {
                "NOT" => v == null ? null : !(bool)v,
                "-" => SqlValues.Negate(v),
                _ => v,
            };
        }

        public override bool Same(BExpr other) => other is BUnary u && u.Op == Op && u.Operand.Same(Operand);
    }

    internal sealed class BArith : BExpr
    {
        public string Op { get; init; }
        public BExpr Left { get; init; }
        public BExpr Right { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Left, Right };
        public override object Eval(Env env) => SqlValues.Arithmetic(Op, Left.Eval(env), Right.Eval(env), Type);
        public override bool Same(BExpr other) => other is BArith a && a.Op == Op && a.Left.Same(Left) && a.Right.Same(Right);
    }

    internal sealed class BConcat : BExpr
    {
        public BExpr Left { get; init; }
        public BExpr Right { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Left, Right };

        public override object Eval(Env env)
        {
            object a = Left.Eval(env);
            if (a == null) return null;
            object b = Right.Eval(env);
            if (b == null) return null;
            return SqlValues.TextOf(a) + SqlValues.TextOf(b);
        }

        public override bool Same(BExpr other) => other is BConcat c && c.Left.Same(Left) && c.Right.Same(Right);
    }

    internal sealed class BCompare : BExpr
    {
        public string Op { get; init; }
        public BExpr Left { get; init; }
        public BExpr Right { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Left, Right };
        public override object Eval(Env env) => SqlValues.Compare(Op, Left.Eval(env), Right.Eval(env));
        public override bool Same(BExpr other) => other is BCompare c && c.Op == Op && c.Left.Same(Left) && c.Right.Same(Right);
    }

    internal sealed class BLogic : BExpr
    {
        public bool And { get; init; }
        public BExpr Left { get; init; }
        public BExpr Right { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Left, Right };

        public override object Eval(Env env)
        {
            bool? a = Left.Truth(env);
            if (And)
            {
                if (a == false) return false;
                bool? b = Right.Truth(env);
                if (b == false) return false;
                return a == true && b == true ? true : null;
            }
            if (a == true) return true;
            bool? c = Right.Truth(env);
            if (c == true) return true;
            return a == false && c == false ? false : null;
        }

        public override bool Same(BExpr other) => other is BLogic l && l.And == And && l.Left.Same(Left) && l.Right.Same(Right);
    }

    internal sealed class BIsNull : BExpr
    {
        public BExpr Operand { get; init; }
        public bool Not { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Operand };
        public override object Eval(Env env) => (Operand.Eval(env) == null) != Not;
        public override bool Same(BExpr other) => other is BIsNull n && n.Not == Not && n.Operand.Same(Operand);
    }

    /// <summary><c>x IS TRUE</c> / <c>x IS FALSE</c>: never NULL.</summary>
    internal sealed class BIsTruth : BExpr
    {
        public BExpr Operand { get; init; }
        public bool Value { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Operand };
        public override object Eval(Env env) => Operand.Truth(env) == Value;
        public override bool Same(BExpr other) => other is BIsTruth t && t.Value == Value && t.Operand.Same(Operand);
    }

    internal sealed class BIn : BExpr
    {
        public BExpr Operand { get; init; }
        public List<BExpr> Items { get; init; }
        public bool Not { get; init; }
        public override IEnumerable<BExpr> Children => Items.Prepend(Operand);

        public override object Eval(Env env)
        {
            object v = Operand.Eval(env);
            if (v == null) return null;
            bool sawNull = false;
            foreach (var item in Items)
            {
                object x = item.Eval(env);
                if (x == null) { sawNull = true; continue; }
                if (SqlValues.Compare(v, x) == 0) return !Not;
            }
            return sawNull ? null : Not;
        }

        public override bool Same(BExpr other) => other is BIn i && i.Not == Not && i.Operand.Same(Operand) && Same(i.Items, Items);
    }

    internal sealed class BLike : BExpr
    {
        public BExpr Operand { get; init; }
        public BExpr Pattern { get; init; }
        public BExpr Escape { get; init; }
        public bool Not { get; init; }
        public bool IgnoreCase { get; init; }
        public override IEnumerable<BExpr> Children => Escape == null ? new[] { Operand, Pattern } : new[] { Operand, Pattern, Escape };

        public override object Eval(Env env)
        {
            bool? m = SqlValues.Like((string)Operand.Eval(env), (string)Pattern.Eval(env), (string)Escape?.Eval(env), IgnoreCase);
            return m == null ? null : m.Value != Not;
        }

        public override bool Same(BExpr other) =>
            other is BLike l && l.Not == Not && l.IgnoreCase == IgnoreCase && l.Operand.Same(Operand) && l.Pattern.Same(Pattern) &&
            (l.Escape == null ? Escape == null : Escape != null && l.Escape.Same(Escape));
    }

    internal sealed class BCase : BExpr
    {
        public BExpr Operand { get; init; }
        public List<(BExpr When, BExpr Then)> Branches { get; init; }
        public BExpr Else { get; init; }

        public override IEnumerable<BExpr> Children
        {
            get
            {
                if (Operand != null) yield return Operand;
                foreach (var (w, t) in Branches) { yield return w; yield return t; }
                if (Else != null) yield return Else;
            }
        }

        public override object Eval(Env env)
        {
            object subject = Operand?.Eval(env);
            foreach (var (when, then) in Branches)
            {
                bool hit = Operand == null ? when.Truth(env) == true : SqlValues.Equal(subject, when.Eval(env)) == true;
                if (hit) return SqlValues.Widen(then.Eval(env), Type);
            }
            return SqlValues.Widen(Else?.Eval(env), Type);
        }

        public override bool Same(BExpr other) =>
            other is BCase c && (c.Operand == null ? Operand == null : Operand != null && c.Operand.Same(Operand)) &&
            c.Branches.Count == Branches.Count && c.Branches.Zip(Branches).All(p => p.First.When.Same(p.Second.When) && p.First.Then.Same(p.Second.Then)) &&
            (c.Else == null ? Else == null : Else != null && c.Else.Same(Else));
    }

    internal sealed class BCast : BExpr
    {
        public BExpr Operand { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Operand };
        public override object Eval(Env env) => SqlValues.Cast(Operand.Eval(env), Type);
        public override bool Same(BExpr other) => other is BCast c && c.Type == Type && c.Operand.Same(Operand);
    }

    /// <summary>A scalar function: its arguments evaluated, then <see cref="Impl"/>; NULL in, NULL out unless
    /// <see cref="TakesNulls"/>.</summary>
    internal sealed class BFunc : BExpr
    {
        public string Name { get; init; }
        public List<BExpr> Args { get; init; }
        public Func<Env, object[], object> Impl { get; init; }
        public bool TakesNulls { get; init; }
        public override IEnumerable<BExpr> Children => Args;

        public override object Eval(Env env)
        {
            var values = new object[Args.Count];
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = Args[i].Eval(env);
                if (values[i] == null && !TakesNulls) return null;
            }
            return Impl(env, values);
        }

        public override bool Same(BExpr other) => other is BFunc f && f.Name == Name && Same(f.Args, Args);
    }

    internal sealed class BSubscript : BExpr
    {
        public BExpr Target { get; init; }
        public BExpr Index { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Target, Index };

        public override object Eval(Env env)
        {
            object t = Target.Eval(env);
            object i = Index.Eval(env);
            if (t == null || i == null) return null;
            if (t is SqlMap map)
                return map.TryGetValue(SqlValues.TextOf(i), out object v) ? v : null;
            var list = (IReadOnlyList<object>)t;
            long at = (long)i;
            return at >= 1 && at <= list.Count ? list[(int)(at - 1)] : null;
        }

        public override bool Same(BExpr other) => other is BSubscript s && s.Target.Same(Target) && s.Index.Same(Index);
    }

    /// <summary>An aggregate over the current group's rows.</summary>
    internal sealed class BAggregate : BExpr
    {
        public string Name { get; init; } // COUNT, SUM, AVG, MIN, MAX
        public BExpr Arg { get; init; }   // null for COUNT(*)
        public bool Distinct { get; init; }
        public override IEnumerable<BExpr> Children => Arg == null ? Enumerable.Empty<BExpr>() : new[] { Arg };

        public override object Eval(Env env)
        {
            var group = env.Group ?? throw new InvalidOperationException("an aggregate outside a group");
            if (Arg == null) return (long)group.Count;

            var values = new List<object>(group.Count);
            var rowEnv = new Env { Outer = env.Outer, Exec = env.Exec };
            HashSet<object> seen = Distinct ? new HashSet<object>() : null;
            foreach (var row in group)
            {
                rowEnv.Row = row;
                object v = Arg.Eval(rowEnv);
                if (v == null) continue;
                if (seen != null && !seen.Add(SqlValues.KeyOf(v))) continue;
                values.Add(v);
            }

            switch (Name)
            {
                case "COUNT":
                    return (long)values.Count;
                case "SUM":
                    if (values.Count == 0) return null;
                    return Arg.Type switch
                    {
                        SqlType.Integer => SumIntegers(values),
                        SqlType.Decimal => SumDecimals(values),
                        SqlType.Interval => values.Aggregate(TimeSpan.Zero, (s, v) => s + (TimeSpan)v),
                        _ => values.Sum(v => (double)v),
                    };
                case "AVG":
                    if (values.Count == 0) return null;
                    return Arg.Type switch
                    {
                        SqlType.Integer => (double)SumIntegersExact(values) / values.Count,
                        SqlType.Decimal => SumDecimals(values) / values.Count,
                        SqlType.Interval => TimeSpan.FromTicks((long)(values.Average(v => (decimal)((TimeSpan)v).Ticks))),
                        _ => values.Average(v => (double)v),
                    };
                case "MIN":
                case "MAX":
                {
                    if (values.Count == 0) return null;
                    object best = values[0];
                    for (int i = 1; i < values.Count; i++)
                    {
                        int c = SqlValues.Compare(values[i], best);
                        if (Name == "MIN" ? c < 0 : c > 0) best = values[i];
                    }
                    return best;
                }
            }
            throw new InvalidOperationException(Name);
        }

        private static long SumIntegers(List<object> values)
        {
            try
            {
                long s = 0;
                foreach (var v in values) s = checked(s + (long)v);
                return s;
            }
            catch (OverflowException)
            {
                throw SqlValues.Overflow();
            }
        }

        // For AVG: the exact sum, which may pass the INTEGER range while the average doesn't.
        private static decimal SumIntegersExact(List<object> values)
        {
            decimal s = 0;
            foreach (var v in values) s += (long)v;
            return s;
        }

        private static decimal SumDecimals(List<object> values)
        {
            try
            {
                decimal s = 0;
                foreach (var v in values) s += (decimal)v;
                return s;
            }
            catch (OverflowException)
            {
                throw SqlValues.Overflow();
            }
        }

        public override bool Same(BExpr other) =>
            other is BAggregate a && a.Name == Name && a.Distinct == Distinct && (a.Arg == null ? Arg == null : Arg != null && a.Arg.Same(Arg));
    }

    /// <summary>COALESCE: the first argument that isn't NULL, evaluated in order and no further.</summary>
    internal sealed class BCoalesce : BExpr
    {
        public List<BExpr> Args { get; init; }
        public override IEnumerable<BExpr> Children => Args;

        public override object Eval(Env env)
        {
            foreach (var a in Args)
            {
                object v = a.Eval(env);
                if (v != null) return SqlValues.Widen(v, Type);
            }
            return null;
        }

        public override bool Same(BExpr other) => other is BCoalesce c && Same(c.Args, Args);
    }

    /// <summary>A subquery as a value: its one row's one value, NULL for no row, XQ011 for more.</summary>
    internal sealed class BScalarSubquery : BExpr
    {
        public BoundSelect Query { get; init; }

        public override object Eval(Env env)
        {
            var rows = Query.Rows(env);
            if (rows.Count > 1) throw new XodSqlException("XQ011", "a subquery used as a value gave more than one row");
            return rows.Count == 0 ? null : rows[0][0];
        }

        public override bool Same(BExpr other) => ReferenceEquals(other, this);
    }

    internal sealed class BExists : BExpr
    {
        public BoundSelect Query { get; init; }
        public override object Eval(Env env) => Query.Rows(env).Count > 0;
        public override bool Same(BExpr other) => ReferenceEquals(other, this);
    }

    /// <summary><c>x [NOT] IN (SELECT ...)</c>, and <c>x op ANY/ALL (SELECT ...)</c>.</summary>
    internal sealed class BInQuery : BExpr
    {
        public BExpr Operand { get; init; }
        public BoundSelect Query { get; init; }
        public string Op { get; init; } = "=";
        public bool All { get; init; }
        public bool Not { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Operand };

        public override object Eval(Env env)
        {
            object v = Operand.Eval(env);
            var rows = Query.Rows(env);
            bool? result = Quantify(v, rows.Select(r => r[0]), Op, All);
            return Not ? (result == null ? null : !result.Value) : result;
        }

        public static bool? Quantify(object v, IEnumerable<object> values, string op, bool all)
        {
            bool sawNull = false, any = false;
            foreach (object x in values)
            {
                any = true;
                bool? c = v == null || x == null ? null : SqlValues.Compare(op, v, x);
                if (c == null) { sawNull = true; continue; }
                if (all && c == false) return false;
                if (!all && c == true) return true;
            }
            if (!any) return all;
            if (sawNull) return null;
            return all;
        }

        public override bool Same(BExpr other) => ReferenceEquals(other, this);
    }

    /// <summary><c>x op ANY (array)</c>, <c>x op ALL (array)</c>.</summary>
    internal sealed class BQuantifiedArray : BExpr
    {
        public BExpr Left { get; init; }
        public BExpr Array { get; init; }
        public string Op { get; init; }
        public bool All { get; init; }
        public override IEnumerable<BExpr> Children => new[] { Left, Array };

        public override object Eval(Env env)
        {
            object arr = Array.Eval(env);
            if (arr == null) return null;
            return BInQuery.Quantify(Left.Eval(env), ((IReadOnlyList<object>)arr), Op, All);
        }

        public override bool Same(BExpr other) => other is BQuantifiedArray q && q.Op == Op && q.All == All && q.Left.Same(Left) && q.Array.Same(Array);
    }

    /// <summary>A child row linked to its parent by the item codes the parent stores (a collection without keys):
    /// true when the current row of <see cref="ChildSlot"/> is one of them.</summary>
    internal sealed class BLinked : BExpr
    {
        public int ChildSlot { get; init; }
        public int ParentDepth { get; init; }
        public int ParentSlot { get; init; }
        public int Relation { get; init; }

        public override object Eval(Env env)
        {
            var child = env.Row[ChildSlot];
            var parent = env.At(ParentDepth).Row[ParentSlot];
            if (child == null || parent == null) return false;
            var codes = parent.Links?[Relation];
            return codes != null && Array.IndexOf(codes, child.Code) >= 0;
        }

        public override bool Same(BExpr other) => other is BLinked l && l.ChildSlot == ChildSlot && l.ParentDepth == ParentDepth && l.ParentSlot == ParentSlot && l.Relation == Relation;
    }
}
