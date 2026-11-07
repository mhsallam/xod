using System;
using System.IO;
using Xod;

namespace Xod.Cli
{
    public static class Program
    {
        public static int Main(string[] args)
        {
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
                    default:
                        Console.Error.WriteLine($"Unknown command '{command}'. Run 'xod --help' for usage.");
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        private static int RunStats(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: xod stats <db-path> [--password <pwd>]");
                return 1;
            }

            string path = args[1];
            string password = GetOption(args, "--password");

            using var db = new XodContext(path, password, new DatabaseOptions { InitialCreate = false });
            var stats = db.GetStatistics();

            Console.WriteLine("==================================================");
            Console.WriteLine("  XOD++ Database Statistics");
            Console.WriteLine("==================================================");
            Console.WriteLine($"  Path          : {stats.DatabasePath}");
            Console.WriteLine($"  Format        : {stats.StorageFormat}");
            Console.WriteLine($"  Encrypted     : {stats.IsEncrypted}");
            Console.WriteLine($"  Total Size    : {stats.TotalSizeBytes:N0} bytes");
            Console.WriteLine($"  Tables        : {stats.TableCount}");
            Console.WriteLine($"  Total Pages   : {stats.TotalPageCount}");
            Console.WriteLine($"  Total Rows    : {stats.TotalRowCount}");
            Console.WriteLine("--------------------------------------------------");

            foreach (var table in stats.Tables)
            {
                Console.WriteLine($"  * {table.TableName}");
                Console.WriteLine($"      Pages: {table.PageCount} | Rows: {table.RowCount} | Size: {table.SizeBytes:N0} bytes");
            }
            return 0;
        }

        private static int RunConvert(string[] args)
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: xod convert <source-dir> <target-dir> --format <xml|json|binary> [--password <pwd>]");
                return 1;
            }

            string source = args[1];
            string target = args[2];
            string formatStr = GetOption(args, "--format") ?? "json";
            string password = GetOption(args, "--password");

            if (!Enum.TryParse<StorageFormat>(formatStr, ignoreCase: true, out var targetFormat))
            {
                Console.Error.WriteLine($"Invalid storage format '{formatStr}'. Expected xml, json, or binary.");
                return 1;
            }

            int files = XodMigrator.ConvertDatabase(source, target, targetFormat, password);
            Console.WriteLine($"Converted {files} database files to {targetFormat} format in '{Path.GetFullPath(target)}'.");
            return 0;
        }

        private static int RunSecure(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: xod secure <db-path> --password <pwd>");
                return 1;
            }

            string path = args[1];
            string password = GetOption(args, "--password");
            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("A password is required (--password <pwd>).");
                return 1;
            }

            using var db = new XodContext(path, null, new DatabaseOptions { InitialCreate = false });
            db.Secure(password);
            Console.WriteLine($"Database '{path}' secured with AES-256-GCM encryption.");
            return 0;
        }

        private static int RunLoose(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: xod loose <db-path> --password <pwd>");
                return 1;
            }

            string path = args[1];
            string password = GetOption(args, "--password");
            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("A password is required (--password <pwd>).");
                return 1;
            }

            using var db = new XodContext(path, password, new DatabaseOptions { InitialCreate = false });
            db.Loose(password);
            Console.WriteLine($"Database '{path}' decrypted.");
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
            Console.WriteLine("""
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
                """);
        }
    }
}
