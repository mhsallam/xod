using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Xod.Sql
{
    internal enum TokenKind
    {
        End,
        Word,           // a bare name or keyword
        QuotedName,     // "Name" or [Name]
        String,         // 'text'
        Integer,        // 42
        Decimal,        // 4.5
        Double,         // 1e9
        Parameter,      // @name or ?
        Symbol,         // = <> != < <= > >= + - * / % || ( ) , . ; [ ]
    }

    internal sealed class Token
    {
        public TokenKind Kind { get; init; }
        /// <summary>A word as written; a quoted name or a string without its quotes; a symbol; a parameter's name
        /// (empty for <c>?</c>).</summary>
        public string Text { get; init; }
        public int Line { get; init; }
        public int Column { get; init; }

        public bool IsWord(string keyword) => Kind == TokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);
        public bool IsSymbol(string symbol) => Kind == TokenKind.Symbol && Text == symbol;
        public override string ToString() => Kind == TokenKind.End ? "the end of the query" : Kind == TokenKind.String ? $"'{Text}'" : Text;
    }

    /// <summary>
    /// Splits a query into tokens (PLAN-045 sections 5 and 8). <c>"Name"</c> and <c>[Name]</c> are quoted names
    /// (D6); a <c>[</c> right after an operand (a name, a closing bracket or parenthesis) is a subscript instead
    /// (<c>Tags['k']</c>), so a bracketed alias needs <c>AS</c>. Comments are <c>--</c> to the line's end and
    /// <c>/* ... */</c>.
    /// </summary>
    internal static class SqlLexer
    {
        /// <summary>
        /// The words a name can't be unless quoted (PLAN-045 section 8): PostgreSQL's reserved words with ILIKE, LIMIT
        /// and OFFSET. The SQL-92 list the spec first named also reserves Year, Day and Value, which the accepted
        /// conformance cases use as column names.
        /// </summary>
        public static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        {
            "ALL", "ANALYSE", "ANALYZE", "AND", "ANY", "ARRAY", "AS", "ASC", "ASYMMETRIC", "BOTH", "CASE", "CAST",
            "CHECK", "COLLATE", "COLUMN", "CONSTRAINT", "CREATE", "CURRENT_CATALOG", "CURRENT_DATE", "CURRENT_ROLE",
            "CURRENT_TIME", "CURRENT_TIMESTAMP", "CURRENT_USER", "DEFAULT", "DEFERRABLE", "DESC", "DISTINCT", "DO",
            "ELSE", "END", "EXCEPT", "FALSE", "FETCH", "FOR", "FOREIGN", "FROM", "GRANT", "GROUP", "HAVING", "IN",
            "INITIALLY", "INTERSECT", "INTO", "LATERAL", "LEADING", "LIMIT", "LOCALTIME", "LOCALTIMESTAMP", "NOT",
            "NULL", "OFFSET", "ON", "ONLY", "OR", "ORDER", "PLACING", "PRIMARY", "REFERENCES", "RETURNING", "SELECT",
            "SESSION_USER", "SOME", "SYMMETRIC", "TABLE", "THEN", "TO", "TRAILING", "TRUE", "UNION", "UNIQUE", "USER",
            "USING", "VARIADIC", "WHEN", "WHERE", "WINDOW", "WITH",
            "AUTHORIZATION", "BINARY", "COLLATION", "CONCURRENTLY", "CROSS", "CURRENT_SCHEMA", "FREEZE", "FULL",
            "ILIKE", "INNER", "IS", "ISNULL", "JOIN", "LEFT", "LIKE", "NATURAL", "NOTNULL", "OUTER", "OVERLAPS",
            "RIGHT", "SIMILAR", "TABLESAMPLE", "VERBOSE",
        };

        public static List<Token> Tokenize(string sql)
        {
            var tokens = new List<Token>();
            int i = 0, line = 1, lineStart = 0;
            while (true)
            {
                // Spaces and comments.
                while (i < sql.Length)
                {
                    char ch = sql[i];
                    if (ch == '\n') { i++; line++; lineStart = i; }
                    else if (char.IsWhiteSpace(ch)) i++;
                    else if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                    {
                        while (i < sql.Length && sql[i] != '\n') i++;
                    }
                    else if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                    {
                        int startLine = line, startCol = i - lineStart + 1;
                        i += 2;
                        while (i < sql.Length && !(sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/'))
                        {
                            if (sql[i] == '\n') { line++; lineStart = i + 1; }
                            i++;
                        }
                        if (i >= sql.Length) throw Error("a comment isn't closed", startLine, startCol);
                        i += 2;
                    }
                    else break;
                }

                int col = i - lineStart + 1;
                if (i >= sql.Length)
                {
                    tokens.Add(new Token { Kind = TokenKind.End, Text = "", Line = line, Column = col });
                    return tokens;
                }

                char c = sql[i];
                Token previous = tokens.Count > 0 ? tokens[^1] : null;

                if (c == '\'')
                {
                    var sb = new StringBuilder();
                    int start = i++;
                    while (true)
                    {
                        if (i >= sql.Length) throw Error("a text literal isn't closed", line, col);
                        if (sql[i] == '\'')
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                            i++;
                            break;
                        }
                        if (sql[i] == '\n') { line++; lineStart = i + 1; }
                        sb.Append(sql[i++]);
                    }
                    tokens.Add(new Token { Kind = TokenKind.String, Text = sb.ToString(), Line = line, Column = col });
                    continue;
                }

                if (c == '"' || (c == '[' && !EndsOperand(previous)))
                {
                    char close = c == '"' ? '"' : ']';
                    var sb = new StringBuilder();
                    i++;
                    while (true)
                    {
                        if (i >= sql.Length) throw Error("a quoted name isn't closed", line, col);
                        if (sql[i] == close)
                        {
                            if (i + 1 < sql.Length && sql[i + 1] == close) { sb.Append(close); i += 2; continue; }
                            i++;
                            break;
                        }
                        sb.Append(sql[i++]);
                    }
                    if (sb.Length == 0) throw Error("a quoted name is empty", line, col);
                    tokens.Add(new Token { Kind = TokenKind.QuotedName, Text = sb.ToString(), Line = line, Column = col });
                    continue;
                }

                if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < sql.Length && char.IsAsciiDigit(sql[i + 1])))
                {
                    int start = i;
                    while (i < sql.Length && char.IsAsciiDigit(sql[i])) i++;
                    bool dot = false, exp = false;
                    if (i < sql.Length && sql[i] == '.')
                    {
                        dot = true;
                        i++;
                        while (i < sql.Length && char.IsAsciiDigit(sql[i])) i++;
                    }
                    if (i < sql.Length && (sql[i] == 'e' || sql[i] == 'E'))
                    {
                        int save = i;
                        i++;
                        if (i < sql.Length && (sql[i] == '+' || sql[i] == '-')) i++;
                        if (i < sql.Length && char.IsAsciiDigit(sql[i]))
                        {
                            exp = true;
                            while (i < sql.Length && char.IsAsciiDigit(sql[i])) i++;
                        }
                        else i = save;
                    }
                    if (i < sql.Length && (char.IsLetter(sql[i]) || sql[i] == '_'))
                        throw Error($"'{sql.Substring(start, i - start + 1)}' isn't a number", line, col);
                    tokens.Add(new Token
                    {
                        Kind = exp ? TokenKind.Double : dot ? TokenKind.Decimal : TokenKind.Integer,
                        Text = sql.Substring(start, i - start),
                        Line = line,
                        Column = col
                    });
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_' || sql[i] == '$')) i++;
                    tokens.Add(new Token { Kind = TokenKind.Word, Text = sql.Substring(start, i - start), Line = line, Column = col });
                    continue;
                }

                if (c == '@')
                {
                    int start = ++i;
                    while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                    if (i == start) throw Error("'@' must name a parameter", line, col);
                    tokens.Add(new Token { Kind = TokenKind.Parameter, Text = sql.Substring(start, i - start), Line = line, Column = col });
                    continue;
                }

                if (c == '?')
                {
                    i++;
                    tokens.Add(new Token { Kind = TokenKind.Parameter, Text = "", Line = line, Column = col });
                    continue;
                }

                string two = i + 1 < sql.Length ? sql.Substring(i, 2) : null;
                if (two is "<>" or "!=" or "<=" or ">=" or "||")
                {
                    i += 2;
                    tokens.Add(new Token { Kind = TokenKind.Symbol, Text = two, Line = line, Column = col });
                    continue;
                }
                if ("=<>+-*/%(),.;[]".IndexOf(c) >= 0)
                {
                    i++;
                    tokens.Add(new Token { Kind = TokenKind.Symbol, Text = c.ToString(), Line = line, Column = col });
                    continue;
                }

                throw Error($"'{c}' isn't part of XOD SQL", line, col);
            }
        }

        // A '[' after one of these is a subscript: an operand has just ended. A keyword that isn't reserved but is
        // always followed by an operand (ORDER BY [Name]) doesn't end one.
        private static bool EndsOperand(Token t) =>
            t != null && (t.Kind == TokenKind.QuotedName || t.IsSymbol(")") || t.IsSymbol("]") ||
                          (t.Kind == TokenKind.Word && !Reserved.Contains(t.Text) && !LeadsOperand.Contains(t.Text)));

        private static readonly HashSet<string> LeadsOperand = new(StringComparer.OrdinalIgnoreCase)
        {
            "BY", "BETWEEN", "EXISTS", "ESCAPE", "EXPLAIN", "NULLS",
        };

        public static XodSqlException Error(string message, int line, int column) =>
            new XodSqlException("XQ001", message, line, column);

        public static decimal ParseDecimal(string text) =>
            decimal.Parse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
    }
}
