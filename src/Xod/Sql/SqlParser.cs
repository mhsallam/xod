using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Xod.Sql
{
    /// <summary>
    /// Parses one query (PLAN-045 section 5): <c>[EXPLAIN] SELECT ...</c>. Writes, <c>WITH</c>, set operations,
    /// window functions and <c>RIGHT</c>/<c>FULL</c> joins are refused with <c>XQ001</c> and a message saying they
    /// aren't in this version.
    /// </summary>
    internal sealed class SqlParser
    {
        private readonly List<Token> tokens;
        private int pos;
        private int positionalParameters;

        private SqlParser(List<Token> tokens) => this.tokens = tokens;

        /// <summary>The number of <c>?</c> parameters the query has.</summary>
        public int PositionalParameters => positionalParameters;

        public static SelectStmt Parse(string sql, out int positionalParameters)
        {
            if (string.IsNullOrWhiteSpace(sql))
                throw new XodSqlException("XQ001", "the query is empty", 1, 1);
            var parser = new SqlParser(SqlLexer.Tokenize(sql));
            var stmt = parser.ParseStatement();
            positionalParameters = parser.positionalParameters;
            return stmt;
        }

        private Token Peek(int ahead = 0) => tokens[Math.Min(pos + ahead, tokens.Count - 1)];
        private Token Next() => tokens[Math.Min(pos++, tokens.Count - 1)];

        private bool AcceptWord(string keyword)
        {
            if (!Peek().IsWord(keyword)) return false;
            pos++;
            return true;
        }

        private bool AcceptSymbol(string symbol)
        {
            if (!Peek().IsSymbol(symbol)) return false;
            pos++;
            return true;
        }

        private void ExpectWord(string keyword)
        {
            if (!AcceptWord(keyword)) throw Expected(keyword);
        }

        private void ExpectSymbol(string symbol)
        {
            if (!AcceptSymbol(symbol)) throw Expected($"'{symbol}'");
        }

        private XodSqlException Expected(string what)
        {
            var t = Peek();
            return SqlLexer.Error($"expected {what}, found {Describe(t)}", t.Line, t.Column);
        }

        private static string Describe(Token t) => t.Kind switch
        {
            TokenKind.End => "the end of the query",
            TokenKind.String => $"'{t.Text}'",
            TokenKind.QuotedName => $"\"{t.Text}\"",
            TokenKind.Parameter => t.Text.Length == 0 ? "?" : "@" + t.Text,
            _ => $"'{t.Text}'",
        };

        private static XodSqlException NotInVersion1(Token t, string what) =>
            SqlLexer.Error($"{what} isn't in XOD SQL version 1", t.Line, t.Column);

        // ---- statements ----

        private SelectStmt ParseStatement()
        {
            var first = Peek();
            bool explain = AcceptWord("EXPLAIN");
            var start = Peek();
            if (start.Kind == TokenKind.Word)
            {
                switch (start.Text.ToUpperInvariant())
                {
                    case "INSERT": case "UPDATE": case "DELETE": case "MERGE": case "UPSERT": case "REPLACE":
                        throw SqlLexer.Error($"{start.Text.ToUpperInvariant()} isn't in XOD SQL version 1: it reads only; SQL writes come in a later release (PLAN-045 D3). Write with the C# API", start.Line, start.Column);
                    case "CREATE": case "ALTER": case "DROP": case "TRUNCATE":
                        throw SqlLexer.Error($"{start.Text.ToUpperInvariant()} isn't XOD SQL: the classes are the schema", start.Line, start.Column);
                    case "BEGIN": case "COMMIT": case "ROLLBACK": case "START":
                        throw SqlLexer.Error("transactions aren't in XOD SQL version 1: a query reads one state of the database", start.Line, start.Column);
                    case "WITH":
                        throw NotInVersion1(start, "WITH");
                }
            }
            if (!start.IsWord("SELECT"))
                throw SqlLexer.Error($"a query starts with SELECT, found {Describe(start)}", start.Line, start.Column);

            var stmt = ParseQuery();
            stmt.Explain = explain;
            AcceptSymbol(";");
            var end = Peek();
            if (end.Kind != TokenKind.End)
            {
                if (end.IsWord("UNION") || end.IsWord("INTERSECT") || end.IsWord("EXCEPT"))
                    throw NotInVersion1(end, end.Text.ToUpperInvariant());
                throw SqlLexer.Error($"unexpected {Describe(end)}" + (end.Kind == TokenKind.Word && SqlLexer.Reserved.Contains(end.Text) ? "" : "; a name that is a reserved word must be quoted"), end.Line, end.Column);
            }
            _ = first;
            return stmt;
        }

        private SelectStmt ParseQuery()
        {
            var selectToken = Peek();
            ExpectWord("SELECT");
            bool distinct = AcceptWord("DISTINCT");
            if (!distinct) AcceptWord("ALL");

            var items = new List<SelectItem>();
            do items.Add(ParseSelectItem());
            while (AcceptSymbol(","));

            var from = new List<FromItem>();
            if (AcceptWord("FROM"))
            {
                from.Add(ParseSource(JoinKind.First));
                while (true)
                {
                    if (AcceptSymbol(","))
                    {
                        from.Add(ParseSource(JoinKind.Cross));
                        continue;
                    }
                    var t = Peek();
                    if (t.IsWord("RIGHT") || t.IsWord("FULL") || t.IsWord("NATURAL"))
                        throw NotInVersion1(t, t.Text.ToUpperInvariant() + " JOIN");
                    if (AcceptWord("CROSS"))
                    {
                        ExpectWord("JOIN");
                        from.Add(ParseSource(JoinKind.Cross));
                        continue;
                    }
                    JoinKind kind;
                    if (AcceptWord("JOIN")) kind = JoinKind.Inner;
                    else if (AcceptWord("INNER")) { ExpectWord("JOIN"); kind = JoinKind.Inner; }
                    else if (AcceptWord("LEFT")) { AcceptWord("OUTER"); ExpectWord("JOIN"); kind = JoinKind.Left; }
                    else break;
                    from.Add(ParseSource(kind));
                }
            }

            Expr where = AcceptWord("WHERE") ? ParseExpr() : null;
            var groupBy = new List<Expr>();
            if (AcceptWord("GROUP"))
            {
                ExpectWord("BY");
                do groupBy.Add(ParseExpr());
                while (AcceptSymbol(","));
            }
            Expr having = AcceptWord("HAVING") ? ParseExpr() : null;
            if (Peek().IsWord("WINDOW")) throw NotInVersion1(Peek(), "WINDOW");

            var orderBy = new List<OrderItem>();
            if (AcceptWord("ORDER"))
            {
                ExpectWord("BY");
                do
                {
                    var e = ParseExpr();
                    bool desc = false;
                    if (AcceptWord("DESC")) desc = true;
                    else AcceptWord("ASC");
                    bool? nullsFirst = null;
                    if (AcceptWord("NULLS"))
                    {
                        if (AcceptWord("FIRST")) nullsFirst = true;
                        else if (AcceptWord("LAST")) nullsFirst = false;
                        else throw Expected("FIRST or LAST");
                    }
                    orderBy.Add(new OrderItem { Expr = e, Desc = desc, NullsFirst = nullsFirst });
                }
                while (AcceptSymbol(","));
            }

            Expr limit = null, offset = null;
            for (int i = 0; i < 2; i++)
            {
                if (limit == null && AcceptWord("LIMIT")) limit = ParseExpr();
                else if (offset == null && AcceptWord("OFFSET")) { offset = ParseExpr(); if (!AcceptWord("ROWS")) AcceptWord("ROW"); }
            }
            if (Peek().IsWord("FETCH")) throw NotInVersion1(Peek(), "FETCH (use LIMIT)");

            return new SelectStmt
            {
                Distinct = distinct,
                Items = items,
                From = from,
                Where = where,
                GroupBy = groupBy,
                Having = having,
                OrderBy = orderBy,
                Limit = limit,
                Offset = offset,
                Line = selectToken.Line,
                Column = selectToken.Column,
            };
        }

        private SelectItem ParseSelectItem()
        {
            var t = Peek();
            if (AcceptSymbol("*"))
                return new SelectItem { Star = true, Line = t.Line, Column = t.Column };
            if (IsName(t) && Peek(1).IsSymbol(".") && Peek(2).IsSymbol("*"))
            {
                pos += 3;
                return new SelectItem { Star = true, Qualifier = t.Text, Line = t.Line, Column = t.Column };
            }
            var e = ParseExpr();
            string alias = null;
            if (AcceptWord("AS")) alias = ParseName("a name after AS");
            else if (IsName(Peek()) && !IsClauseWord(Peek())) alias = Next().Text;
            return new SelectItem { Expr = e, Alias = alias, Line = t.Line, Column = t.Column };
        }

        // Words that start the next clause rather than name an alias.
        private static bool IsClauseWord(Token t) =>
            t.Kind == TokenKind.Word && (t.IsWord("EXPLAIN") || t.IsWord("WINDOW") || t.IsWord("FETCH") || t.IsWord("ROWS"));

        private FromItem ParseSource(JoinKind join)
        {
            var t = Peek();
            FromItem item;
            if (AcceptSymbol("("))
            {
                if (!Peek().IsWord("SELECT")) throw Expected("SELECT");
                var sub = ParseQuery();
                ExpectSymbol(")");
                AcceptWord("AS");
                if (!IsName(Peek())) throw Expected("a name for the subquery (a subquery in FROM needs one)");
                string alias = Next().Text;
                item = new FromItem { Join = join, Subquery = sub, Alias = alias, Line = t.Line, Column = t.Column };
            }
            else
            {
                string first = ParseName("a table");
                string collectionOf = null, collection = null, table = null;
                if (AcceptSymbol("."))
                {
                    collectionOf = first;
                    collection = ParseLabel("a collection");
                    if (Peek().IsSymbol(".")) throw Expected("one collection: a table with dots in its name is quoted (\"MyApp.Shop.Customer\")");
                }
                else
                {
                    table = first;
                }
                string alias = null;
                if (AcceptWord("AS")) alias = ParseName("a name after AS");
                else if (IsName(Peek()) && !IsJoinWord(Peek())) alias = Next().Text;
                item = new FromItem { Join = join, Table = table, CollectionOf = collectionOf, Collection = collection, Alias = alias, Line = t.Line, Column = t.Column };
            }

            if (join == JoinKind.Inner || join == JoinKind.Left)
            {
                if (AcceptWord("ON"))
                {
                    item = new FromItem
                    {
                        Join = item.Join, Table = item.Table, Subquery = item.Subquery, CollectionOf = item.CollectionOf,
                        Collection = item.Collection, Alias = item.Alias, On = ParseExpr(), Line = item.Line, Column = item.Column
                    };
                }
                else if (item.Collection == null)
                {
                    if (Peek().IsWord("USING")) throw NotInVersion1(Peek(), "JOIN ... USING (use ON)");
                    throw Expected("ON");
                }
            }
            return item;
        }

        private static bool IsJoinWord(Token t) =>
            t.Kind == TokenKind.Word && (t.IsWord("WHERE") || t.IsWord("JOIN") || t.IsWord("LEFT") || t.IsWord("INNER") ||
                                         t.IsWord("CROSS") || t.IsWord("RIGHT") || t.IsWord("FULL") || t.IsWord("ON") ||
                                         t.IsWord("GROUP") || t.IsWord("ORDER") || t.IsWord("LIMIT") || t.IsWord("OFFSET") ||
                                         t.IsWord("HAVING") || t.IsWord("NATURAL") || t.IsWord("UNION") || t.IsWord("WINDOW"));

        private static bool IsName(Token t) =>
            t.Kind == TokenKind.QuotedName || (t.Kind == TokenKind.Word && !SqlLexer.Reserved.Contains(t.Text));

        private string ParseName(string what)
        {
            var t = Peek();
            if (t.Kind == TokenKind.QuotedName || (t.Kind == TokenKind.Word && !SqlLexer.Reserved.Contains(t.Text)))
            {
                pos++;
                return t.Text;
            }
            if (t.Kind == TokenKind.Word)
                throw SqlLexer.Error($"'{t.Text}' is a reserved word: quote it (\"{t.Text}\") to use it as a name", t.Line, t.Column);
            throw Expected(what);
        }

        // After a '.', any word is a name, reserved or not, as PostgreSQL's ColLabel: l.Order.Customer.Name.
        private string ParseLabel(string what)
        {
            var t = Peek();
            if (t.Kind == TokenKind.QuotedName || t.Kind == TokenKind.Word)
            {
                pos++;
                return t.Text;
            }
            throw Expected(what);
        }

        // ---- expressions ----

        private Expr ParseExpr() => ParseOr();

        private Expr ParseOr()
        {
            var left = ParseAnd();
            while (Peek().IsWord("OR"))
            {
                var t = Next();
                left = new BinaryExpr { Op = "OR", Left = left, Right = ParseAnd(), Line = t.Line, Column = t.Column };
            }
            return left;
        }

        private Expr ParseAnd()
        {
            var left = ParseNot();
            while (Peek().IsWord("AND"))
            {
                var t = Next();
                left = new BinaryExpr { Op = "AND", Left = left, Right = ParseNot(), Line = t.Line, Column = t.Column };
            }
            return left;
        }

        private Expr ParseNot()
        {
            var t = Peek();
            if (AcceptWord("NOT"))
                return new UnaryExpr { Op = "NOT", Operand = ParseNot(), Line = t.Line, Column = t.Column };
            return ParseComparison();
        }

        private Expr ParseComparison()
        {
            var left = ParseConcat();
            while (true)
            {
                var t = Peek();
                if (t.Kind == TokenKind.Symbol && t.Text is "=" or "<>" or "!=" or "<" or "<=" or ">" or ">=")
                {
                    pos++;
                    string op = t.Text == "!=" ? "<>" : t.Text;
                    var q = Peek();
                    if ((q.IsWord("ANY") || q.IsWord("SOME") || q.IsWord("ALL")) && Peek(1).IsSymbol("("))
                    {
                        pos += 2;
                        bool all = q.IsWord("ALL");
                        if (Peek().IsWord("SELECT"))
                        {
                            var sub = ParseQuery();
                            ExpectSymbol(")");
                            left = new QuantifiedExpr { Left = left, Op = op, All = all, Query = sub, Line = t.Line, Column = t.Column };
                        }
                        else
                        {
                            var arr = ParseExpr();
                            ExpectSymbol(")");
                            left = new QuantifiedExpr { Left = left, Op = op, All = all, Array = arr, Line = t.Line, Column = t.Column };
                        }
                        continue;
                    }
                    left = new BinaryExpr { Op = op, Left = left, Right = ParseConcat(), Line = t.Line, Column = t.Column };
                    continue;
                }

                if (t.IsWord("IS"))
                {
                    pos++;
                    bool not = AcceptWord("NOT");
                    if (AcceptWord("NULL"))
                    {
                        left = new IsNullExpr { Operand = left, Not = not, Line = t.Line, Column = t.Column };
                        continue;
                    }
                    if (Peek().IsWord("TRUE") || Peek().IsWord("FALSE"))
                    {
                        // x IS [NOT] TRUE: never unknown.
                        bool value = Next().IsWord("TRUE");
                        Expr test = new FunctionExpr { Name = value ? "$ISTRUE" : "$ISFALSE", Args = new List<Expr> { left }, Line = t.Line, Column = t.Column };
                        left = not ? new UnaryExpr { Op = "NOT", Operand = test, Line = t.Line, Column = t.Column } : test;
                        continue;
                    }
                    throw Expected("NULL, TRUE or FALSE after IS");
                }

                bool negated = false;
                if (t.IsWord("NOT") && (Peek(1).IsWord("IN") || Peek(1).IsWord("LIKE") || Peek(1).IsWord("ILIKE") || Peek(1).IsWord("BETWEEN")))
                {
                    pos++;
                    negated = true;
                    t = Peek();
                }

                if (AcceptWord("BETWEEN"))
                {
                    AcceptWord("SYMMETRIC");
                    var low = ParseConcat();
                    ExpectWord("AND");
                    var high = ParseConcat();
                    left = new BetweenExpr { Operand = left, Low = low, High = high, Not = negated, Line = t.Line, Column = t.Column };
                    continue;
                }
                if (AcceptWord("IN"))
                {
                    ExpectSymbol("(");
                    if (Peek().IsWord("SELECT"))
                    {
                        var sub = ParseQuery();
                        ExpectSymbol(")");
                        left = new InExpr { Operand = left, Query = sub, Not = negated, Line = t.Line, Column = t.Column };
                    }
                    else
                    {
                        var items = new List<Expr>();
                        do items.Add(ParseExpr());
                        while (AcceptSymbol(","));
                        ExpectSymbol(")");
                        left = new InExpr { Operand = left, Items = items, Not = negated, Line = t.Line, Column = t.Column };
                    }
                    continue;
                }
                if (t.IsWord("LIKE") || t.IsWord("ILIKE"))
                {
                    pos++;
                    var pattern = ParseConcat();
                    Expr escape = AcceptWord("ESCAPE") ? ParseConcat() : null;
                    left = new LikeExpr { Operand = left, Pattern = pattern, Escape = escape, Not = negated, IgnoreCase = t.IsWord("ILIKE"), Line = t.Line, Column = t.Column };
                    continue;
                }
                if (negated) throw Expected("IN, LIKE, ILIKE or BETWEEN after NOT");
                return left;
            }
        }

        private Expr ParseConcat()
        {
            var left = ParseAdditive();
            while (Peek().IsSymbol("||"))
            {
                var t = Next();
                left = new BinaryExpr { Op = "||", Left = left, Right = ParseAdditive(), Line = t.Line, Column = t.Column };
            }
            return left;
        }

        private Expr ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (Peek().IsSymbol("+") || Peek().IsSymbol("-"))
            {
                var t = Next();
                left = new BinaryExpr { Op = t.Text, Left = left, Right = ParseMultiplicative(), Line = t.Line, Column = t.Column };
            }
            return left;
        }

        private Expr ParseMultiplicative()
        {
            var left = ParseUnary();
            while (Peek().IsSymbol("*") || Peek().IsSymbol("/") || Peek().IsSymbol("%"))
            {
                var t = Next();
                left = new BinaryExpr { Op = t.Text, Left = left, Right = ParseUnary(), Line = t.Line, Column = t.Column };
            }
            return left;
        }

        private Expr ParseUnary()
        {
            var t = Peek();
            if (AcceptSymbol("-"))
            {
                var operand = ParseUnary();
                // A negative number is a literal, so -9223372036854775808 is an INTEGER.
                if (operand is LiteralExpr lit && !lit.Untyped)
                {
                    switch (lit.Value)
                    {
                        case decimal m when lit.Type == SqlType.Decimal && m == 9223372036854775808m:
                            return new LiteralExpr { Type = SqlType.Integer, Value = long.MinValue, Line = t.Line, Column = t.Column };
                        case long l:
                            return new LiteralExpr { Type = SqlType.Integer, Value = -l, Line = t.Line, Column = t.Column };
                        case decimal m:
                            return new LiteralExpr { Type = SqlType.Decimal, Value = -m, Line = t.Line, Column = t.Column };
                        case double d:
                            return new LiteralExpr { Type = SqlType.Double, Value = -d, Line = t.Line, Column = t.Column };
                    }
                }
                return new UnaryExpr { Op = "-", Operand = operand, Line = t.Line, Column = t.Column };
            }
            if (AcceptSymbol("+"))
                return new UnaryExpr { Op = "+", Operand = ParseUnary(), Line = t.Line, Column = t.Column };
            return ParsePostfix();
        }

        private Expr ParsePostfix()
        {
            var e = ParsePrimary();
            while (Peek().IsSymbol("["))
            {
                var t = Next();
                var index = ParseExpr();
                ExpectSymbol("]");
                e = new SubscriptExpr { Target = e, Index = index, Line = t.Line, Column = t.Column };
            }
            return e;
        }

        private Expr ParsePrimary()
        {
            var t = Peek();
            switch (t.Kind)
            {
                case TokenKind.Integer:
                    pos++;
                    if (long.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long l))
                        return new LiteralExpr { Type = SqlType.Integer, Value = l, Line = t.Line, Column = t.Column };
                    if (decimal.TryParse(t.Text, NumberStyles.None, CultureInfo.InvariantCulture, out decimal big))
                        return new LiteralExpr { Type = SqlType.Decimal, Value = big, Line = t.Line, Column = t.Column };
                    return new LiteralExpr { Type = SqlType.Double, Value = double.Parse(t.Text, CultureInfo.InvariantCulture), Line = t.Line, Column = t.Column };
                case TokenKind.Decimal:
                    pos++;
                    if (decimal.TryParse(t.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal m))
                        return new LiteralExpr { Type = SqlType.Decimal, Value = m, Line = t.Line, Column = t.Column };
                    return new LiteralExpr { Type = SqlType.Double, Value = double.Parse(t.Text, CultureInfo.InvariantCulture), Line = t.Line, Column = t.Column };
                case TokenKind.Double:
                    pos++;
                    return new LiteralExpr { Type = SqlType.Double, Value = double.Parse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture), Line = t.Line, Column = t.Column };
                case TokenKind.String:
                    pos++;
                    return new LiteralExpr { Type = SqlType.Text, Value = t.Text, Untyped = true, Line = t.Line, Column = t.Column };
                case TokenKind.Parameter:
                    pos++;
                    return t.Text.Length == 0
                        ? new ParameterExpr { Position = positionalParameters++, Line = t.Line, Column = t.Column }
                        : new ParameterExpr { Name = t.Text, Line = t.Line, Column = t.Column };
                case TokenKind.Symbol when t.Text == "(":
                {
                    pos++;
                    if (Peek().IsWord("SELECT"))
                    {
                        var sub = ParseQuery();
                        ExpectSymbol(")");
                        return new SubqueryExpr { Query = sub, Line = t.Line, Column = t.Column };
                    }
                    var inner = ParseExpr();
                    ExpectSymbol(")");
                    return inner;
                }
                case TokenKind.QuotedName:
                    return ParseNameExpr();
                case TokenKind.Word:
                    break;
                case TokenKind.End:
                    throw Expected("a value");
                default:
                    throw Expected("a value");
            }

            string upper = t.Text.ToUpperInvariant();
            switch (upper)
            {
                case "NULL":
                    pos++;
                    return new LiteralExpr { Type = SqlType.Null, Value = null, Line = t.Line, Column = t.Column };
                case "TRUE":
                case "FALSE":
                    pos++;
                    return new LiteralExpr { Type = SqlType.Boolean, Value = upper == "TRUE", Line = t.Line, Column = t.Column };
                case "CURRENT_DATE":
                case "CURRENT_TIMESTAMP":
                    pos++;
                    AcceptEmptyParens();
                    return new FunctionExpr { Name = upper, Args = new List<Expr>(), Line = t.Line, Column = t.Column };
                case "CASE":
                    return ParseCase();
                case "CAST":
                    return ParseCast();
                case "EXISTS":
                {
                    pos++;
                    ExpectSymbol("(");
                    if (!Peek().IsWord("SELECT")) throw Expected("SELECT");
                    var sub = ParseQuery();
                    ExpectSymbol(")");
                    return new ExistsExpr { Query = sub, Line = t.Line, Column = t.Column };
                }
            }

            // A typed literal: DATE '2026-11-08', TIMESTAMP '...', UUID '...'.
            if (Peek(1).Kind == TokenKind.String && TypedLiteralType(upper) is SqlType literalType)
            {
                pos++;
                var text = Next();
                if (!SqlLiterals.TryParse(literalType, text.Text, out object value))
                    throw SqlLexer.Error($"'{text.Text}' isn't a {SqlTypes.Name(literalType)}", text.Line, text.Column);
                return new LiteralExpr { Type = literalType, Value = value, Line = t.Line, Column = t.Column };
            }
            if (upper == "TIMESTAMP" && Peek(1).IsWord("WITH") && Peek(2).IsWord("TIME") && Peek(3).IsWord("ZONE") && Peek(4).Kind == TokenKind.String)
            {
                pos += 4;
                var text = Next();
                if (!SqlLiterals.TryParse(SqlType.TimestampTz, text.Text, out object value))
                    throw SqlLexer.Error($"'{text.Text}' isn't a TIMESTAMPTZ", text.Line, text.Column);
                return new LiteralExpr { Type = SqlType.TimestampTz, Value = value, Line = t.Line, Column = t.Column };
            }

            if (Peek(1).IsSymbol("(") && !SqlLexer.Reserved.Contains(t.Text))
                return ParseFunction();

            if (SqlLexer.Reserved.Contains(t.Text))
                throw SqlLexer.Error($"'{t.Text}' is a reserved word: quote it (\"{t.Text}\") to use it as a name", t.Line, t.Column);
            return ParseNameExpr();
        }

        private void AcceptEmptyParens()
        {
            if (Peek().IsSymbol("(") && Peek(1).IsSymbol(")")) pos += 2;
        }

        private static SqlType? TypedLiteralType(string word) => word switch
        {
            "DATE" => SqlType.Date,
            "TIME" => SqlType.Time,
            "TIMESTAMP" => SqlType.Timestamp,
            "TIMESTAMPTZ" => SqlType.TimestampTz,
            "INTERVAL" => SqlType.Interval,
            "UUID" => SqlType.Uuid,
            _ => null,
        };

        private Expr ParseNameExpr()
        {
            var t = Peek();
            var parts = new List<string> { ParseName("a name") };
            while (Peek().IsSymbol(".") && !Peek(1).IsSymbol("*"))
            {
                pos++;
                parts.Add(ParseLabel("a name after '.'"));
            }
            return new NameExpr { Parts = parts, Line = t.Line, Column = t.Column };
        }

        private Expr ParseCase()
        {
            var t = Next();
            Expr operand = Peek().IsWord("WHEN") ? null : ParseExpr();
            var branches = new List<(Expr, Expr)>();
            while (AcceptWord("WHEN"))
            {
                var when = ParseExpr();
                ExpectWord("THEN");
                branches.Add((when, ParseExpr()));
            }
            if (branches.Count == 0) throw Expected("WHEN");
            Expr otherwise = AcceptWord("ELSE") ? ParseExpr() : null;
            ExpectWord("END");
            return new CaseExpr { Operand = operand, Branches = branches, Else = otherwise, Line = t.Line, Column = t.Column };
        }

        private Expr ParseCast()
        {
            var t = Next();
            ExpectSymbol("(");
            var operand = ParseExpr();
            ExpectWord("AS");
            var typeToken = Peek();
            string name = ParseTypeName();
            if (!SqlTypes.TryParseName(name, out SqlType type))
                throw SqlLexer.Error($"'{name}' isn't a XOD SQL type", typeToken.Line, typeToken.Column);
            ExpectSymbol(")");
            return new CastExpr { Operand = operand, Type = type, Line = t.Line, Column = t.Column };
        }

        private string ParseTypeName()
        {
            var t = Peek();
            if (t.Kind != TokenKind.Word) throw Expected("a type");
            pos++;
            string name = t.Text.ToUpperInvariant();
            if (name == "DOUBLE" && AcceptWord("PRECISION")) name = "DOUBLE PRECISION";
            else if (name == "CHARACTER" && AcceptWord("VARYING")) name = "CHARACTER VARYING";
            else if (name == "TIMESTAMP" && Peek().IsWord("WITH") && Peek(1).IsWord("TIME") && Peek(2).IsWord("ZONE")) { pos += 3; name = "TIMESTAMPTZ"; }
            else if (name == "TIMESTAMP" && Peek().IsWord("WITHOUT") && Peek(1).IsWord("TIME") && Peek(2).IsWord("ZONE")) pos += 3;
            // A length or precision, as in VARCHAR(20) or DECIMAL(10, 2): accepted and ignored.
            if (AcceptSymbol("("))
            {
                do
                {
                    if (Peek().Kind != TokenKind.Integer) throw Expected("a number");
                    pos++;
                }
                while (AcceptSymbol(","));
                ExpectSymbol(")");
            }
            return name;
        }

        private Expr ParseFunction()
        {
            var t = Next();
            string name = t.Text.ToUpperInvariant();
            ExpectSymbol("(");
            var args = new List<Expr>();
            bool star = false, distinct = false;

            switch (name)
            {
                case "EXTRACT":
                {
                    var partToken = Peek();
                    string part = partToken.Kind == TokenKind.String ? Next().Text : ParseName("a date part").ToUpperInvariant();
                    ExpectWord("FROM");
                    args.Add(new LiteralExpr { Type = SqlType.Text, Value = part.ToUpperInvariant(), Line = partToken.Line, Column = partToken.Column });
                    args.Add(ParseExpr());
                    ExpectSymbol(")");
                    return Function(t, name, args, false, false);
                }
                case "POSITION":
                {
                    var needle = ParseConcat();
                    if (AcceptWord("IN"))
                    {
                        args.Add(needle);
                        args.Add(ParseExpr());
                    }
                    else
                    {
                        ExpectSymbol(",");
                        // POSITION(a, b) as some engines write it.
                        args.Add(needle);
                        args.Add(ParseExpr());
                    }
                    ExpectSymbol(")");
                    return Function(t, name, args, false, false);
                }
                case "SUBSTRING":
                case "SUBSTR":
                {
                    args.Add(ParseExpr());
                    if (AcceptWord("FROM"))
                    {
                        args.Add(ParseExpr());
                        if (AcceptWord("FOR")) args.Add(ParseExpr());
                    }
                    else if (AcceptSymbol(","))
                    {
                        args.Add(ParseExpr());
                        if (AcceptSymbol(",")) args.Add(ParseExpr());
                    }
                    ExpectSymbol(")");
                    return Function(t, "SUBSTRING", args, false, false);
                }
                case "TRIM":
                {
                    string side = "BOTH";
                    if (AcceptWord("LEADING")) side = "LEADING";
                    else if (AcceptWord("TRAILING")) side = "TRAILING";
                    else if (AcceptWord("BOTH")) side = "BOTH";
                    Expr chars = null, source;
                    if (AcceptWord("FROM"))
                    {
                        source = ParseExpr();
                    }
                    else
                    {
                        var first = ParseExpr();
                        if (AcceptWord("FROM")) { chars = first; source = ParseExpr(); }
                        else if (AcceptSymbol(",")) { source = first; chars = ParseExpr(); }
                        else source = first;
                    }
                    ExpectSymbol(")");
                    args.Add(new LiteralExpr { Type = SqlType.Text, Value = side, Line = t.Line, Column = t.Column });
                    args.Add(source);
                    if (chars != null) args.Add(chars);
                    return Function(t, name, args, false, false);
                }
            }

            if (AcceptSymbol("*"))
            {
                star = true;
            }
            else if (!Peek().IsSymbol(")"))
            {
                if (AcceptWord("DISTINCT")) distinct = true;
                else AcceptWord("ALL");
                do args.Add(ParseExpr());
                while (AcceptSymbol(","));
            }
            ExpectSymbol(")");
            if (Peek().IsWord("OVER")) throw NotInVersion1(Peek(), "window functions (OVER)");
            if (Peek().IsWord("FILTER") && Peek(1).IsSymbol("(")) throw NotInVersion1(Peek(), "FILTER");
            return Function(t, name, args, star, distinct);
        }

        private static FunctionExpr Function(Token t, string name, List<Expr> args, bool star, bool distinct) =>
            new() { Name = name, Args = args, Star = star, Distinct = distinct, Line = t.Line, Column = t.Column };
    }

    /// <summary>The text forms of typed literals (PLAN-045 section 4), also used when a text literal meets another
    /// type and takes it.</summary>
    internal static class SqlLiterals
    {
        public static bool TryParse(SqlType type, string text, out object value)
        {
            value = null;
            if (text == null) return false;
            string s = text.Trim();
            var inv = CultureInfo.InvariantCulture;
            switch (type)
            {
                case SqlType.Text:
                    value = text;
                    return true;
                case SqlType.Integer:
                    if (long.TryParse(s, NumberStyles.AllowLeadingSign, inv, out long l)) { value = l; return true; }
                    return false;
                case SqlType.Decimal:
                    if (decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, inv, out decimal m)) { value = m; return true; }
                    return false;
                case SqlType.Double:
                    if (double.TryParse(s, NumberStyles.Float, inv, out double d)) { value = d; return true; }
                    return false;
                case SqlType.Boolean:
                    switch (s.ToLowerInvariant())
                    {
                        case "true": case "t": case "yes": case "y": case "on": case "1": value = true; return true;
                        case "false": case "f": case "no": case "n": case "off": case "0": value = false; return true;
                        default: return false;
                    }
                case SqlType.Uuid:
                    if (Guid.TryParse(s, out Guid g)) { value = g; return true; }
                    return false;
                case SqlType.Timestamp:
                    if (DateTime.TryParse(s, inv, DateTimeStyles.RoundtripKind, out DateTime dt)) { value = dt; return true; }
                    return false;
                case SqlType.TimestampTz:
                    if (DateTimeOffset.TryParse(s, inv, DateTimeStyles.None, out DateTimeOffset dto)) { value = dto; return true; }
                    return false;
                case SqlType.Date:
                    if (DateOnly.TryParseExact(s, new[] { "yyyy-MM-dd", "yyyy-M-d" }, inv, DateTimeStyles.None, out DateOnly day)) { value = day; return true; }
                    return false;
                case SqlType.Time:
                    if (TimeOnly.TryParse(s, inv, DateTimeStyles.None, out TimeOnly time)) { value = time; return true; }
                    return false;
                case SqlType.Interval:
                    if (TimeSpan.TryParse(s, inv, out TimeSpan span)) { value = span; return true; }
                    return false;
                default:
                    return false;
            }
        }
    }
}
