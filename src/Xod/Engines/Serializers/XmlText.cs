using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Xod.Engines.Serializers
{
    /// <summary>
    /// The one character policy for XOD's XML (PLAN-046 section 4.2, ISS-051). A stored string must read
    /// back exactly, and XML can't carry every character raw:
    /// <list type="bullet">
    /// <item>a control character (U+0000 to U+001F except tab and LF, and U+FFFE, U+FFFF) isn't allowed raw;</item>
    /// <item>a raw CR is turned into LF by every parser (line-end normalisation);</item>
    /// <item>in an attribute, a raw tab, LF or CR becomes a space (attribute-value normalisation).</item>
    /// </list>
    /// So every writer writes those characters as numeric references (<c>&amp;#xD;</c>, <c>&amp;#x1;</c>),
    /// and every reader accepts references to control characters, which XML 1.0 forbids. XOD never writes
    /// a lone surrogate (<c>StoredValue.CheckText</c> refuses one); a reference to a surrogate in a file
    /// written elsewhere reads back as that UTF-16 unit, from both readers alike. Both XML writers (the document writer and the fast row writer) and
    /// every reader of a stored document go through here; the transaction journal, which holds only file
    /// names and base64, is the one XML file that doesn't.
    /// </summary>
    internal static class XmlText
    {
        /// <summary>For every XML read: references to any character, no DTD. Whitespace is kept by the
        /// reader and the padding dropped afterwards (<see cref="DropPadding"/>): a reader that ignores
        /// whitespace also drops a value made only of whitespace.</summary>
        internal static readonly XmlReaderSettings ReaderSettings = new XmlReaderSettings
        {
            IgnoreWhitespace = false,
            IgnoreComments = true,
            CheckCharacters = false,
            DtdProcessing = DtdProcessing.Prohibit,
        };

        /// <summary>For every XML document written. <see cref="NewLineHandling.Entitize"/> writes a CR in
        /// text, and a tab, LF or CR in an attribute, as a reference; with <c>CheckCharacters = false</c> a
        /// control character is written as a reference too.</summary>
        internal static readonly XmlWriterSettings DocumentWriterSettings = new XmlWriterSettings
        {
            Indent = false,
            CheckCharacters = false,
            NewLineHandling = NewLineHandling.Entitize,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = false,
        };

        private static readonly XmlWriterSettings RowWriterSettings = new XmlWriterSettings
        {
            Indent = false,
            CheckCharacters = false,
            NewLineHandling = NewLineHandling.Entitize,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            OmitXmlDeclaration = true,
            ConformanceLevel = ConformanceLevel.Fragment,
        };

        internal static XDocument Load(Stream stream)
        {
            using var reader = XmlReader.Create(stream, ReaderSettings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            DropPadding(document);
            return document;
        }

        internal static XElement Parse(string xml)
        {
            using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings);
            var element = XElement.Load(reader, LoadOptions.PreserveWhitespace);
            DropPadding(element);
            return element;
        }

        /// <summary>
        /// An element holds child elements or a text value, never both (PLAN-046 section 4.1), so
        /// whitespace beside child elements is padding (section 4.2: rows deleted or shrunk in place) and
        /// goes. Whitespace that is an element's whole content is its value and stays.
        /// </summary>
        private static void DropPadding(XContainer root)
        {
            var pending = new Stack<XContainer>();
            pending.Push(root);
            List<XText> padding = null;
            while (pending.Count > 0)
            {
                XContainer container = pending.Pop();
                if (container is XElement leaf && !leaf.HasElements)
                    continue;
                foreach (XNode node in container.Nodes())
                {
                    if (node is XElement child)
                        pending.Push(child);
                    else if (node is XText text && node is not XCData && IsXmlWhitespace(text.Value))
                        (padding ??= new List<XText>()).Add(text);
                }
            }
            if (padding != null)
                foreach (XText text in padding)
                    text.Remove();
        }

        private static bool IsXmlWhitespace(string s)
        {
            foreach (char c in s)
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r')
                    return false;
            return true;
        }

        internal static byte[] Serialize(XDocument document)
        {
            using var ms = new MemoryStream(65536);
            using (var writer = XmlWriter.Create(ms, DocumentWriterSettings))
                document.Save(writer);
            return ms.ToArray();
        }

        /// <summary>One element as UTF-8, without a declaration: a row written into a page in place.</summary>
        internal static byte[] SerializeElement(XElement element)
        {
            using var ms = new MemoryStream(1024);
            using (var writer = XmlWriter.Create(ms, RowWriterSettings))
                element.WriteTo(writer);
            return ms.ToArray();
        }

        /// <summary>Element text for a value. CDATA (wanted for <c>[Markup]</c>) can't hold a reference,
        /// and a parser turns a CR in it into LF, so a value with a character that needs a reference is
        /// written as text instead.</summary>
        internal static XText TextNode(string value, bool preferCData) =>
            preferCData && value.AsSpan().IndexOfAny(ReferencedOnly) < 0 ? new XCData(value) : new XText(value);

        private static readonly SearchValues<char> ReferencedOnly = SearchValues.Create(BuildTextSpecials().Substring(3));

        // ---- the fast row writer's half (element text only) ----

        /// <summary>Characters element text can't hold raw: the markup three, and those written as references.</summary>
        internal static readonly SearchValues<char> TextSpecials = SearchValues.Create(BuildTextSpecials());

        private static string BuildTextSpecials()
        {
            var sb = new StringBuilder("&<>");
            for (char c = '\0'; c < ' '; c++)
                if (NeedsReference(c)) sb.Append(c);
            sb.Append('￾').Append('￿');
            return sb.ToString();
        }

        /// <summary>A character element text must carry as a numeric reference.</summary>
        internal static bool NeedsReference(char c) =>
            (c < ' ' && c != '\t' && c != '\n') || c == '￾' || c == '￿';

        // ---- the fast row reader's half ----

        /// <summary>Decodes the five named entities and numeric references, as an XML parser would, but
        /// without refusing any character.</summary>
        internal static string Unescape(string raw)
        {
            int amp = raw.IndexOf('&');
            if (amp < 0) return raw;

            var sb = new StringBuilder(raw.Length);
            int start = 0;
            while (amp >= 0)
            {
                int semi = raw.IndexOf(';', amp + 1);
                if (semi < 0) break;
                sb.Append(raw, start, amp - start);
                ReadOnlySpan<char> name = raw.AsSpan(amp + 1, semi - amp - 1);
                if (name.Length > 1 && name[0] == '#')
                {
                    bool hex = name[1] == 'x' || name[1] == 'X';
                    ReadOnlySpan<char> digits = hex ? name.Slice(2) : name.Slice(1);
                    if (!int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out int code) ||
                        code > 0x10FFFF)
                        throw new XmlException($"Invalid character reference '&{name.ToString()};'.");
                    // A reference to a surrogate gives that UTF-16 unit, as the document reader does with
                    // CheckCharacters off: both readers read a file the same way, whoever wrote it.
                    if (code >= 0xD800 && code <= 0xDFFF)
                        sb.Append((char)code);
                    else
                        sb.Append(char.ConvertFromUtf32(code));
                }
                else
                {
                    sb.Append(name switch
                    {
                        "lt" => "<",
                        "gt" => ">",
                        "amp" => "&",
                        "quot" => "\"",
                        "apos" => "'",
                        _ => throw new XmlException($"Unknown entity '&{name.ToString()};'."),
                    });
                }
                start = semi + 1;
                amp = raw.IndexOf('&', start);
            }
            sb.Append(raw, start, raw.Length - start);
            return sb.ToString();
        }
    }
}
