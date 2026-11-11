// Regenerates the conformance files (PLAN-047 section 6):
//   dotnet run --project conformance/Xod.Conformance -f net9.0 -- cases   rewrite conformance/cases from Cases.cs
//   dotnet run --project conformance/Xod.Conformance -f net9.0 -- data    rewrite the fixtures in conformance/data
//   ... -- data <folder>    write the same files to <folder> instead (for read.mjs --check-fresh)
// The fixtures are written by the library under test, so rewrite them only when the dataset or the file
// format changes on purpose, and record why in PLAN-047. `dotnet test` never writes either.
using System;
using System.IO;

namespace Xod.Conformance
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string what = args.Length > 0 ? args[0] : "";
            switch (what)
            {
                case "cases":
                    CaseFiles.WriteAll();
                    Console.WriteLine($"wrote {Cases.All.Count} cases to {CaseFiles.Folder}");
                    return 0;
                case "data":
                    // With a folder: the same files, written elsewhere, for read.mjs --check-fresh.
                    string dataRoot = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(Repo.Conformance, "data");
                    foreach (var fixture in Repo.Fixtures)
                    {
                        string dir = Path.Combine(dataRoot, fixture);
                        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                        Directory.CreateDirectory(dir);
                        Dataset.Write(dir, Repo.FormatOf(fixture), Repo.PasswordOf(fixture));
                        Console.WriteLine($"wrote {dir}");
                    }
                    return 0;
                default:
                    Console.Error.WriteLine("usage: Xod.Conformance cases | data [folder]");
                    return 2;
            }
        }
    }
}
