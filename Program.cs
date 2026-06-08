using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Management;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RAMLIMITER
{
    class Program
    {
        [DllImport("kernel32.dll")]
        static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetProcessInformation(
            IntPtr hProcess,
            PROCESS_INFORMATION_CLASS processInformationClass,
            ref PROCESS_POWER_THROTTLING_STATE processInformation,
            int processInformationSize);

        const int SW_HIDE = 0;
        const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
        const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

        static readonly ProcessTarget[] PrechosenTargets =
        {
            new ProcessTarget(
                "Discord",
                new[] { "Discord" },
                new[]
                {
                    @"%LOCALAPPDATA%\Discord\app-*\Discord.exe"
                }),
            new ProcessTarget(
                "Microsoft Edge",
                new[] { "msedge" },
                new[]
                {
                    @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
                    @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"
                }),
            new ProcessTarget(
                "Overwolf",
                new[] { "Overwolf", "OverwolfBrowser", "OverwolfHelper", "OverwolfHelper64", "ow-overlay", "OverwolfLauncherProxy" },
                new[]
                {
                    @"%ProgramFiles(x86)%\Overwolf\Overwolf.exe",
                    @"%ProgramFiles(x86)%\Overwolf\*\OverwolfBrowser.exe",
                    @"%ProgramFiles(x86)%\Overwolf\*\ow-overlay.exe",
                    @"%ProgramFiles%\Overwolf\Overwolf.exe",
                    @"%ProgramFiles%\Overwolf\*\OverwolfBrowser.exe",
                    @"%ProgramFiles%\Overwolf\*\ow-overlay.exe"
                }),
            new ProcessTarget(
                "OneDrive",
                new[] { "OneDrive", "OneDrive.App", "OneDrive.Sync.Service", "FileCoAuth", "FileSyncHelper", "Microsoft.SharePoint", "Groove", "OneDriveStandaloneUpdater" },
                new[]
                {
                    @"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.exe",
                    @"%LOCALAPPDATA%\Microsoft\OneDrive\OneDrive.App.exe",
                    @"%LOCALAPPDATA%\Microsoft\OneDrive\*\OneDrive.Sync.Service.exe",
                    @"%LOCALAPPDATA%\Microsoft\OneDrive\*\FileCoAuth.exe",
                    @"%LOCALAPPDATA%\Microsoft\OneDrive\*\FileSyncHelper.exe",
                    @"%LOCALAPPDATA%\Microsoft\OneDrive\*\Microsoft.SharePoint.exe",
                    @"%LOCALAPPDATA%\Microsoft\OneDrive\*\Groove.exe",
                    @"%LOCALAPPDATA%\Microsoft\OneDrive\*\OneDriveStandaloneUpdater.exe"
                }),
            new ProcessTarget(
                "Cursor",
                new[] { "Cursor" },
                new[]
                {
                    @"%LOCALAPPDATA%\Programs\cursor\Cursor.exe"
                }),
            new ProcessTarget(
                "Malwarebytes",
                new[] { "Malwarebytes", "MBAM", "MBAMService", "MBAMWsc", "MbamBgNativeMsg", "malwarebytes_assistant" },
                new[]
                {
                    @"%ProgramFiles%\Malwarebytes\Anti-Malware\Malwarebytes.exe",
                    @"%ProgramFiles%\Malwarebytes\Anti-Malware\MBAM.exe",
                    @"%ProgramFiles%\Malwarebytes\Anti-Malware\MBAMService.exe",
                    @"%ProgramFiles%\Malwarebytes\Anti-Malware\MBAMWsc.exe",
                    @"%ProgramFiles%\Malwarebytes\Anti-Malware\MbamBgNativeMsg.exe",
                    @"%ProgramFiles%\Malwarebytes\Anti-Malware\malwarebytes_assistant.exe"
                }),
            new ProcessTarget(
                "SyncTrayzor",
                new[] { "SyncTrayzor", "syncthing" },
                new[]
                {
                    @"%ProgramFiles%\SyncTrayzor\SyncTrayzor.exe",
                    @"%ProgramFiles%\SyncTrayzor\syncthing.exe",
                    @"%ProgramFiles%\SyncTrayzor\CefSharp.BrowserSubprocess.exe",
                    @"%APPDATA%\SyncTrayzor\syncthing.exe"
                })
        };

        const string StartupRunValueName = "RAM Limiter Prechosen";
        static readonly string CustomPrechosenTargetsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RAM Limiter",
            "prechosen-exes.txt");
        static readonly string CpuModeConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RAM Limiter",
            "prechosen-cpu-modes.txt");

        // Pagefile is intentionally omitted: it is OS virtual memory backing store, not an app process to trim.
        static bool prechosenLimiterStarted;
        static bool cpuLimiterStarted;

        enum PROCESS_INFORMATION_CLASS
        {
            ProcessPowerThrottling = 4
        }

        struct PROCESS_POWER_THROTTLING_STATE
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        enum CpuThrottleMode
        {
            Normal,
            BelowNormal,
            Idle,
            EcoQos
        }

        static readonly Dictionary<string, CpuThrottleMode> DefaultCpuModes = new Dictionary<string, CpuThrottleMode>(StringComparer.OrdinalIgnoreCase)
        {
            { "Discord", CpuThrottleMode.BelowNormal },
            { "Microsoft Edge", CpuThrottleMode.BelowNormal },
            { "Overwolf", CpuThrottleMode.BelowNormal },
            { "OneDrive", CpuThrottleMode.EcoQos },
            { "Cursor", CpuThrottleMode.BelowNormal },
            { "Malwarebytes", CpuThrottleMode.Normal },
            { "SyncTrayzor", CpuThrottleMode.EcoQos }
        };

        class ProcessTarget
        {
            public ProcessTarget(string displayName, string[] processNames, string[] executablePathPatterns, bool matchByProcessName = true)
            {
                DisplayName = displayName;
                ProcessNames = processNames;
                ExecutablePathPatterns = executablePathPatterns;
                MatchByProcessName = matchByProcessName;
            }

            public string DisplayName { get; private set; }
            public string[] ProcessNames { get; private set; }
            public string[] ExecutablePathPatterns { get; private set; }
            public bool MatchByProcessName { get; private set; }

            public bool MatchesProcessName(string processName)
            {
                return MatchByProcessName && ProcessNames.Any(name => string.Equals(name, processName, StringComparison.OrdinalIgnoreCase));
            }

            public bool MatchesExecutablePath(string path)
            {
                return ExecutablePathPatterns.Any(pattern => PathPatternMatches(pattern, path));
            }

            static bool PathPatternMatches(string pattern, string path)
            {
                string expandedPattern = Environment.ExpandEnvironmentVariables(pattern);
                string regexPattern = "^" + Regex.Escape(expandedPattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                return Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase);
            }
        }

        static bool IsAdmin() // Method to check the program's current privileges - created by Hypn0tick | github.com/Hypn0tick
        {
            WindowsIdentity id = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(id);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        static void ElevatePrivileges(string args) // Method to elevate the program's privileges if the user chooses to do so - created by Hypn0tick | github.com/Hypn0tick
        {
            if (!IsAdmin())
            {
                var proc = new ProcessStartInfo
                {
                    UseShellExecute = true,
                    WorkingDirectory = Environment.CurrentDirectory,
                    FileName = Assembly.GetEntryAssembly().Location,
                    Arguments = args,
                    Verb = "runas"
                };

                Console.Clear();
                Console.WriteLine("RAM Limiter does not currently have admin. privileges.\nWould you like to run as admin? (y/n)");
                if (Console.ReadKey(true).Key == ConsoleKey.Y)
                {
                    try
                    {
                        Process.Start(proc);
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Could not elevate program.\n" + ex);
                    }
                }
            }
        }

        class RamUsage
        {
            public RamUsage(double freeMb, double totalMb)
            {
                FreeMb = freeMb;
                TotalMb = totalMb;
            }

            public double FreeMb { get; private set; }
            public double TotalMb { get; private set; }
            public double UsedMb { get { return TotalMb - FreeMb; } }
            public double Percent { get { return TotalMb <= 0 ? 0 : (UsedMb / TotalMb) * 100; } }

            public string ToDisplayString()
            {
                return string.Format("{0:F2} GB / {1:F2} GB ({2:F2}%)", UsedMb / 1024, TotalMb / 1024, Percent);
            }
        }

        class CpuUsageSnapshot
        {
            public CpuUsageSnapshot(DateTime timestampUtc, double totalProcessorMs)
            {
                TimestampUtc = timestampUtc;
                TotalProcessorMs = totalProcessorMs;
            }

            public DateTime TimestampUtc { get; private set; }
            public double TotalProcessorMs { get; private set; }
        }

        class CpuThrottleStats
        {
            public int Applied { get; set; }
            public int Failed { get; set; }
            public double? UsagePercent { get; set; }
        }

        static readonly Dictionary<int, CpuUsageSnapshot> CpuUsageSamples = new Dictionary<int, CpuUsageSnapshot>();

        static RamUsage GetRamUsage()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher("select * from Win32_OperatingSystem"))
                {
                    var mem = searcher.Get().Cast<ManagementObject>().Select(mo => new
                    {
                        Free = double.Parse(mo["FreePhysicalMemory"].ToString()),
                        Total = double.Parse(mo["TotalVisibleMemorySize"].ToString())
                    }).FirstOrDefault();

                    if (mem == null)
                        return null;

                    return new RamUsage(mem.Free, mem.Total);
                }
            }
            catch
            {
                return null;
            }
        }

        static int TrimTargets(IEnumerable<ProcessTarget> targets, IntPtr min, IntPtr max, out int failed)
        {
            int trimmed = 0;
            failed = 0;

            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    if (FindTargetForProcess(targets, proc) == null)
                        continue;

                    try
                    {
                        if (SetProcessWorkingSetSize(proc.Handle, min, max))
                            trimmed++;
                        else
                            failed++;
                    }
                    catch
                    {
                        failed++;
                    }
                }
            }

            return trimmed;
        }

        static ProcessTarget FindTargetForProcess(IEnumerable<ProcessTarget> targets, Process proc)
        {
            ProcessTarget target = targets.FirstOrDefault(t => t.MatchesProcessName(proc.ProcessName));
            if (target != null)
                return target;

            string path = GetProcessExecutablePath(proc);
            if (!string.IsNullOrEmpty(path))
                target = targets.FirstOrDefault(t => t.MatchesExecutablePath(path));

            return target;
        }

        static string GetProcessExecutablePath(Process process)
        {
            try
            {
                return process.MainModule.FileName;
            }
            catch
            {
                return null;
            }
        }

        static ProcessTarget[] GetPrechosenTargets()
        {
            return PrechosenTargets.Concat(LoadCustomPrechosenTargets()).ToArray();
        }

        static ProcessTarget[] LoadCustomPrechosenTargets()
        {
            return LoadCustomPrechosenExecutableNames()
                .Select(name => new ProcessTarget(
                    "Custom: " + name,
                    new[] { NormalizeProcessName(name) },
                    new string[0]))
                .ToArray();
        }

        static string[] LoadCustomPrechosenExecutableNames()
        {
            if (!File.Exists(CustomPrechosenTargetsPath))
                return new string[0];

            try
            {
                return File.ReadAllLines(CustomPrechosenTargetsPath)
                    .Select(line => line.Trim().Trim('"'))
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Select(NormalizeProcessName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                return new string[0];
            }
        }

        static void SaveCustomPrechosenExecutableNames(IEnumerable<string> names)
        {
            string directory = Path.GetDirectoryName(CustomPrechosenTargetsPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(
                CustomPrechosenTargetsPath,
                names.Select(NormalizeProcessName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToArray());
        }

        static void AddCustomPrechosenExecutable()
        {
            Console.Clear();
            Console.WriteLine("Enter exe/process name to add to Prechosen Apps (e.g., steam.exe, chrome):");
            string input = Console.ReadLine();
            string name = string.IsNullOrWhiteSpace(input) ? null : input.Trim().Trim('"');

            if (string.IsNullOrWhiteSpace(name))
            {
                PrintTimedMessage(ConsoleColor.Red, "No exe/process name entered.");
                return;
            }

            name = NormalizeProcessName(name);

            if (!Regex.IsMatch(name, @"^[A-Za-z0-9_. -]+$"))
            {
                PrintTimedMessage(ConsoleColor.Red, "Invalid exe/process name.");
                return;
            }

            string[] existingNames = LoadCustomPrechosenExecutableNames();
            if (existingNames.Any(existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)))
            {
                PrintTimedMessage(ConsoleColor.Yellow, "Already in Prechosen Apps: " + name);
                return;
            }

            SaveCustomPrechosenExecutableNames(existingNames.Concat(new[] { name }));
            PrintTimedMessage(ConsoleColor.Green, "Added to Prechosen Apps: " + name);
        }

        static string NormalizeProcessName(string name)
        {
            string normalized = name.Trim().Trim('"');
            if (normalized.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
                normalized = Path.GetFileName(normalized);
            if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                normalized = normalized.Substring(0, normalized.Length - 4);
            return normalized;
        }

        static Dictionary<string, CpuThrottleMode> LoadCpuModeSettings()
        {
            var settings = new Dictionary<string, CpuThrottleMode>(DefaultCpuModes, StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(CpuModeConfigPath))
                return settings;

            try
            {
                foreach (string line in File.ReadAllLines(CpuModeConfigPath))
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#"))
                        continue;

                    string[] parts = trimmed.Split(new[] { '=' }, 2);
                    if (parts.Length != 2)
                        continue;

                    string targetName = parts[0].Trim();
                    CpuThrottleMode mode;
                    if (DefaultCpuModes.ContainsKey(targetName) && TryParseCpuMode(parts[1].Trim(), out mode))
                        settings[targetName] = mode;
                }
            }
            catch
            {
            }

            return settings;
        }

        static void SaveCpuModeSettings(Dictionary<string, CpuThrottleMode> settings)
        {
            string directory = Path.GetDirectoryName(CpuModeConfigPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(
                CpuModeConfigPath,
                PrechosenTargets.Select(target => target.DisplayName + "=" + CpuModeToConfigValue(GetCpuMode(settings, target.DisplayName))).ToArray());
        }

        static CpuThrottleMode GetCpuMode(Dictionary<string, CpuThrottleMode> settings, string targetName)
        {
            CpuThrottleMode mode;
            if (settings.TryGetValue(targetName, out mode))
                return mode;
            if (DefaultCpuModes.TryGetValue(targetName, out mode))
                return mode;
            return CpuThrottleMode.Normal;
        }

        static bool TryParseCpuMode(string value, out CpuThrottleMode mode)
        {
            string normalized = Regex.Replace(value ?? string.Empty, @"[\s_-]+", string.Empty).ToLowerInvariant();
            switch (normalized)
            {
                case "normal":
                    mode = CpuThrottleMode.Normal;
                    return true;
                case "belownormal":
                    mode = CpuThrottleMode.BelowNormal;
                    return true;
                case "idle":
                    mode = CpuThrottleMode.Idle;
                    return true;
                case "ecoqos":
                case "eco":
                    mode = CpuThrottleMode.EcoQos;
                    return true;
                default:
                    mode = CpuThrottleMode.Normal;
                    return false;
            }
        }

        static string CpuModeToConfigValue(CpuThrottleMode mode)
        {
            switch (mode)
            {
                case CpuThrottleMode.BelowNormal:
                    return "BelowNormal";
                case CpuThrottleMode.Idle:
                    return "Idle";
                case CpuThrottleMode.EcoQos:
                    return "EcoQoS";
                default:
                    return "Normal";
            }
        }

        static string FormatCpuMode(CpuThrottleMode mode)
        {
            switch (mode)
            {
                case CpuThrottleMode.BelowNormal:
                    return "Below Normal";
                case CpuThrottleMode.Idle:
                    return "Idle";
                case CpuThrottleMode.EcoQos:
                    return "EcoQoS";
                default:
                    return "Normal";
            }
        }

        static CpuThrottleMode NextCpuMode(CpuThrottleMode mode)
        {
            switch (mode)
            {
                case CpuThrottleMode.Normal:
                    return CpuThrottleMode.BelowNormal;
                case CpuThrottleMode.BelowNormal:
                    return CpuThrottleMode.Idle;
                case CpuThrottleMode.Idle:
                    return CpuThrottleMode.EcoQos;
                default:
                    return CpuThrottleMode.Normal;
            }
        }

        static void ConfigureCpuModes()
        {
            var settings = LoadCpuModeSettings();

            while (true)
            {
                Console.Clear();
                Console.WriteLine("CPU throttling modes (separate from RAM).");
                Console.WriteLine("Select preset app number to cycle: Normal -> Below Normal -> Idle -> EcoQoS.");
                Console.WriteLine();

                for (int i = 0; i < PrechosenTargets.Length; i++)
                {
                    ProcessTarget target = PrechosenTargets[i];
                    Console.WriteLine("{0}: {1} [{2}]", i + 1, target.DisplayName, FormatCpuMode(GetCpuMode(settings, target.DisplayName)));
                }

                Console.WriteLine("0: Back");
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.D0 || key.Key == ConsoleKey.NumPad0)
                    return;

                int index = KeyToNumber(key) - 1;
                if (index < 0 || index >= PrechosenTargets.Length)
                {
                    PrintTimedMessage(ConsoleColor.Red, "Invalid input. Try again.");
                    continue;
                }

                string targetName = PrechosenTargets[index].DisplayName;
                settings[targetName] = NextCpuMode(GetCpuMode(settings, targetName));
                SaveCpuModeSettings(settings);
            }
        }

        static int KeyToNumber(ConsoleKeyInfo key)
        {
            if (key.Key >= ConsoleKey.D0 && key.Key <= ConsoleKey.D9)
                return (int)key.Key - (int)ConsoleKey.D0;
            if (key.Key >= ConsoleKey.NumPad0 && key.Key <= ConsoleKey.NumPad9)
                return (int)key.Key - (int)ConsoleKey.NumPad0;
            return -1;
        }

        static void StartCpuThrottleLimiter()
        {
            if (cpuLimiterStarted)
            {
                PrintTimedMessage(ConsoleColor.Yellow, "CPU throttling is already running.");
                return;
            }

            cpuLimiterStarted = true;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Started CPU throttling. RAM limiter not started.");
            PrintCpuModeSummary(LoadCpuModeSettings());
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.ForegroundColor = ConsoleColor.White;

            LimitCpuForTargets(PrechosenTargets, 5000);
        }

        static void PrintCpuModeSummary(Dictionary<string, CpuThrottleMode> settings)
        {
            Console.WriteLine("CPU modes: " + string.Join(", ", PrechosenTargets.Select(target => target.DisplayName + "=" + FormatCpuMode(GetCpuMode(settings, target.DisplayName)))));
        }

        static void LimitCpuForTargets(IEnumerable<ProcessTarget> targets, int interval)
        {
            while (true)
            {
                var settings = LoadCpuModeSettings();
                CpuThrottleStats stats = ApplyCpuModesForTargets(targets, settings);

                Console.ForegroundColor = stats.Applied > 0 ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
                Console.WriteLine("CPU: applied {0} process(es), failed {1}, usage {2}", stats.Applied, stats.Failed, FormatCpuUsage(stats.UsagePercent));
                PrintCpuModeSummary(settings);
                Console.ForegroundColor = ConsoleColor.White;
                Thread.Sleep(interval);
            }
        }

        static CpuThrottleStats ApplyCpuModesForTargets(IEnumerable<ProcessTarget> targets, Dictionary<string, CpuThrottleMode> settings)
        {
            var stats = new CpuThrottleStats();
            double totalCpuPercent = 0;
            bool hasCpuSample = false;

            foreach (var proc in Process.GetProcesses())
            {
                using (proc)
                {
                    ProcessTarget target = FindTargetForProcess(targets, proc);
                    if (target == null)
                        continue;

                    try
                    {
                        double? processCpuPercent = GetProcessCpuUsagePercent(proc);
                        if (processCpuPercent.HasValue)
                        {
                            totalCpuPercent += processCpuPercent.Value;
                            hasCpuSample = true;
                        }

                        ApplyCpuMode(proc, GetCpuMode(settings, target.DisplayName));
                        stats.Applied++;
                    }
                    catch
                    {
                        stats.Failed++;
                    }
                }
            }

            stats.UsagePercent = hasCpuSample ? (double?)totalCpuPercent : null;
            return stats;
        }

        static double? GetProcessCpuUsagePercent(Process process)
        {
            DateTime now = DateTime.UtcNow;
            double totalProcessorMs = process.TotalProcessorTime.TotalMilliseconds;

            CpuUsageSnapshot previous;
            bool hasPrevious = CpuUsageSamples.TryGetValue(process.Id, out previous);
            CpuUsageSamples[process.Id] = new CpuUsageSnapshot(now, totalProcessorMs);
            if (!hasPrevious)
                return null;

            double elapsedMs = (now - previous.TimestampUtc).TotalMilliseconds;
            double cpuMs = totalProcessorMs - previous.TotalProcessorMs;
            if (elapsedMs <= 0 || cpuMs < 0)
                return null;

            return Math.Max(0, (cpuMs / (elapsedMs * Environment.ProcessorCount)) * 100);
        }

        static string FormatCpuUsage(double? percent)
        {
            return percent.HasValue ? string.Format("{0:F2}%", percent.Value) : "warming up";
        }

        static void ApplyCpuMode(Process process, CpuThrottleMode mode)
        {
            switch (mode)
            {
                case CpuThrottleMode.BelowNormal:
                    SetEcoQos(process, false);
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                    break;
                case CpuThrottleMode.Idle:
                    SetEcoQos(process, false);
                    process.PriorityClass = ProcessPriorityClass.Idle;
                    break;
                case CpuThrottleMode.EcoQos:
                    process.PriorityClass = ProcessPriorityClass.Normal;
                    SetEcoQos(process, true);
                    break;
                default:
                    SetEcoQos(process, false);
                    process.PriorityClass = ProcessPriorityClass.Normal;
                    break;
            }
        }

        static void SetEcoQos(Process process, bool enabled)
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enabled ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };

            SetProcessInformation(
                process.Handle,
                PROCESS_INFORMATION_CLASS.ProcessPowerThrottling,
                ref state,
                Marshal.SizeOf(typeof(PROCESS_POWER_THROTTLING_STATE)));
        }

        static bool IsPrechosenStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && !string.IsNullOrEmpty(key.GetValue(StartupRunValueName) as string);
                }
            }
            catch
            {
                return false;
            }
        }

        static void TogglePrechosenStartup()
        {
            Console.Clear();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    if (key == null)
                    {
                        PrintTimedMessage(ConsoleColor.Red, "Could not open Windows startup registry key.");
                        return;
                    }

                    string existingValue = key.GetValue(StartupRunValueName) as string;
                    if (!string.IsNullOrEmpty(existingValue))
                    {
                        key.DeleteValue(StartupRunValueName, false);
                        PrintTimedMessage(ConsoleColor.Yellow, "Start with Windows disabled.");
                    }
                    else
                    {
                        key.SetValue(StartupRunValueName, GetPrechosenStartupCommand(), RegistryValueKind.String);
                        PrintTimedMessage(ConsoleColor.Green, "Start with Windows enabled. Prechosen Apps will run in tray mode.");
                    }
                }
            }
            catch (Exception ex)
            {
                PrintTimedMessage(ConsoleColor.Red, "Could not update startup setting: " + ex.Message);
            }
        }

        static string GetPrechosenStartupCommand()
        {
            return "\"" + Assembly.GetEntryAssembly().Location + "\" --prechosen --tray";
        }

        static void PrintTimedMessage(ConsoleColor color, string message)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = ConsoleColor.White;
            Thread.Sleep(2000);
        }

        static void LimitRamForProcess(string name, int min, int max, int interval)
        {
            LimitRamForTargets(new[] { new ProcessTarget(name, new[] { name }, new string[0]) }, min, max, interval, name.ToUpper());
        }

        static void LimitRamForTargets(IEnumerable<ProcessTarget> targets, int min, int max, int interval, string label)
        {
            while (true)
            {
                try
                {
                    int failed;
                    int trimmed = TrimTargets(targets, new IntPtr(min), new IntPtr(max), out failed);
                    RamUsage ram = GetRamUsage();

                    Console.ForegroundColor = trimmed > 0 ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
                    if (ram != null)
                    {
                        Console.WriteLine("{0}: trimmed {1} process(es), failed {2}, RAM {3}", label, trimmed, failed, ram.ToDisplayString());
                    }
                    else
                    {
                        Console.WriteLine("{0}: trimmed {1} process(es), failed {2}", label, trimmed, failed);
                    }
                    Console.ForegroundColor = ConsoleColor.White;
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Error limiting RAM for {0}: {1}", label, ex.Message);
                    Console.ForegroundColor = ConsoleColor.White;
                }

                Thread.Sleep(interval);
            }
        }

        static void RunPrechosenTrayMode()
        {
            IntPtr consoleWindow = GetConsoleWindow();
            if (consoleWindow != IntPtr.Zero)
                ShowWindow(consoleWindow, SW_HIDE);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new PrechosenTrayContext());
        }

        class PrechosenTrayContext : ApplicationContext
        {
            readonly NotifyIcon notifyIcon;
            volatile bool running = true;

            public PrechosenTrayContext()
            {
                notifyIcon = new NotifyIcon
                {
                    Icon = LoadTrayIcon(),
                    Text = "RAM Limiter: starting",
                    Visible = true,
                    ContextMenu = new ContextMenu(new[]
                    {
                        new MenuItem("Exit", Exit)
                    })
                };

                notifyIcon.DoubleClick += delegate { ShowStatusBalloon(); };
                ShowStatusBalloon();

                new Thread(TrimLoop) { IsBackground = true }.Start();
            }

            static Icon LoadTrayIcon()
            {
                try
                {
                    return Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location) ?? SystemIcons.Application;
                }
                catch
                {
                    return SystemIcons.Application;
                }
            }

            void TrimLoop()
            {
                while (running)
                {
                    string status;
                    try
                    {
                        int failed;
                        int trimmed = TrimTargets(GetPrechosenTargets(), new IntPtr(-1), new IntPtr(-1), out failed);
                        RamUsage ram = GetRamUsage();
                        status = ram != null
                            ? string.Format("RAM Limiter: trimmed {0}, failed {1}, RAM {2}", trimmed, failed, ram.ToDisplayString())
                            : string.Format("RAM Limiter: trimmed {0}, failed {1}", trimmed, failed);
                    }
                    catch (Exception ex)
                    {
                        status = "RAM Limiter error: " + ex.Message;
                    }

                    UpdateTrayStatus(status);

                    for (int i = 0; running && i < 50; i++)
                        Thread.Sleep(100);
                }
            }

            void UpdateTrayStatus(string status)
            {
                if (notifyIcon == null)
                    return;

                string tooltip = status.Length > 63 ? status.Substring(0, 63) : status;
                try
                {
                    notifyIcon.Text = tooltip;
                }
                catch
                {
                }
            }

            void ShowStatusBalloon()
            {
                notifyIcon.ShowBalloonTip(
                    3000,
                    "RAM Limiter",
                    "Prechosen app limiter is running. Right-click tray icon to exit.",
                    ToolTipIcon.Info);
            }

            void Exit(object sender, EventArgs e)
            {
                running = false;
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
                ExitThread();
            }
        }

        static void CustomRamLimiter(int min, int max)
        {
            Console.WriteLine("Enter process names separated by commas (e.g., chrome, obs64, discord):");
            var names = Console.ReadLine().Split(',').Select(p => p.Trim().ToLower());

            foreach (var name in names)
            {
                var processName = name;
                new Thread(() => LimitRamForProcess(processName, min, max, 3000)) { IsBackground = true }.Start();
            }
            Thread.Sleep(Timeout.Infinite);
        }

        static void StartPrechosenLimiter()
        {
            if (prechosenLimiterStarted)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Prechosen app limiter is already running.");
                Console.ForegroundColor = ConsoleColor.White;
                Thread.Sleep(2000);
                return;
            }

            prechosenLimiterStarted = true;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Started prechosen app limiter.");
            RamUsage ram = GetRamUsage();
            if (ram != null)
                Console.WriteLine("RAM before start: " + ram.ToDisplayString());
            Console.WriteLine("Targets: " + string.Join(", ", GetPrechosenTargets().Select(t => t.DisplayName)));
            Console.WriteLine("Skipped: pagefile (not an app process).");
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.ForegroundColor = ConsoleColor.White;

            LimitRamForTargets(GetPrechosenTargets(), -1, -1, 5000, "PRECHOSEN");
        }

        static void StartRamAndCpuLimiters()
        {
            if (prechosenLimiterStarted || cpuLimiterStarted)
            {
                PrintTimedMessage(ConsoleColor.Yellow, "RAM or CPU limiter is already running.");
                return;
            }

            prechosenLimiterStarted = true;
            cpuLimiterStarted = true;

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Started RAM + CPU limiters.");
            RamUsage ram = GetRamUsage();
            if (ram != null)
                Console.WriteLine("RAM before start: " + ram.ToDisplayString());
            Console.WriteLine("RAM targets: " + string.Join(", ", GetPrechosenTargets().Select(t => t.DisplayName)));
            PrintCpuModeSummary(LoadCpuModeSettings());
            Console.WriteLine("Press Ctrl+C to stop.");
            Console.ForegroundColor = ConsoleColor.White;

            LimitRamAndCpuForTargets(GetPrechosenTargets(), PrechosenTargets, 5000);
        }

        static void LimitRamAndCpuForTargets(IEnumerable<ProcessTarget> ramTargets, IEnumerable<ProcessTarget> cpuTargets, int interval)
        {
            while (true)
            {
                int ramFailed;
                int ramTrimmed = TrimTargets(ramTargets, new IntPtr(-1), new IntPtr(-1), out ramFailed);
                RamUsage ram = GetRamUsage();

                var cpuSettings = LoadCpuModeSettings();
                CpuThrottleStats cpuStats = ApplyCpuModesForTargets(cpuTargets, cpuSettings);

                Console.ForegroundColor = (ramTrimmed > 0 || cpuStats.Applied > 0) ? ConsoleColor.Cyan : ConsoleColor.DarkGray;
                if (ram != null)
                    Console.WriteLine("RAM: trimmed {0} process(es), failed {1}, usage {2}", ramTrimmed, ramFailed, ram.ToDisplayString());
                else
                    Console.WriteLine("RAM: trimmed {0} process(es), failed {1}", ramTrimmed, ramFailed);
                Console.WriteLine("CPU: applied {0} process(es), failed {1}, usage {2}", cpuStats.Applied, cpuStats.Failed, FormatCpuUsage(cpuStats.UsagePercent));
                Console.ForegroundColor = ConsoleColor.White;

                Thread.Sleep(interval);
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            if (HasArg(args, "--prechosen") && HasArg(args, "--tray"))
            {
                RunPrechosenTrayMode();
                return;
            }

            new Thread(() =>
            {
                var s = "        ";
                var random = new Random();
                while (true)
                {
                    switch (random.Next(1, 3))
                    {
                        case 1:
                            Console.Title = "Have you starred the repo? " + s + "| github.com/0vm";
                            break;
                        case 2:
                            Console.Title = "https://github.com/0vm/RAM-Limiter  " + s + "| github.com/0vm";
                            break;
                    }
                    Thread.Sleep(2000);
                }
            })
            { IsBackground = true }.Start();

            ElevatePrivileges(string.Concat(args));

            while (true)
            {
                Console.Clear();
                Console.WriteLine("Limit Discord: 1");
                Console.WriteLine("Limit Chrome: 2");
                Console.WriteLine("Limit OBS: 3");
                Console.WriteLine("Limit Discord & Chrome: 4");
                Console.WriteLine("Limit Custom: 5");
                Console.WriteLine("Limit Prechosen Apps: 6");
                Console.WriteLine("Add EXE to Prechosen Apps: 7");
                Console.WriteLine("Start Prechosen Apps with Windows: 8 [{0}]", IsPrechosenStartupEnabled() ? "On" : "Off");
                Console.WriteLine("Start CPU Throttling: 9");
                Console.WriteLine("Configure CPU Throttling: C");
                Console.WriteLine("Start RAM + CPU Limiters: B");
                Console.WriteLine("Exit: 0");

                var key = Console.ReadKey(true).Key;

                if (key == ConsoleKey.D1 || key == ConsoleKey.NumPad1)
                    new Thread(() => LimitRamForProcess("discord", -1, -1, 5000)) { IsBackground = true }.Start();
                else if (key == ConsoleKey.D2 || key == ConsoleKey.NumPad2)
                    new Thread(() => LimitRamForProcess("chrome", -1, -1, 5000)) { IsBackground = true }.Start();
                else if (key == ConsoleKey.D3 || key == ConsoleKey.NumPad3)
                    new Thread(() => LimitRamForProcess("obs64", -1, -1, 5000)) { IsBackground = true }.Start();
                else if (key == ConsoleKey.D4 || key == ConsoleKey.NumPad4)
                {
                    new Thread(() => LimitRamForProcess("discord", -1, -1, 5000)) { IsBackground = true }.Start();
                    new Thread(() => LimitRamForProcess("chrome", -1, -1, 5000)) { IsBackground = true }.Start();
                }
                else if (key == ConsoleKey.D5 || key == ConsoleKey.NumPad5)
                    CustomRamLimiter(-1, -1);
                else if (key == ConsoleKey.D6 || key == ConsoleKey.NumPad6)
                    StartPrechosenLimiter();
                else if (key == ConsoleKey.D7 || key == ConsoleKey.NumPad7)
                    AddCustomPrechosenExecutable();
                else if (key == ConsoleKey.D8 || key == ConsoleKey.NumPad8)
                    TogglePrechosenStartup();
                else if (key == ConsoleKey.D9 || key == ConsoleKey.NumPad9)
                    StartCpuThrottleLimiter();
                else if (key == ConsoleKey.C)
                    ConfigureCpuModes();
                else if (key == ConsoleKey.B)
                    StartRamAndCpuLimiters();
                else if (key == ConsoleKey.D0 || key == ConsoleKey.NumPad0)
                    Environment.Exit(0);
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("Invalid input. Try again.");
                    Thread.Sleep(2000);
                    Console.ForegroundColor = ConsoleColor.White;
                }
            }
        }

        static bool HasArg(string[] args, string name)
        {
            return args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
