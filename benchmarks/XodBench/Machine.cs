using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace XodBench;

/// <summary>
/// What the stress runs record about the machine, and the one change they make to it: pinning the run to
/// the performance cores of a hybrid CPU. Pinning gives every run the same kind of core; it does not keep
/// other programs off those cores, which is why every sample also records how busy the machine was.
/// </summary>
internal static class Machine
{
    public static readonly int LogicalCpus = ReadCpuSets()?.Count ?? Environment.ProcessorCount;

    /// <summary>Pins this process to the most performant cores when the CPU mixes core types.</summary>
    public static (string Mode, int Cpus) PinToPerformanceCores()
    {
        var sets = ReadCpuSets();
        if (sets == null || sets.Count == 0) return ("all", LogicalCpus);

        // EfficiencyClass: higher values are faster, less power-efficient cores (SYSTEM_CPU_SET_INFORMATION).
        byte best = sets.Max(s => s.EfficiencyClass);
        if (sets.All(s => s.EfficiencyClass == best)) return ("all", LogicalCpus);

        ulong mask = 0;
        int count = 0;
        foreach (var s in sets)
        {
            if (s.Group != 0 || s.EfficiencyClass != best || s.LogicalProcessor >= 64) continue;
            mask |= 1UL << s.LogicalProcessor;
            count++;
        }

        using var process = Process.GetCurrentProcess();
        process.ProcessorAffinity = (IntPtr)(long)mask;
        return ("performance", count);
    }

    /// <summary>
    /// Where the machine record of a result file goes: a `.machine` folder beside it, which git ignores
    /// (`benchmarks/results/**/.machine/`). Result files are committed and published, and the owner does not
    /// publish the machine a run used (CPU, threads, memory, OS build, runtime patch level); the record
    /// keeps it for reproducing a run. The one reading the site needs, mains power, stays in the result.
    /// </summary>
    public static string RecordPath(string resultPath)
    {
        string full = Path.GetFullPath(resultPath);
        return Path.Combine(Path.GetDirectoryName(full)!, ".machine", Path.GetFileName(full) + ".json");
    }

    /// <summary>Writes the machine record for a result file (see <see cref="RecordPath"/>).</summary>
    public static void WriteRecord(string resultPath, object machine)
    {
        string path = RecordPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(machine, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    public static object Describe(string affinity, int affinityCpus, double freeGb, bool? onAcPower) => new
    {
        cpu = CpuName(),
        logicalCpus = LogicalCpus,
        affinity,
        affinityCpus,
        ramGb = Math.Round(TotalMb() / 1024.0, 1),
        availMbAtStart = AvailableMb(),
        onAcPower,
        os = RuntimeInformation.OSDescription,
        runtime = RuntimeInformation.FrameworkDescription,
        freeDiskGb = Math.Round(freeGb, 1),
    };

    /// <summary>
    /// A hash of the assemblies that ran (the harness and the engine): the identity of the code itself,
    /// which a commit marked "-dirty" is not. Runs with the same id ran the same code.
    /// </summary>
    public static string BuildId(params Type[] types)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        foreach (var path in types.Select(t => t.Assembly.Location).Distinct().OrderBy(p => p, StringComparer.Ordinal))
        {
            byte[] bytes = File.ReadAllBytes(path);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// The checkout this harness was built from, as git answers it from the folder the binary runs in;
    /// null outside a git working tree or without git. Git decides, not a search for a ".git" folder: in
    /// a linked worktree or a submodule ".git" is a file (gitrepository-layout). Both stamps read it, so a
    /// run names the code it ran, whatever folder it was started from.
    /// </summary>
    public static string RepoRoot()
    {
        string top = Git(AppContext.BaseDirectory, "rev-parse --show-toplevel");
        return string.IsNullOrEmpty(top) ? null : Path.GetFullPath(top);
    }

    /// <summary>The commit that was measured, with "-dirty" when the tree had changes it does not contain.</summary>
    public static string GitCommit()
    {
        string repo = RepoRoot();
        if (repo == null) return null;
        string sha = Git(repo, "rev-parse --short HEAD");
        if (string.IsNullOrEmpty(sha)) return null;
        string changes = Git(repo, "status --porcelain --untracked-files=all");
        return changes == null || IsDirty(changes) ? sha + "-dirty" : sha;
    }

    private const string ResultsFolder = "benchmarks/results/";

    /// <summary>
    /// Whether `git status --porcelain` shows a change to anything but the results folder. Untracked
    /// files count: a new source file changes the code as much as an edit does. The results folder does
    /// not: the runs write their own files there. Porcelain v1 paths are always relative to the
    /// repository root, whatever the working directory (git-status, "Porcelain Format Version 1").
    /// </summary>
    internal static bool IsDirty(string porcelain) =>
        porcelain.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Length > 3)
            .SelectMany(line => line[3..].Split(" -> ")) // "XY path" or "XY old -> new"
            .Select(path => path.Trim('"'))
            .Any(path => !path.StartsWith(ResultsFolder, StringComparison.Ordinal));

    private static string Git(string workingDirectory, string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            // TrimEnd only: a porcelain line starts with its status, which can be a space (" M path").
            string output = p.StandardOutput.ReadToEnd().TrimEnd();
            p.WaitForExit(5_000);
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null; // no git on this machine: the header says so with a null commit
        }
    }

    public static string CpuName()
    {
        if (!OperatingSystem.IsWindows()) return RuntimeInformation.ProcessArchitecture.ToString();
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (key?.GetValue("ProcessorNameString") as string)?.Trim();
    }

    /// <summary>Machine-wide CPU time so far, as GetSystemTimes reports it: kernel time includes idle time.
    /// Only ratios of deltas are used, so the unit (100 ns ticks, or jiffies on Linux) does not matter.</summary>
    public static bool SystemTimes(out long idle, out long kernel, out long user)
    {
        idle = kernel = user = 0;
        if (OperatingSystem.IsWindows()) return GetSystemTimes(out idle, out kernel, out user);
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/stat")) return false;

        // First line: cpu user nice system idle iowait irq softirq steal ...
        string[] f = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long[] v = f.Skip(1).Take(8).Select(long.Parse).ToArray();
        long total = v.Sum();
        idle = v[3] + v[4];
        user = v[0] + v[1];
        kernel = total - user;
        return true;
    }

    public static long AvailableMb() => Memory().AvailMb;

    private static long TotalMb() => Memory().TotalMb;

    internal static bool? OnAcPower()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var s)) return null;
        return s.ACLineStatus switch { 1 => true, 0 => false, _ => null };
    }

    /// <summary>Physical memory in MB; -1 where the platform gives no answer.</summary>
    private static (long TotalMb, long AvailMb) Memory()
    {
        if (OperatingSystem.IsWindows())
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref m) ? ((long)(m.ullTotalPhys / 1048576), (long)(m.ullAvailPhys / 1048576)) : (-1, -1);
        }

        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            long total = -1, avail = -1;
            foreach (string line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) total = Kb(line) / 1024;
                else if (line.StartsWith("MemAvailable:")) avail = Kb(line) / 1024;
            }

            return (total, avail);
        }

        return (-1, -1);

        static long Kb(string line) => long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
    }

    private readonly record struct CpuSet(int Group, byte LogicalProcessor, byte EfficiencyClass);

    // SYSTEM_CPU_SET_INFORMATION, Type == CpuSetInformation (0): Size @0, Type @4, Id @8, Group @12,
    // LogicalProcessorIndex @14, CoreIndex @15, LastLevelCacheIndex @16, NumaNodeIndex @17, EfficiencyClass @18.
    private static List<CpuSet> ReadCpuSets()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint length, IntPtr.Zero, 0);
            if (length == 0) return null;
            IntPtr buffer = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return null;
                var sets = new List<CpuSet>();
                for (int offset = 0; offset < length;)
                {
                    int size = Marshal.ReadInt32(buffer, offset);
                    if (size <= 0) break;
                    if (Marshal.ReadInt32(buffer, offset + 4) == 0)
                    {
                        sets.Add(new CpuSet(
                            Marshal.ReadInt16(buffer, offset + 12),
                            Marshal.ReadByte(buffer, offset + 14),
                            Marshal.ReadByte(buffer, offset + 18)));
                    }

                    offset += size;
                }

                return sets;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (EntryPointNotFoundException)
        {
            return null; // before Windows 10: no CPU sets, so no core types to choose from
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);
}

/// <summary>
/// Process and machine readings for one sample, each a delta since the previous reading. "otherCpuPct" is
/// the share of the whole machine used by everything except this process: the number that says whether
/// a sample was taken on a quiet machine.
/// </summary>
internal sealed class Meter
{
    private long idle, kernel, user, stamp;
    private TimeSpan cpu, pause;

    public Meter() => Reset();

    private void Reset()
    {
        Machine.SystemTimes(out idle, out kernel, out user);
        using var process = Process.GetCurrentProcess();
        cpu = process.TotalProcessorTime;
        pause = GC.GetTotalPauseDuration();
        stamp = Stopwatch.GetTimestamp();
    }

    public void AddTo(IDictionary<string, object> row)
    {
        bool machineTimes = Machine.SystemTimes(out long i, out long k, out long u);
        using var process = Process.GetCurrentProcess();
        double wall = (Stopwatch.GetTimestamp() - stamp) / (double)Stopwatch.Frequency;
        long busyAndIdle = (k - kernel) + (u - user); // kernel time includes idle time
        double? host = machineTimes && busyAndIdle > 0 ? 100.0 * (busyAndIdle - (i - idle)) / busyAndIdle : null;
        double own = wall > 0 ? 100.0 * (process.TotalProcessorTime - cpu).TotalSeconds / (wall * Machine.LogicalCpus) : 0;

        // Unknown stays null: a 0 would read as "the machine was idle".
        row["hostCpuPct"] = host is double h ? Math.Round(h, 1) : null;
        row["processCpuPct"] = Math.Round(own, 1);
        row["otherCpuPct"] = host is double hh ? Math.Round(Math.Max(0, hh - own), 1) : null;
        row["availMb"] = Machine.AvailableMb();
        row["workingSetMb"] = Mb(process.WorkingSet64);
        row["privateMb"] = Mb(process.PrivateMemorySize64);
        row["gcHeapMb"] = Mb(GC.GetTotalMemory(false));
        row["gen0"] = GC.CollectionCount(0);
        row["gen1"] = GC.CollectionCount(1);
        row["gen2"] = GC.CollectionCount(2);
        row["gcPausePct"] = wall > 0 ? Math.Round(100 * (GC.GetTotalPauseDuration() - pause).TotalSeconds / wall, 2) : 0;

        Reset();
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);
}
