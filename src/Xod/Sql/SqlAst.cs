using System.Collections.Generic;

namespace Xod.Sql
{
    // The parsed form of a query (PLAN-045 section 5), before names are resolved.

    internal abstract class Expr
    {
        public int Line { get; init; }
        public int Column { get; init; }
    }

    /// <summary>A literal: its SQL type and value (a number, text, TRUE/FALSE, NULL, or a typed literal such as
    /// <c>DATE '2026-11-08'</c>).</summary>
    internal sealed class LiteralExpr : Expr
    {
        public SqlType Type { get; init; }
        public object Value { get; init; }
        /// <summary>Text written as a plain literal, which takes the type of what it meets (PLAN-060 section 3).</summary>
        public bool Untyped { get; init; }
    }

    internal sealed class ParameterExpr : Expr
    {
        /// <summary>The name without <c>@</c>, or null for <c>?</c>.</summary>
        public string Name { get; init; }
        /// <summary>For <c>?</c>: its position among the query's <c>?</c>s, from 0.</summary>
        public int Position { get; init; }
    }

    /// <summary>A name, or a dotted path: <c>Name</c>, <c>c.Name</c>, <c>o.Customer.Name</c>.</summary>
    internal sealed class NameExpr : Expr
    {
        public List<string> Parts { get; init; }
        public override string ToString() => string.Join(".", Parts);
    }

    internal sealed class UnaryExpr : Expr
    {
        public string Op { get; init; } // "-", "+", "NOT"
        public Expr Operand { get; init; }
    }

    internal sealed class BinaryExpr : Expr
    {
        public string Op { get; init; } // "+", "-", "*", "/", "%", "||", "=", "<>", "<", "<=", ">", ">=", "AND", "OR"
        public Expr Left { get; init; }
        public Expr Right { get; init; }
    }

    internal sealed class IsNullExpr : Expr
    {
        public Expr Operand { get; init; }
        public bool Not { get; init; }
    }

    internal sealed class BetweenExpr : Expr
    {
        public Expr Operand { get; init; }
        public Expr Low { get; init; }
        public Expr High { get; init; }
        public bool Not { get; init; }
    }

    internal sealed class InExpr : Expr
    {
        public Expr Operand { get; init; }
        public List<Expr> Items { get; init; }
        public SelectStmt Query { get; init; }
        public bool Not { get; init; }
    }

    internal sealed class ExistsExpr : Expr
    {
        public SelectStmt Query { get; init; }
    }

    internal sealed class LikeExpr : Expr
    {
        public Expr Operand { get; init; }
        public Expr Pattern { get; init; }
        public Expr Escape { get; init; }
        public bool Not { get; init; }
        public bool IgnoreCase { get; init; }
    }

    internal sealed class CaseExpr : Expr
    {
        public Expr Operand { get; init; }
        public List<(Expr When, Expr Then)> Branches { get; init; }
        public Expr Else { get; init; }
    }

    internal sealed class CastExpr : Expr
    {
        public Expr Operand { get; init; }
        public SqlType Type { get; init; }
    }

    /// <summary>A function or an aggregate: <c>LOWER(x)</c>, <c>COUNT(*)</c>, <c>COUNT(DISTINCT x)</c>. The special
    /// forms (<c>EXTRACT(part FROM x)</c>, <c>POSITION(a IN b)</c>, <c>SUBSTRING(s FROM a FOR b)</c>,
    /// <c>TRIM(BOTH c FROM s)</c>) are parsed into ordinary arguments.</summary>
    internal sealed class FunctionExpr : Expr
    {
        public string Name { get; init; } // upper case
        public List<Expr> Args { get; init; }
        public bool Star { get; init; }
        public bool Distinct { get; init; }
    }

    internal sealed class SubscriptExpr : Expr
    {
        public Expr Target { get; init; }
        public Expr Index { get; init; }
    }

    internal sealed class SubqueryExpr : Expr
    {
        public SelectStmt Query { get; init; }
    }

    /// <summary><c>x op ANY (array or subquery)</c>, <c>x op ALL (...)</c>.</summary>
    internal sealed class QuantifiedExpr : Expr
    {
        public Expr Left { get; init; }
        public string Op { get; init; }
        public bool All { get; init; }
        public Expr Array { get; init; }
        public SelectStmt Query { get; init; }
    }

    internal sealed class SelectItem
    {
        public Expr Expr { get; init; }
        public string Alias { get; init; }
        /// <summary><c>*</c> or <c>alias.*</c>: Expr is null, and Qualifier names the source (null for <c>*</c>).</summary>
        public bool Star { get; init; }
        public string Qualifier { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }
    }

    internal enum JoinKind { First, Inner, Left, Cross }

    internal sealed class FromItem
    {
        public JoinKind Join { get; init; }
        /// <summary>A table's name as written (a short or full name).</summary>
        public string Table { get; init; }
        public SelectStmt Subquery { get; init; }
        /// <summary><c>alias.collection</c>: the source and the collection.</summary>
        public string CollectionOf { get; init; }
        public string Collection { get; init; }
        public string Alias { get; init; }
        public Expr On { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }
    }

    internal sealed class OrderItem
    {
        public Expr Expr { get; init; }
        public bool Desc { get; init; }
        /// <summary>NULLS FIRST (true), NULLS LAST (false), or the default for the direction (null).</summary>
        public bool? NullsFirst { get; init; }
    }

    internal sealed class SelectStmt
    {
        public bool Explain { get; set; }
        public bool Distinct { get; init; }
        public List<SelectItem> Items { get; init; }
        public List<FromItem> From { get; init; } = new();
        public Expr Where { get; init; }
        public List<Expr> GroupBy { get; init; } = new();
        public Expr Having { get; init; }
        public List<OrderItem> OrderBy { get; init; } = new();
        public Expr Limit { get; init; }
        public Expr Offset { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }
    }
}
