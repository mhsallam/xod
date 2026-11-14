using System;
using System.IO;
using Xod;

namespace Xod.Cli
{
    public static class Program
    {
        /// <summary>Output sent to a file or a pipe is UTF-8 text. Console.Out would encode it in the console's code
        /// page, where text a database holds (Arabic, an em dash) becomes '?'. A console window keeps Console.Out.</summary>
        public static int Main(string[] args)
        {
            TextWriter output = Console.IsOutputRedirected ? Utf8(Console.OpenStandardOutput()) : Console.Out;
            TextWriter error = Console.IsErrorRedirected ? Utf8(Console.OpenStandardError()) : Console.Error;
            try
            {
                return Run(args, output, error);
            }
            finally
            {
                output.Flush();
                error.Flush();
            }
        }

        private static TextWriter Utf8(Stream stream) => new StreamWriter(stream, new System.Text.UTF8Encoding(false));

        /// <summary>Runs one command, writing its output and its errors to the writers given (the console's, from
        /// <see cref="Main"/>). Returns the exit code.</summary>
        public static int Run(string[] args, TextWriter output, TextWriter error)
        {
            Out = output;
            Err = error;
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                PrintHelp();
                return 0;
            }

            try
            {
                string command = args[0].ToLowerInvariant();
                switch (command)
                {
                    case "stats":
                        return RunStats(args);
                    case "convert":
                        return RunConvert(args);
                    case "secure":
                        return RunSecure(args);
                    case "loose":
                        return RunLoose(args);
                    case "query":
                        return RunQuery(args);
                    default:
                        Err.WriteLine($"Unknown command '{command}'. Run 'xod --help' for usage.");
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Err.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        [ThreadStatic] private static TextWriter Out;
        [ThreadStatic] private static TextWriter Err;

        private static int RunStats(string[] args)
        {
            if (args.Length < 2)
            {
                Err.WriteLine("Usage: xod stats <db-path> [--password <pwd>]");
                return 1;
            }

            string path = args[1];
            string password = GetOption(args, "--password");

            using var db = new XodContext(path, password, new DatabaseOptions { InitialCreate = false });
            var stats = db.GetStatistics();

            Out.WriteLine("==================================================");
            Out.WriteLine("  XOD++ Database Statistics");
            Out.WriteLine("==================================================");
            Out.WriteLine($"  Path          : {stats.DatabasePath}");
            Out.WriteLine($"  Format        : {stats.StorageFormat}");
            Out.WriteLine($"  Encrypted     : {stats.IsEncrypted}");
            Out.WriteLine($"  Total Size    : {stats.TotalSizeBytes:N0} bytes");
            Out.WriteLine($"  Tables        : {stats.TableCount}");
            Out.WriteLine($"  Total Pages   : {stats.TotalPageCount}");
            Out.WriteLine($"  Total Rows    : {stats.TotalRowCount}");
            Out.WriteLine("--------------------------------------------------");

            foreach (var table in stats.Tables)
            {
                Out.WriteLine($"  * {table.TableName}");
                Out.WriteLine($"      Pages: {table.PageCount} | Rows: {table.RowCount} | Size: {table.SizeBytes:N0} bytes");
            }
            return 0;
        }

        private static int RunConvert(string[] args)
        {
            if (args.Length < 3)
            {
                Err.WriteLine("Usage: xod convert <source-dir> <target-dir> --format <xml|json|binary> [--password <pwd>]");
                return 1;
            }

            string source = args[1];
            string target = args[2];
            string formatStr = GetOption(args, "--format") ?? "json";
            string password = GetOption(args, "--password");

            if (!Enum.TryParse<StorageFormat>(formatStr, ignoreCase: true, out var targetFormat))
            {
                Err.WriteLine($"Invalid storage format '{formatStr}'. Expected xml, json, or binary.");
                return 1;
            }

            int files = XodMigrator.ConvertDatabase(source, target, targetFormat, password);
            Out.WriteLine($"Converted {files} database files to {targetFormat} format in '{Path.GetFullPath(target)}'.");
            return 0;
        }

        private static int RunSecure(string[] args)
        {
            if (args.Length < 2)
            {
                Err.WriteLine("Usage: xod secure <db-path> --password <pwd>");
                return 1;
            }

            string path = args[1];
            string password = GetOption(args, "--password");
            if (string.IsNullOrEmpty(password))
            {
                Err.WriteLine("A password is required (--password <pwd>).");
                return 1;
            }

            using var db = new XodContext(path, null, new DatabaseOptions { InitialCreate = false });
            db.Secure(password);
            Out.WriteLine($"Database '{path}' secured with AES-256-GCM encryption.");
            return 0;
        }

        private static int RunLoose(string[] args)
        {
            if (args.Length < 2)
            {
                Err.WriteLine("Usage: xod loose <db-path> --password <pwd>");
                return 1;
            }

            string path = args[1];
            string password = GetOption(args, "--password");
            if (string.IsNullOrEmpty(password))
            {
                Err.WriteLine("A password is required (--password <pwd>).");
                return 1;
            }

            using var db = new XodContext(path, password, new DatabaseOptions { InitialCreate = false });
            db.Loose(password);
            Out.WriteLine($"Database '{path}' decrypted.");
            return 0;
        }

        private const string QueryUsage = "Usage: xod query <db-path> \"SELECT ...\" [--format table|csv|json] [--password <pwd>]";

        /// <summary>
        /// xod query (PLAN-060 section 2.5): reads the database through the schema its table files record
        /// (SqlSchemaSource.Recorded). It calls no writer, and InitialCreate is off, so a missing database isn't made;
        /// the open itself still holds xod.lock and puts back a commit a crash cut short, as every open does. The
        /// rows go to the output as a table, CSV or JSON; warnings and errors (<c>XQnnn: message</c>) to the error
        /// stream, an error with exit code 1.
        /// </summary>
        private static int RunQuery(string[] args)
        {
            if (args.Length < 3 || args[1].StartsWith("--", StringComparison.Ordinal) || args[2].StartsWith("--", StringComparison.Ordinal))
            {
                Err.WriteLine(QueryUsage);
                return 1;
            }

            string path = args[1];
            string sql = args[2];
            string format = (GetOption(args, "--format") ?? "table").ToLowerInvariant();
            if (format is not ("table" or "csv" or "json"))
            {
                Err.WriteLine($"Unknown format '{format}': table, csv or json.");
                return 1;
            }

            SqlResult result;
            try
            {
                var options = new DatabaseOptions { InitialCreate = false, SqlSchema = SqlSchemaSource.Recorded };
                using var db = new XodContext(path, GetOption(args, "--password"), options);
                result = db.Sql(sql);
            }
            catch (XodSqlException e)
            {
                Err.WriteLine(e.Message);
                return 1;
            }

            foreach (var warning in result.Warnings)
                Err.WriteLine(warning.ToString());
            switch (format)
            {
                case "csv":
                    QueryOutput.WriteCsv(Out, result);
                    break;
                case "json":
                    QueryOutput.WriteJson(Out, result);
                    break;
                default:
                    QueryOutput.WriteTable(Out, result);
                    break;
            }
            return 0;
        }

        private static string GetOption(string[] args, string flag)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static void PrintHelp()
        {
            Out.WriteLine("""
                XOD++ CLI (v2.0.0) - Embedded Object-Relational Database Tool
                
                Commands:
                  xod stats <db-path> [--password <pwd>]
                      Display tables, pages, row counts, and disk usage for a XOD++ database.

                  xod convert <src-dir> <dest-dir> --format <xml|json|binary> [--password <pwd>]
                      Convert a XOD++ database between XML, JSON, and Binary storage engines.

                  xod secure <db-path> --password <pwd>
                      Encrypt an unencrypted XOD++ database using AES-256-GCM.

                  xod loose <db-path> --password <pwd>
                      Decrypt an encrypted XOD++ database.

                  xod query <db-path> "SELECT ..." [--format table|csv|json] [--password <pwd>]
                      Run a SQL query (XOD SQL, SELECT only). Tables are read through the schema their files
                      record, without the application's classes. It writes no rows and creates no
                      database; like any open, it puts back a commit a crash cut short.
                """);
        }
    }
}
