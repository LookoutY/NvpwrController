using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Nvpwr
{
    public sealed class DriverSession
    {
        public bool Ready, OwnsDriver, NeedsRestore, RestartRequired, OriginalKnown;
        public uint OriginalDse = 6;
        public decimal? LastReadback;
        public DriverBackend Backend;
        public PowerRange PowerRange;
        public string PowerRangeError;
    }

    public sealed class DriverEngine
    {
        private readonly ISystemServices system;
        private readonly Action<string> log;
        public readonly DriverSession Session = new DriverSession();

        public DriverEngine(ISystemServices system, Action<string> log) { this.system = system; this.log = log; }
        private void RequireAdmin() { if (!system.Administrator) throw new InvalidOperationException("Administrator privileges are required."); }
        private ProcessResult Execute(string path, string arguments, params int[] allowed)
        {
            ProcessResult result = system.Run(path, arguments);
            if (!string.IsNullOrWhiteSpace(result.Output)) log(result.Output);
            if (allowed.Length != 0 && Array.IndexOf(allowed, result.ExitCode) < 0)
                throw new InvalidOperationException("Process exit " + result.ExitCode + ": " + path + "\n" + result.Output);
            return result;
        }

        public void Load(DriverBackend backend)
        {
            RequireAdmin();
            if (Session.NeedsRestore) throw new InvalidOperationException("DSE recovery is pending.");
            if (Session.Ready || Session.OwnsDriver) throw new InvalidOperationException("End the active session before loading or switching backend.");
            SecurityState security = system.ReadSecurity();
            if (security.HvciRunning != false || security.CodeIntegrityEnabled != true) throw new InvalidOperationException("HVCI must be off and kernel Code Integrity must be enabled and known.");
            if (backend == DriverBackend.EfiGuard && (security.Firmware != "UEFI" || security.VbsRunning != false))
                throw new InvalidOperationException("EfiGuard requires UEFI and VBS/HVCI disabled. Unknown state is not accepted.");
            system.ReadGpu();
            string driver = system.Asset("driver");
            system.Asset("controller");
            string helper = system.Asset(backend == DriverBackend.Kdu ? "kdu" : "efifix");
            Session.Backend = backend;
            Session.OriginalKnown = false;
            Session.OriginalDse = 6;
            if (backend == DriverBackend.EfiGuard) {
                ProcessResult check = Execute(helper, "-c", 0);
                if (!Regex.IsMatch(check.Output, @"(?i)\bSuccess\.")) throw new InvalidOperationException("EfiGuard EFI hook was not confirmed. Boot through EfiGuard before loading.");
                Session.OriginalDse = ReadEfiFlags(helper);
                Session.OriginalKnown = true;
                if (Session.OriginalDse == 0) throw new InvalidOperationException("EfiGuard reports DSE already disabled. Reboot to a known state first.");
            }
            DriverService service = system.ReadDriver();
            if (service == null) {
                ProcessResult created = Execute("sc.exe", "create Nvpwr type= kernel start= demand binPath= " + WindowsServices.Quote(driver), 0, 1073);
                if (created.ExitCode == 1073) log("Nvpwr already exists; querying its current configuration before continuing.");
                service = system.ReadDriver();
                if (service == null) throw new InvalidOperationException("Nvpwr service configuration could not be confirmed after creation. No DSE change was attempted.");
            }
            if (service.Type != 1) throw new InvalidOperationException("The Nvpwr name is used by a non-kernel-driver service. No changes were made to that service.");
            {
                string path = service.Path.Trim('"');
                if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path.Substring(4);
                if (service.State == "Running") {
                    if (!string.Equals(path, driver, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("A different Nvpwr driver is already running. Close its owning application first.");
                    Session.Ready = true;
                    log("Attached to an existing driver; this window will not unload an externally owned session.");
                    RefreshPowerRange();
                    return;
                }
                if (service.State != "Stopped") throw new InvalidOperationException("Nvpwr is not stopped: " + service.State);
                if (!string.Equals(path, driver, StringComparison.OrdinalIgnoreCase) || service.Path.Contains("\""))
                    Execute("sc.exe", "config Nvpwr type= kernel start= demand binPath= " + WindowsServices.Quote(driver), 0);
            }
            Exception failure = null;
            Session.NeedsRestore = true;
            try {
                if (backend == DriverBackend.Kdu) {
                    ProcessResult result = Execute(helper, "-dse 0");
                    Match original = Regex.Match(result.Output, @"(?im)DSE flags[^\r\n]*\bvalue:\s*(?:0x)?([0-9a-f]+),\s*new value");
                    if (original.Success) { Session.OriginalDse = uint.Parse(original.Groups[1].Value, NumberStyles.HexNumber); Session.OriginalKnown = true; }
                    if (result.ExitCode != 1 || !result.Output.Contains("Write result verification succeeded")) throw new InvalidOperationException("KDU did not verify the DSE write.");
                    if (!Session.OriginalKnown) throw new InvalidOperationException("Original DSE flags were not captured. Recovery uses legacy value 6; reboot is recommended.");
                } else {
                    ProcessResult result = Execute(helper, "-d", 0);
                    if (result.Output.IndexOf("Successfully disabled DSE", StringComparison.OrdinalIgnoreCase) < 0 || ReadEfiFlags(helper) != 0)
                        throw new InvalidOperationException("EfiGuard did not verify disabled DSE.");
                }
                Session.OwnsDriver = true;
                Execute("sc.exe", "start Nvpwr", 0);
                system.WaitDriver("Running");
                DriverService loaded = system.ReadDriver();
                if (loaded == null || loaded.State != "Running") throw new InvalidOperationException("Nvpwr did not reach Running state.");
            } catch (Exception error) { failure = error; }
            finally {
                try { Restore(); }
                catch (Exception error) { failure = new InvalidOperationException((failure == null ? "" : failure.Message + "\n") + "DSE RECOVERY FAILED: " + error.Message); }
            }
            if (failure != null) throw failure;
            Session.Ready = true;
            Session.RestartRequired = false;
            log("Driver ready; temporary DSE load window closed.");
            RefreshPowerRange();
        }

        public void RefreshPowerRange()
        {
            Session.PowerRange = null;
            Session.PowerRangeError = null;
            if (!Session.Ready || Session.NeedsRestore || Session.RestartRequired) return;
            try {
                system.ReadGpu();
                ProcessResult status = Execute(system.Asset("controller"), "status", 0);
                Session.PowerRange = PowerProtocol.ReadRange(status.Output);
                log(string.Format(CultureInfo.InvariantCulture, "OEM baseline={0:0.###} W; selectable range={1}-{2} W; step={3} W. Software ceiling is not a hardware rating.",
                    Session.PowerRange.OemWatts, Session.PowerRange.MinimumWatts, Session.PowerRange.MaximumWatts, PowerProtocol.StepWatts));
            } catch (Exception error) {
                Session.PowerRangeError = error.Message;
                log("Power adjustment disabled: " + error.Message);
            }
        }

        private uint ReadEfiFlags(string helper)
        {
            ProcessResult read = Execute(helper, "-r", 0);
            Match value = Regex.Match(read.Output, @"(?i)Success\.\s*g_Ci(?:Options|Enabled)\s+value:\s*0x([0-9a-f]+)");
            if (!value.Success) throw new InvalidOperationException("EfiGuard did not return a verified current DSE value.");
            return uint.Parse(value.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        public void Restore()
        {
            RequireAdmin();
            if (!Session.NeedsRestore) return;
            if (Session.Backend == DriverBackend.Kdu) {
                ProcessResult result = Execute(system.Asset("kdu"), "-dse " + Session.OriginalDse.ToString(CultureInfo.InvariantCulture), 1);
                if (!result.Output.Contains("Write result verification succeeded")) throw new InvalidOperationException("KDU recovery verification missing.");
            } else {
                string helper = system.Asset("efifix");
                if (!Session.OriginalKnown || Session.OriginalDse == 0) throw new InvalidOperationException("Original EfiGuard DSE value is unknown.");
                Execute(helper, "-e " + Session.OriginalDse.ToString("X", CultureInfo.InvariantCulture), 0);
                if (ReadEfiFlags(helper) != Session.OriginalDse) throw new InvalidOperationException("EfiGuard DSE recovery readback mismatch.");
            }
            Session.NeedsRestore = false;
            log("Original DSE flags restored and verified: 0x" + Session.OriginalDse.ToString("X"));
        }

        public string Stop()
        {
            RequireAdmin();
            Exception failure = null;
            try {
                if (Session.OwnsDriver) {
                    Execute("sc.exe", "stop Nvpwr", 0, 1060, 1062);
                    if (system.ReadDriver() != null) system.WaitDriver("Stopped");
                    Session.OwnsDriver = false;
                }
            } catch (Exception error) { failure = error; }
            finally {
                Session.Ready = false;
                Session.PowerRange = null;
                Session.PowerRangeError = null;
                try { if (Session.NeedsRestore) Restore(); }
                catch (Exception error) { failure = new InvalidOperationException((failure == null ? "" : failure.Message + "\n") + "DSE RECOVERY FAILED: " + error.Message); }
            }
            if (failure != null) throw failure;
            DriverService service = system.ReadDriver();
            log("Session ended. Unloading the helper is not an OEM power reset.");
            return service == null ? "NotInstalled" : service.State;
        }

        public PowerResult SetPower(int watts)
        {
            RequireAdmin();
            if (!Session.Ready || Session.NeedsRestore || Session.RestartRequired) throw new InvalidOperationException("Driver session is not ready.");
            if (!PowerProtocol.IsValid(watts)) throw new ArgumentOutOfRangeException("watts", "Target must be within the software bounds in 5 W steps.");
            RefreshPowerRange();
            if (Session.PowerRange == null) throw new InvalidOperationException(Session.PowerRangeError ?? "OEM baseline is unavailable; no power write was attempted.");
            if (!Session.PowerRange.Contains(watts)) throw new ArgumentOutOfRangeException("watts", string.Format(CultureInfo.InvariantCulture,
                "Target must be {0}-{1} W in 5 W steps. Values below the current OEM baseline ({2:0.###} W) are not supported by this loading path.",
                Session.PowerRange.MinimumWatts, Session.PowerRange.MaximumWatts, Session.PowerRange.OemWatts));
            Session.LastReadback = null;
            log("NvpwrCtl set 5050 " + watts);
            string controller = system.Asset("controller");
            var timer = Stopwatch.StartNew();
            ProcessResult raw = Execute(controller, "set 5050 " + watts);
            timer.Stop();
            PowerResult result = PowerProtocol.Parse(raw.Output, raw.ExitCode, watts);
            result.DurationMilliseconds = timer.ElapsedMilliseconds;
            Session.LastReadback = result.Watts;
            Session.RestartRequired = result.RestartRequired;
            log(string.Format(CultureInfo.InvariantCulture,
                "Power result: target={0} W; readback={1}; exit={2}; NVIDIA={3}; NTSTATUS={4}; elapsed={5} ms; outcome={6}.",
                watts, result.Watts.HasValue ? result.Watts.Value.ToString("0.###", CultureInfo.InvariantCulture) + " W" : "unknown",
                result.ExitCode, result.NvidiaStatus.HasValue ? "0x" + result.NvidiaStatus.Value.ToString("X8") : "unknown",
                result.NtStatus.HasValue ? "0x" + result.NtStatus.Value.ToString("X8") : "unknown", result.DurationMilliseconds, result.Outcome));
            return result;
        }

        public bool RestartGpu()
        {
            RequireAdmin();
            GpuInfo gpu = system.ReadGpu();
            if (!Regex.IsMatch(gpu.InstanceId, @"^PCI\\VEN_10DE[^""\r\n]+$")) throw new InvalidOperationException("Invalid NVIDIA device instance ID.");
            Stop();
            DriverService service = system.ReadDriver();
            if (service != null && service.State != "Stopped") throw new InvalidOperationException("An external driver session is still running.");
            ProcessResult result = Execute("pnputil.exe", "/restart-device " + WindowsServices.Quote(gpu.InstanceId), 0, 3010);
            Session.RestartRequired = result.ExitCode == 3010;
            Session.LastReadback = null;
            return Session.RestartRequired;
        }

        public SecurityState SetTestSigning(bool enabled)
        {
            RequireAdmin();
            if (Session.NeedsRestore) throw new InvalidOperationException("Restore DSE before changing the next-boot configuration.");
            SecurityState before = system.ReadSecurity();
            if (before.BootTestSigning == enabled) {
                log("The requested test-signing boot configuration is already set; no change was made.");
                return before;
            }
            if (enabled && before.Firmware != "Legacy" && before.SecureBoot != false)
                throw new InvalidOperationException("Secure Boot is enabled or unknown. Test signing cannot be enabled.");
            Execute("bcdedit.exe", "/set {current} testsigning " + (enabled ? "on" : "off"), 0);
            SecurityState after = system.ReadSecurity();
            if (after.BootTestSigning != enabled)
                throw new InvalidOperationException("The command completed, but the requested BCD value could not be verified. Refresh the status before retrying; the boot configuration may have changed.");
            log("Test-signing boot configuration verified: " + (enabled ? "on" : "off") + ". Restart Windows manually to apply; no driver, certificate or other security setting was changed.");
            return after;
        }

    }
}