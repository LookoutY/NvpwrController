using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Nvpwr
{
    public static class AppPaths
    {
        public static readonly string ExecutableDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
        public static readonly string Root = Path.Combine(ExecutableDirectory, "NvpwrData");

        public static string Beside(params string[] parts)
        {
            return Path.Combine(new[] { Root }.Concat(parts).ToArray());
        }
    }

    public sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
    }

    public sealed class SecurityState
    {
        public bool? TestSigning, BootTestSigning, SecureBoot, HvciRunning, HvciConfigured, CodeIntegrityEnabled, VbsRunning, BlocklistConfigured;
        public string Firmware = "Unknown";
        public uint? CodeIntegrityFlags;
        public readonly List<string> Errors = new List<string>();
    }

    public sealed class GpuInfo
    {
        public string Name, DriverVersion, InstanceId;
    }

    public sealed class DriverService
    {
        public string State, Path;
        public uint Type = 1;
    }

    public sealed class SystemSnapshot
    {
        public GpuInfo Gpu;
        public string GpuError;
        public SecurityState Security;
        public string ServiceState = "Unknown";
        public string ServicePath;
    }

    public sealed class AssetInfo
    {
        public string Key, Resource, RelativePath;
    }

    public sealed class BundleAssets
    {
        public const string Version = "3.0.6";
        public static readonly AssetInfo[] Files = {
            new AssetInfo { Key = "driver", Resource = "Nvpwr.Payload.Nvpwr.sys", RelativePath = "Nvpwr.sys" },
            new AssetInfo { Key = "controller", Resource = "Nvpwr.Payload.NvpwrCtl.exe", RelativePath = "NvpwrCtl.exe" },
            new AssetInfo { Key = "kdu", Resource = "Nvpwr.Payload.KDU.exe", RelativePath = "KDU.exe" },
            new AssetInfo { Key = "database", Resource = "Nvpwr.Payload.drv64.dll", RelativePath = "drv64.dll" },
            new AssetInfo { Key = "efifix", Resource = "Nvpwr.Payload.EfiDSEFix.exe", RelativePath = "EfiDSEFix.exe" },
            new AssetInfo { Key = "efiboot", Resource = "Nvpwr.Payload.bootx64.efi", RelativePath = @"EFI\Boot\bootx64.efi" },
            new AssetInfo { Key = "efidriver", Resource = "Nvpwr.Payload.EfiGuardDxe.efi", RelativePath = @"EFI\Boot\EfiGuardDxe.efi" }
        };
        public readonly string Root = AppPaths.Root;

        public bool Exists(string key)
        {
            AssetInfo file = Files.Single(item => item.Key == key);
            return Assembly.GetExecutingAssembly().GetManifestResourceNames().Contains(file.Resource);
        }

        public byte[] ReadResource(string name)
        {
            using (Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (source == null) throw new FileNotFoundException("Missing embedded resource: " + name);
                using (var target = new MemoryStream()) { source.CopyTo(target); return target.ToArray(); }
            }
        }

        public void VerifyBundle()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            if (assembly.GetReferencedAssemblies().Any(reference => reference.Name == "System.Management.Automation"))
                throw new InvalidDataException("Unexpected PowerShell runtime dependency.");
            string[] scripts = { ".ps1", ".psm1", ".bat", ".cmd" };
            if (assembly.GetManifestResourceNames().Any(name => scripts.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("Unexpected embedded startup script.");
            foreach (AssetInfo file in Files)
                if (ReadResource(file.Resource).Length < 100) throw new InvalidDataException("Invalid embedded file: " + file.Key);
        }

        public string Ensure(string key)
        {
            AssetInfo file = Files.Single(item => item.Key == key);
            string path = Path.Combine(Root, Path.GetFileName(file.RelativePath));
            byte[] expected = ReadResource(file.Resource);
            if (File.Exists(path) && SameHash(expected, File.ReadAllBytes(path))) return path;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, expected);
            try { if (File.Exists(path)) File.Delete(path); File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return path;
        }

        public void ExportEfi(string folder)
        {
            foreach (AssetInfo file in Files.Where(item => item.Key == "efiboot" || item.Key == "efidriver"))
            {
                string path = Path.Combine(folder, file.RelativePath);
                if (File.Exists(path)) throw new IOException("EFI export refuses to overwrite: " + path);
            }
            foreach (AssetInfo file in Files.Where(item => item.Key == "efiboot" || item.Key == "efidriver"))
            {
                string path = Path.Combine(folder, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, ReadResource(file.Resource));
            }
        }

        private static bool SameHash(byte[] first, byte[] second)
        {
            using (SHA256 hash = SHA256.Create()) { return hash.ComputeHash(first).SequenceEqual(hash.ComputeHash(second)); }
        }
    }

    public interface ISystemServices
    {
        bool Administrator { get; }
        SecurityState ReadSecurity();
        GpuInfo ReadGpu();
        DriverService ReadDriver();
        void WaitDriver(string state);
        string Asset(string key);
        ProcessResult Run(string executable, string arguments);
    }

    public sealed class WindowsServices : ISystemServices
    {
        private readonly BundleAssets assets;
        public WindowsServices(BundleAssets assets) { this.assets = assets; }
        public bool Administrator
        {
            get { using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
        }

        public string Asset(string key)
        {
            if (key == "kdu") assets.Ensure("database");
            return assets.Ensure(key);
        }

        public ProcessResult Run(string executable, string arguments)
        {
            if (Path.IsPathRooted(executable) && string.Equals(Path.GetFileName(executable), "EfiDSEFix.exe", StringComparison.OrdinalIgnoreCase))
                return ConsoleProcess.Run(executable, arguments);
            if (!Path.IsPathRooted(executable))
            {
                if (!new[] { "sc.exe", "bcdedit.exe", "pnputil.exe" }.Contains(executable)) throw new InvalidOperationException("Executable must have an absolute path.");
                executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), executable);
            }
            var info = new ProcessStartInfo(executable, arguments) {
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            using (var process = new Process { StartInfo = info })
            {
                if (!process.Start()) throw new IOException("Cannot start " + executable);
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(90000)) { process.Kill(); process.WaitForExit(); throw new System.TimeoutException("Process timeout: " + executable); }
                return new ProcessResult { ExitCode = process.ExitCode, Output = (stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult()).Trim() };
            }
        }

        public static string Quote(string value)
        {
            var text = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { slashes++; continue; }
                text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                text.Append(character);
                slashes = 0;
            }
            return text.Append('\\', slashes * 2).Append('"').ToString();
        }

        private static ManagementObjectSearcher Query(string scope, string query)
        {
            return new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query),
                new EnumerationOptions { Timeout = TimeSpan.FromSeconds(8), ReturnImmediately = false });
        }

        public GpuInfo ReadGpu()
        {
            var found = new List<GpuInfo>();
            using (ManagementObjectSearcher search = Query(@"root\cimv2", "SELECT Name,DriverVersion,PNPDeviceID FROM Win32_VideoController WHERE Name LIKE '%NVIDIA%'"))
            using (ManagementObjectCollection rows = search.Get())
                foreach (ManagementObject row in rows)
                    using (row) found.Add(new GpuInfo { Name = Convert.ToString(row["Name"]), DriverVersion = Convert.ToString(row["DriverVersion"]), InstanceId = Convert.ToString(row["PNPDeviceID"]) });
            if (found.Count != 1) throw new InvalidOperationException("Expected exactly one NVIDIA GPU; found " + found.Count + ".");
            if (!Regex.IsMatch(found[0].Name, @"(?i)\bRTX\s*-?\s*(3050|3060|3070|3080|4050|4060|4070|4080|4090|5050|5060|5070|5080|5090)\s*(Ti)?\s+Laptop\s+GPU\b"))
                throw new InvalidOperationException("Unsupported NVIDIA Laptop GPU: " + found[0].Name);
            return found[0];
        }

        public DriverService ReadDriver()
        {
            return Native.QueryDriverService("Nvpwr");
        }

        public void WaitDriver(string state)
        {
            using (var service = new ServiceController("Nvpwr"))
                service.WaitForStatus((ServiceControllerStatus)Enum.Parse(typeof(ServiceControllerStatus), state), TimeSpan.FromSeconds(10));
        }

        public SystemSnapshot Snapshot()
        {
            var snapshot = new SystemSnapshot { Security = ReadSecurity() };
            try { snapshot.Gpu = ReadGpu(); } catch (Exception error) { snapshot.GpuError = error.Message; }
            try {
                DriverService service = ReadDriver();
                snapshot.ServiceState = service == null ? "NotInstalled" : service.State;
                snapshot.ServicePath = service == null ? null : service.Path;
            }
            catch (Exception error) { snapshot.Security.Errors.Add(error.Message); }
            return snapshot;
        }

        private static bool? RegistryFlag(string path, string name)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(path))
            {
                object value = key == null ? null : key.GetValue(name);
                return value == null ? (bool?)null : Convert.ToInt32(value) != 0;
            }
        }

        public SecurityState ReadSecurity()
        {
            var state = new SecurityState();
            try {
                uint flags = Native.QueryCodeIntegrity();
                state.CodeIntegrityFlags = flags; state.TestSigning = (flags & 2) != 0;
                state.CodeIntegrityEnabled = (flags & 1) != 0; state.HvciRunning = (flags & 0x400) != 0;
            } catch (Exception error) { state.Errors.Add(error.Message); }
            try {
                uint firmware = Native.QueryFirmwareType();
                state.Firmware = firmware == 2 ? "UEFI" : firmware == 1 ? "Legacy" : "Unknown";
                if (firmware == 2) state.SecureBoot = RegistryFlag(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled");
            } catch (Exception error) { state.Errors.Add(error.Message); }
            try {
                var scope = new ManagementScope(@"\\.\root\WMI", new ConnectionOptions { EnablePrivileges = true });
                using (var store = new ManagementObject(scope, new ManagementPath("BcdStore.FilePath=\"\""), null))
                using (ManagementBaseObject parameters = store.GetMethodParameters("OpenObject")) {
                    parameters["Id"] = "{fa926493-6f1c-4193-a414-58f0b2456d1e}";
                    using (ManagementBaseObject opened = store.InvokeMethod("OpenObject", parameters, null)) {
                        if (!Convert.ToBoolean(opened["ReturnValue"])) throw new InvalidOperationException("BCD entry unavailable.");
                        var data = (ManagementBaseObject)opened["Object"];
                        string id = Convert.ToString(data["Id"]);
                        if (!Regex.IsMatch(id, @"^\{[0-9a-fA-F-]{36}\}$")) throw new InvalidDataException("Invalid BCD object identifier.");
                        using (var entry = new ManagementObject(scope, new ManagementPath("BcdObject.Id=\"" + id + "\",StoreFilePath=\"\""), null))
                        using (ManagementBaseObject request = entry.GetMethodParameters("GetElement")) {
                            request["Type"] = (uint)0x16000049;
                            using (ManagementBaseObject result = entry.InvokeMethod("GetElement", request, null))
                                if (Convert.ToBoolean(result["ReturnValue"])) state.BootTestSigning = Convert.ToBoolean(((ManagementBaseObject)result["Element"])["Boolean"]);
                        }
                    }
                }
            } catch (Exception error) { state.Errors.Add("BCD: " + error.Message); }
            try {
                using (ManagementObjectSearcher search = Query(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning,SecurityServicesConfigured,VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard"))
                using (ManagementObjectCollection rows = search.Get())
                    foreach (ManagementObject row in rows) using (row) {
                        uint[] running = row["SecurityServicesRunning"] as uint[];
                        if (running != null) {
                            bool hvci = running.Contains((uint)2);
                            if (state.HvciRunning.HasValue && state.HvciRunning.Value != hvci) { state.HvciRunning = null; state.Errors.Add("HVCI sources disagree."); }
                            else state.HvciRunning = hvci;
                        }
                        uint[] configured = row["SecurityServicesConfigured"] as uint[];
                        if (configured != null) state.HvciConfigured = configured.Contains((uint)2);
                        state.VbsRunning = Convert.ToUInt32(row["VirtualizationBasedSecurityStatus"]) == 2;
                    }
            } catch (Exception error) { state.Errors.Add(error.Message); }
            try {
                bool? configured = RegistryFlag(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled");
                if (configured.HasValue) state.HvciConfigured = configured;
                state.BlocklistConfigured = RegistryFlag(@"SYSTEM\CurrentControlSet\Control\CI\Config", "VulnerableDriverBlocklistEnable");
            } catch (Exception error) { state.Errors.Add(error.Message); }
            return state;
        }
    }
}