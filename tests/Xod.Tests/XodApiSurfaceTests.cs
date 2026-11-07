using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Xod;
using Xunit;

namespace Xod.Tests
{
    /// <summary>
    /// Gate layer 9 of TEST-PLAN-2.0: the 2.0 public surface is pinned. This test reflects over the
    /// exported types, writes a canonical line per public type and member, and compares it with the
    /// committed snapshot in fixtures/api-surface.txt. Any addition or removal fails the suite until the
    /// snapshot is regenerated on purpose with XOD_UPDATE_API=1 - which is what makes a breaking change a
    /// decision rather than an accident.
    /// </summary>
    public class XodApiSurfaceTests
    {
        private static string SnapshotPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "tests", "Xod.Tests", "fixtures", "api-surface.txt");
                if (Directory.Exists(Path.GetDirectoryName(candidate)!))
                    return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("fixtures directory not found above " + AppContext.BaseDirectory);
        }

        private static string Describe(Type type)
        {
            var lines = new List<string>();
            lines.Add($"type {type.FullName}" + (type.IsAbstract && type.IsSealed ? " (static)" : type.IsAbstract ? " (abstract)" : type.IsSealed ? " (sealed)" : ""));

            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                lines.Add($"  ctor({string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                lines.Add($"  prop {property.PropertyType.Name} {property.Name} {{{(property.CanRead ? " get;" : "")}{(property.CanWrite ? " set;" : "")} }}");

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                lines.Add($"  field {field.FieldType.Name} {field.Name}");

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                         .Where(m => !m.IsSpecialName)
                         .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                lines.Add($"  method {method.ReturnType.Name} {method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})");
            }

            return string.Join("\n", lines);
        }

        private static string BuildSurface()
        {
            var assembly = typeof(XodContext).Assembly;
            var types = assembly.GetExportedTypes()
                .Where(t => t.Namespace != null && t.Namespace.StartsWith("Xod", StringComparison.Ordinal))
                .OrderBy(t => t.FullName, StringComparer.Ordinal);

            return string.Join("\n\n", types.Select(Describe)) + "\n";
        }

        [Fact]
        public void The_public_api_matches_the_committed_snapshot()
        {
            string actual = BuildSurface();
            string path = SnapshotPath();

            if (Environment.GetEnvironmentVariable("XOD_UPDATE_API") == "1")
            {
                File.WriteAllText(path, actual);
                return;
            }

            Assert.True(File.Exists(path), $"api snapshot missing at {path}; run with XOD_UPDATE_API=1 to create it");
            string expected = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.Equal(expected, actual.Replace("\r\n", "\n"));
        }
    }
}
