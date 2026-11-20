using System;
using System.IO;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// ISS-156: <c>xod convert --format 7</c> is refused (the migrator refuses an undefined format, XodApiDefectTests), and
    /// nothing is written. The tool targets .NET 9 only, so this test does too (Xod.Tests.csproj).
    /// </summary>
    public class XodCliConvertTests : IDisposable
    {
        private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "xod-cliconv-" + Guid.NewGuid().ToString("N")[..8]);

        public void Dispose()
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        public class CcRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }

        [Fact]
        public void The_cli_refuses_an_undefined_format()
        {
            string dir = Path.Combine(tempRoot, "cli");
            Directory.CreateDirectory(dir);
            using (var db = new XodContext(Path.Combine(dir, "data.xod")))
                db.Insert(new CcRow { Name = "one" });
            string target = Path.Combine(tempRoot, "cli-target");

            int code = Xod.Cli.Program.Run(new[] { "convert", dir, target, "--format", "7" }, new StringWriter(), new StringWriter());
            Assert.Equal(1, code);
            Assert.False(Directory.Exists(target));
        }
    }
}
