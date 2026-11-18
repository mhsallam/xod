using System;

namespace Xod.Services
{
    /// <summary>
    /// The one reader of where a page's rows are in its XML bytes (ISS-143). A page is
    /// <c>&lt;TablePage&gt;&lt;Rows&gt;&lt;Row code="…"&gt;…&lt;/Row&gt;…&lt;/Rows&gt;&lt;/TablePage&gt;</c>, with
    /// blanks where rows were deleted or shrunk. A row ends where its element does, found by following element depth:
    /// a value can hold <c>&lt;/Row&gt;</c> or <c>&lt;Row code="</c> (a <c>[Markup]</c> value, in CDATA), and a property
    /// or class named <c>Row</c> writes a <c>&lt;Row&gt;</c> element inside every row. The readers looked for those byte
    /// patterns instead, eleven of them, so such a row was cut short or a forged one started inside it. Text outside
    /// CDATA is escaped, so a raw <c>&lt;</c> starts markup. A page this can't follow is damaged:
    /// <see cref="DatabaseFileException"/>, never fewer rows.
    /// </summary>
    internal static class PageRows
    {
        private static ReadOnlySpan<byte> RowOpen => "<Row code=\""u8;

        /// <summary>
        /// The offset just past the page's <c>&lt;Rows&gt;</c> open tag, where its rows begin, or -1 when the page has
        /// no rows element or an empty one (<c>&lt;Rows/&gt;</c>).
        /// </summary>
        internal static int RowsStart(ReadOnlySpan<byte> page, string pageName)
        {
            // The header before the rows holds XOD's own markup alone, so the first "<Rows" is the element.
            int at = page.IndexOf("<Rows"u8);
            if (at < 0)
                return -1;
            int end = TagEnd(page, at);
            if (end < 0)
                throw Damaged(pageName, "its rows element doesn't close");
            return page[end - 2] == (byte)'/' ? -1 : end;
        }

        /// <summary>
        /// The next row at or after <paramref name="cursor"/>, a position between rows; the cursor moves past it.
        /// False at the end of the rows.
        /// </summary>
        internal static bool TryNext(ReadOnlySpan<byte> page, ref int cursor, string pageName, out int start, out int length, out ReadOnlySpan<byte> code)
        {
            start = -1;
            length = 0;
            code = default;
            if (cursor < 0)
                return false;

            int i = cursor;
            while (i < page.Length && IsBlank(page[i]))
                i++;
            if (i >= page.Length)
                throw Damaged(pageName, "it ends inside its rows");

            ReadOnlySpan<byte> rest = page.Slice(i);
            if (rest.StartsWith("</Rows"u8))
            {
                cursor = i;
                return false;
            }
            if (!rest.StartsWith(RowOpen))
                throw Damaged(pageName, "it holds something other than a row at offset " + i);

            int quote = rest.Slice(RowOpen.Length).IndexOf((byte)'"');
            if (quote <= 0)
                throw Damaged(pageName, "a row at offset " + i + " has no code");
            int end = ElementEnd(page, i);
            if (end < 0)
                throw Damaged(pageName, "the row at offset " + i + " doesn't close");

            start = i;
            length = end - i;
            code = rest.Slice(RowOpen.Length, quote);
            cursor = end;
            return true;
        }

        /// <summary>
        /// The end (exclusive) of the element that starts at <paramref name="start"/>, by element depth, CDATA,
        /// comments and processing instructions skipped; -1 when the page ends first or closes more than it opened.
        /// </summary>
        internal static int ElementEnd(ReadOnlySpan<byte> page, int start)
        {
            int depth = 0;
            int i = start;
            while (i < page.Length)
            {
                int lt = page.Slice(i).IndexOf((byte)'<');
                if (lt < 0)
                    return -1;
                i += lt;
                ReadOnlySpan<byte> rest = page.Slice(i);

                if (rest.StartsWith("<![CDATA["u8))
                {
                    int close = rest.IndexOf("]]>"u8);
                    if (close < 0)
                        return -1;
                    i += close + 3;
                    continue;
                }
                if (rest.StartsWith("<!--"u8))
                {
                    int close = rest.IndexOf("-->"u8);
                    if (close < 0)
                        return -1;
                    i += close + 3;
                    continue;
                }
                if (rest.StartsWith("<?"u8))
                {
                    int close = rest.IndexOf("?>"u8);
                    if (close < 0)
                        return -1;
                    i += close + 2;
                    continue;
                }

                int tagEnd = TagEnd(page, i);
                if (tagEnd < 0)
                    return -1;

                if (rest.Length > 1 && rest[1] == (byte)'/')
                {
                    depth--;
                    if (depth == 0)
                        return tagEnd;
                    if (depth < 0)
                        return -1;
                }
                else if (page[tagEnd - 2] != (byte)'/')
                {
                    depth++;
                }
                else if (depth == 0)
                {
                    return tagEnd; // the element itself is empty: <Row code="1"/>
                }

                i = tagEnd;
            }

            return -1;
        }

        /// <summary>
        /// The end (exclusive) of the tag that starts at <paramref name="start"/>: its '>', past quoted attribute
        /// values; -1 when the page ends first. It jumps from quote to quote by a vectorized search, where it looked at
        /// every byte of the tag.
        /// </summary>
        internal static int TagEnd(ReadOnlySpan<byte> page, int start)
        {
            int i = start + 1;
            while (i < page.Length)
            {
                int at = page.Slice(i).IndexOfAny((byte)'>', (byte)'"', (byte)'\'');
                if (at < 0)
                    return -1;
                i += at;
                byte c = page[i];
                if (c == (byte)'>')
                    return i + 1;
                int close = page.Slice(i + 1).IndexOf(c);
                if (close < 0)
                    return -1;
                i += close + 2;
            }
            return -1;
        }

        private static bool IsBlank(byte b) => b == (byte)' ' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\t';

        internal static DatabaseFileException Damaged(string pageName, string what) =>
            new DatabaseFileException($"The page '{pageName ?? "?"}' is damaged: {what}.");
    }
}
