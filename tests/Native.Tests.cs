using System;
using System.Collections.Generic;
using System.Linq;
using Nvpwr;

internal static class NativeTests
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        assertions++;
    }

    private static int Main() { return Run(); }

    public static int Run()
    {
        try
        {
            assertions = 0;
            Check(typeof(DriverEngine).GetMethod("SetTestSigning") != null, "Explicit test-mode settings operation is missing");
            string executableDirectory = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string dataDirectory = System.IO.Path.Combine(executableDirectory, "NvpwrData");
            Check(string.Equals(AppPaths.ExecutableDirectory.TrimEnd('\\'), executableDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase), "Executable directory mismatch");
            Check(string.Equals(AppPaths.Root, dataDirectory, StringComparison.OrdinalIgnoreCase), "Portable data is not contained in NvpwrData");
            Check(new BundleAssets().Root == AppPaths.Root, "Assets escaped the portable data directory");
            foreach (string name in new[] { "backend.txt", "startup.log", "README.txt" })
                Check(string.Equals(System.IO.Path.GetDirectoryName(AppPaths.Beside(name)), dataDirectory, StringComparison.OrdinalIgnoreCase), "State escaped NvpwrData");
            foreach (AssetInfo file in BundleAssets.Files.Where(asset => asset.Key != "efiboot" && asset.Key != "efidriver"))
                Check(System.IO.Path.GetFileName(file.RelativePath) == file.RelativePath, "Runtime component is not a sibling file");
            foreach (int baseline in new[] { 45, 80, 115, 140, 175 })
                Check(PowerProtocol.ReadOemBaseline("OEM baseline : " + baseline * 1000 + " (" + baseline + ".000 W)") == baseline, "Device OEM baseline was not read: " + baseline);
            Check(PowerProtocol.ReadOemBaseline("OEM baseline : 115500 (115.500 W)") == 115.5m, "Fractional OEM baseline was rounded");
            Check(!PowerProtocol.ReadOemBaseline(null).HasValue, "Missing OEM baseline became a fixed fallback");
            Check(!PowerProtocol.ReadOemBaseline("OEM baseline : 0 (0.000 W)").HasValue, "Uninitialized OEM baseline accepted");
            Check(!PowerProtocol.ReadOemBaseline("OEM baseline : 140000 (115.000 W)").HasValue, "Inconsistent OEM units accepted");
            Check(!PowerProtocol.ReadOemBaseline("OEM baseline : 115000 (115.000 W)\nOEM baseline : 140000 (140.000 W)").HasValue, "Ambiguous OEM baseline accepted");
            Check(!PowerProtocol.ReadOemBaseline("OEM baseline : 115000 (115.000 W)\nOEM baseline : unknown").HasValue, "Malformed duplicate OEM baseline accepted");
            foreach (int baseline in new[] { 45, 80, 115, 140, 175 }) {
                var range = new PowerRange(baseline);
                Check(range.Targets().First() == baseline && range.Targets().Last() == 300, "Range ignored this device's OEM baseline");
                Check(range.Contains(baseline) && !range.Contains(baseline - 5), "Range allowed power below the OEM baseline");
            }
            Check(new PowerRange(115.5m).MinimumWatts == 120, "Fractional baseline rounded downward");
            Check(new PowerRange(300).Targets().SequenceEqual(new[] { 300 }), "Upper-edge baseline range failed");
            Reject(() => new PowerRange(0), "Zero baseline accepted");
            Reject(() => new PowerRange(301), "OEM baseline above the software ceiling accepted");
            foreach (int value in new[] { 0, 3, 177, 305, 1000 }) Check(!PowerProtocol.IsValid(value), "Invalid target accepted: " + value);
            foreach (int value in PowerProtocol.Targets()) Check(PowerProtocol.IsValid(value), "Listed target rejected");
            PowerResult mismatch = PowerProtocol.Parse("Current F7: 200000 (200.000 W)", 6, 175);
            Check(mismatch.Outcome == "Mismatch" && mismatch.RestartRequired && mismatch.Watts == 200, "Mismatch readback");
            Check(!PowerProtocol.Parse("Current F7: 200000 (200.000 W)", 6, 200).Verified, "Nonzero exit accepted without stock evidence");
            Check(PowerProtocol.Parse("Current F7: 200000 (175 W)", 0, 200).Outcome == "ReadbackUnavailable", "Incoherent units accepted");
            Check(PowerProtocol.Parse("no status", 6, 175).RestartRequired, "Missing readback unguarded");
            const string stock = "Set failed: Win32=23\nState : STOCK_BASELINE (9)\nDetail : 0\nLast NTSTATUS : 0x00000000\nLast NVIDIA status : 0x00000000\nFlags init/elig/amt : 1 / 0 / 0\nOEM baseline : 175000 (175.000 W)\nF7 input (+3D14) : 175000 (175.000 W)\namount (+3D18) : 0 (0.000 W)\nUPPER (+3D24) : 175000 (175.000 W)\nMAX effective : 175000 (175.000 W)\nCurrent effective : 175000 (175.000 W)\nCurrent F7 : 175000 (175.000 W)\nApplied target : 175000 (175.000 W)";
            Check(PowerProtocol.Parse(stock, 6, 175).Outcome == "StockBaseline", "Stock baseline regression");
            string timeout = stock.Replace("Last NVIDIA status : 0x00000000", "Last NVIDIA status : 0x00000065");
            PowerResult timedOut = PowerProtocol.Parse(timeout, 6, 175);
            Check(timedOut.Outcome == "NvidiaTimeout", "NVIDIA 0x65 was not identified as a driver-call timeout");
            Check(!timedOut.Verified && timedOut.Watts == 175 && !timedOut.RestartRequired, "Matching timeout readback must remain unconfirmed without demanding a GPU restart");
            Check(timedOut.NvidiaStatus == 0x65 && timedOut.NtStatus == 0, "Driver status codes were lost");
            Check(!PowerProtocol.Parse(timeout, 0, 175).Verified, "NVIDIA timeout was hidden by a zero process exit");
            Check(PowerProtocol.Parse(timeout.Replace("0x00000065", "0x00000066"), 6, 175).Outcome != "NvidiaTimeout", "Different NVIDIA status was classified as 0x65");
            Check(PowerProtocol.Parse(timeout.Replace("Last NTSTATUS : 0x00000000", "Last NTSTATUS : 0xC0000001"), 6, 175).Outcome == "ControllerFailure", "Kernel error was hidden as a transient NVIDIA timeout");
            Check(PowerProtocol.Parse(timeout + "\nLast NVIDIA status : 0x00000065", 6, 175).Outcome != "NvidiaTimeout", "Ambiguous status was accepted as a timeout");
            Check(PowerProtocol.Parse(timeout, 6, 200).Outcome == "Mismatch", "Timeout classification removed mismatch protection");
            Check(!PowerProtocol.Parse(stock.Replace("Win32=23", "Win32=5"), 6, 175).Verified, "Different error accepted");
            Check(!PowerProtocol.Parse(stock.Replace("STOCK_BASELINE (9)", "APPLIED (2)"), 6, 175).Verified, "Wrong state accepted");
            Check(!PowerProtocol.Parse(stock.Replace("Last NTSTATUS : 0x00000000", "Last NTSTATUS : 0xC0000001"), 6, 175).Verified, "Kernel failure accepted");
            foreach (int watts in new[] { 45, 80, 115, 140, 175, 200, 225, 300 })
                Check(PowerProtocol.Parse("Current F7: " + watts * 1000 + " (" + watts + ".000 W)", 0, watts).Verified, "Normal result rejected");
            TestDrivers();
            TestDevicePowerRanges();
            TestSigningSettings();
            Console.WriteLine("PASS: " + assertions + " native C# power protocol assertions; no system mutations.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Reject(Action action, string reason)
    {
        bool rejected = false;
        try { action(); } catch (InvalidOperationException) { rejected = true; } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected, reason);
    }

    private static void TestDrivers()
    {
        var fake = new FakeSystem();
        var engine = new DriverEngine(fake, delegate { });
        engine.Load(DriverBackend.Kdu);
        Check(engine.Session.Ready && !engine.Session.NeedsRestore && fake.Flags == 14, "KDU original flags not restored");
        Check(fake.Commands[fake.Commands.Count - 2] == "kdu -dse 14" && fake.Commands.Last() == "controller status", "Power query must follow DSE recovery");
        Reject(() => engine.Load(DriverBackend.EfiGuard), "Backend switched while active");
        engine.SetPower(200);
        Check(engine.Session.LastReadback == 200, "Native power result not recorded");
        int count = fake.Commands.Count;
        Reject(() => engine.SetPower(305), "Backend accepted more than 300 W");
        Check(count == fake.Commands.Count, "Invalid target dispatched");
        engine.Stop();
        Check(!engine.Session.Ready && !engine.Session.OwnsDriver, "Stop did not clear ownership");

        fake = new FakeSystem();
        var powerLog = new List<string>();
        engine = new DriverEngine(fake, powerLog.Add);
        engine.Load(DriverBackend.Kdu);
        fake.ControllerResponse = new ProcessResult { ExitCode = 6, Output = "Last NTSTATUS : 0x00000000\nLast NVIDIA status : 0x00000065\nCurrent F7 : 175000 (175.000 W)" };
        count = fake.Commands.Count;
        PowerResult timeout = engine.SetPower(175);
        Check(timeout.Outcome == "NvidiaTimeout" && !timeout.Verified && engine.Session.LastReadback == 175, "Engine lost the timeout classification or observed value");
        Check(engine.Session.Ready && !engine.Session.RestartRequired && !engine.Session.NeedsRestore, "Matching timeout unnecessarily reset driver readiness");
        Check(fake.Commands.Count == count + 2 && fake.Commands[count] == "controller status" && fake.Commands.Last() == "controller set 5050 175", "Timeout automatically retried a power write or changed the driver");
        Check(powerLog.Last().Contains("NVIDIA=0x00000065") && powerLog.Last().Contains("elapsed=") && powerLog.Last().Contains("outcome=NvidiaTimeout"), "Timeout diagnostics not preserved in the operation log");
        fake.ControllerResponse = null;
        Check(engine.SetPower(175).Verified, "An explicit later retry could not succeed after a timeout");

        fake = new FakeSystem { Driver = new DriverService { State = "Stopped", Path = @"\??\D:\Workspace\Nvpwr\Nvpwr.sys" } };
        engine = new DriverEngine(fake, delegate { });
        engine.Load(DriverBackend.Kdu);
        Check(!fake.Commands.Any(command => command.StartsWith("sc.exe create ")), "Existing stopped service was created again");
        Check(fake.Commands.First().StartsWith("sc.exe config "), "Old driver path was not updated before DSE");
        Check(fake.Driver.Path == fake.Asset("driver") && engine.Session.Ready, "Stopped legacy service was not reused");

        fake = new FakeSystem { CreateCollision = true };
        engine = new DriverEngine(fake, delegate { });
        engine.Load(DriverBackend.Kdu);
        Check(fake.Commands.Count(command => command.StartsWith("sc.exe create ")) == 1, "Service creation was retried instead of re-queried");
        Check(fake.Commands.Any(command => command.StartsWith("sc.exe config ")) && engine.Session.Ready, "1073 collision was not handled idempotently");
        Check(!fake.Commands.Any(command => command.StartsWith("sc.exe delete ")), "Collision handling deleted a service");

        fake = new FakeSystem { CreateCollision = true, CollisionRunning = true };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.Load(DriverBackend.Kdu), "Running foreign service was overwritten after 1073");
        Check(fake.Commands.Count == 1 && !engine.Session.NeedsRestore, "Foreign running service caused a DSE mutation");

        fake = new FakeSystem { CreateCollision = true, CollisionUnqueryable = true };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.Load(DriverBackend.Kdu), "Unqueryable 1073 service was accepted");
        Check(fake.Commands.Count == 1 && !engine.Session.NeedsRestore, "DSE changed despite unknown service configuration");

        fake = new FakeSystem { Driver = new DriverService { State = "Stopped", Path = @"C:\Other\application.exe", Type = 16 } };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.Load(DriverBackend.Kdu), "Non-driver service was repurposed");
        Check(fake.Commands.Count == 0, "Non-driver service was modified");

        fake = new FakeSystem { Driver = new DriverService { State = "Running", Path = @"C:\Nvpwr test\driver" } };
        engine = new DriverEngine(fake, delegate { });
        engine.Load(DriverBackend.Kdu);
        Check(engine.Session.Ready && !engine.Session.OwnsDriver && fake.Commands.SequenceEqual(new[] { "controller status" }), "Existing running driver was not attached safely");
        engine.Stop();
        Check(fake.Driver.State == "Running" && fake.Commands.SequenceEqual(new[] { "controller status" }), "Externally owned driver was stopped");

        fake = new FakeSystem { FailStart = true };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.Load(DriverBackend.Kdu), "Start failure ignored");
        Check(fake.Flags == 14 && !engine.Session.NeedsRestore, "KDU failure did not restore DSE");
        fake = new FakeSystem { FailRestore = true };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.Load(DriverBackend.Kdu), "Restore failure ignored");
        Check(engine.Session.NeedsRestore && !engine.Session.Ready, "Recovery state lost");

        fake = new FakeSystem();
        engine = new DriverEngine(fake, delegate { });
        engine.Load(DriverBackend.EfiGuard);
        Check(engine.Session.Ready && !engine.Session.NeedsRestore && fake.Flags == 14, "EFI did not restore captured flags");
        Check(fake.Commands.Contains("efifix -c") && fake.Commands.Contains("efifix -e E"), "EFI protocol must check then restore with -e");
        Check(fake.Commands[fake.Commands.Count - 2] == "efifix -r" && fake.Commands.Last() == "controller status", "EFI restoration must be read back before querying power");
        engine.Stop();
        fake = new FakeSystem { EfiAvailable = false };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.Load(DriverBackend.EfiGuard), "Missing EFI hook accepted");
        Check(!engine.Session.NeedsRestore && !fake.Commands.Any(command => command.Contains("start Nvpwr")), "Missing hook mutated driver state");
        fake = new FakeSystem { FailStart = true };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.Load(DriverBackend.EfiGuard), "EFI start failure ignored");
        Check(fake.Flags == 14 && !engine.Session.NeedsRestore, "EFI failure did not restore");
        fake = new FakeSystem { FailRestore = true };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.Load(DriverBackend.EfiGuard), "EFI restore failure ignored");
        Check(engine.Session.NeedsRestore, "EFI recovery flag lost");

        foreach (bool? hvci in new bool?[] { true, null }) {
            fake = new FakeSystem(); fake.Security.HvciRunning = hvci;
            engine = new DriverEngine(fake, delegate { });
            Reject(() => engine.Load(DriverBackend.Kdu), "HVCI guard failed");
            Check(fake.Commands.Count == 0, "HVCI guard ran an executable");
        }
        fake = new FakeSystem(); fake.Security.VbsRunning = true;
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.Load(DriverBackend.EfiGuard), "EFI VBS guard failed");
        Check(fake.Commands.Count == 0, "EFI ran with VBS enabled");
    }

    private static void TestDevicePowerRanges()
    {
        foreach (int baseline in new[] { 45, 80, 115, 140, 175 }) {
            var fake = new FakeSystem { OemWatts = baseline };
            var engine = new DriverEngine(fake, delegate { });
            engine.Load(DriverBackend.Kdu);
            Check(engine.Session.PowerRange != null && engine.Session.PowerRange.MinimumWatts == baseline, "Load did not discover the OEM baseline");
            Check(!fake.Commands.Any(command => command.StartsWith("controller set ")), "Baseline discovery wrote power");
            Check(engine.SetPower(baseline + 5).Verified, "Lower-power GPU target was rejected");
            Check(engine.SetPower(baseline).Verified, "Explicit reduction back to the OEM baseline was rejected");
            int writes = fake.Commands.Count(command => command.StartsWith("controller set "));
            Reject(() => engine.SetPower(baseline - 5), "Power below the OEM baseline was dispatched");
            Check(fake.Commands.Count(command => command.StartsWith("controller set ")) == writes, "Rejected low target still wrote power");
            fake.OemWatts = baseline + 10;
            Reject(() => engine.SetPower(baseline + 5), "Changed OEM mode used a stale lower bound");
            Check(engine.Session.PowerRange.MinimumWatts == baseline + 10, "Changed OEM range was not exposed to the UI");
            Check(fake.Commands.Count(command => command.StartsWith("controller set ")) == writes, "OEM mode change still dispatched a stale target");
            engine.Stop();
            Check(engine.Session.PowerRange == null, "Stopped session retained an actionable power range");
        }
        foreach (string invalid in new[] {
            "OEM baseline : 115000 (115.000 W)",
            FakeSystem.Status(0),
            FakeSystem.Status(115).Replace("115000 (115.000 W)", "115000 (140.000 W)"),
            FakeSystem.Status(115).Replace("STOCK_BASELINE (9)", "MIXED (3)"),
            FakeSystem.Status(115).Replace("0x00000000", "0xC0000001"),
            FakeSystem.Status(115).Replace("Current F7 : 115000", "Current F7 : 140000")
        }) {
            var fake = new FakeSystem { StatusResponse = new ProcessResult { ExitCode = 0, Output = invalid } };
            var engine = new DriverEngine(fake, delegate { });
            engine.Load(DriverBackend.Kdu);
            Check(engine.Session.Ready && engine.Session.PowerRange == null && !string.IsNullOrEmpty(engine.Session.PowerRangeError), "Unknown baseline was treated as 175 W");
            Reject(() => engine.SetPower(200), "Unknown OEM baseline allowed a power write");
            Check(!fake.Commands.Any(command => command.StartsWith("controller set ")), "Unknown baseline dispatched a set command");
        }
        var failedQuery = new FakeSystem { StatusResponse = new ProcessResult { ExitCode = 3, Output = FakeSystem.Status(115) } };
        var failedEngine = new DriverEngine(failedQuery, delegate { });
        failedEngine.Load(DriverBackend.Kdu);
        Check(failedEngine.Session.PowerRange == null, "Nonzero query exit accepted a baseline");
        failedQuery.StatusResponse = null;
        failedEngine.RefreshPowerRange();
        Check(failedEngine.Session.PowerRange != null, "Explicit refresh could not recover a query failure");
        string applied = FakeSystem.Status(175).Replace("STOCK_BASELINE (9)", "APPLIED (2)").Replace("175000 (175.000 W)", "200000 (200.000 W)").Replace("OEM baseline : 200000 (200.000 W)", "OEM baseline : 175000 (175.000 W)");
        Check(PowerProtocol.ReadRange(applied).OemWatts == 175, "Active target was mistaken for the original OEM baseline");
    }

    private static void TestSigningSettings()
    {
        var fake = new FakeSystem();
        fake.Security.BootTestSigning = false;
        fake.Security.TestSigning = false;
        var engine = new DriverEngine(fake, delegate { });
        engine.Session.Ready = true;
        engine.Session.OwnsDriver = true;
        SecurityState changed = engine.SetTestSigning(true);
        Check(changed.BootTestSigning == true && changed.TestSigning == false, "Next-boot change was not verified or altered runtime state");
        Check(fake.Commands.SequenceEqual(new[] { "bcdedit.exe /set {current} testsigning on" }), "Test mode changed settings beyond the current BCD entry");
        Check(engine.Session.Ready && engine.Session.OwnsDriver && !engine.Session.NeedsRestore, "Test-mode settings interrupted the driver session");
        engine.SetTestSigning(true);
        Check(fake.Commands.Count == 1, "An already configured value was rewritten");

        fake = new FakeSystem();
        fake.Security.BootTestSigning = true;
        fake.Security.TestSigning = true;
        fake.Security.SecureBoot = true;
        engine = new DriverEngine(fake, delegate { });
        changed = engine.SetTestSigning(false);
        Check(changed.BootTestSigning == false && changed.TestSigning == true, "Disabling test mode did not preserve current runtime state");
        Check(fake.Commands.Single() == "bcdedit.exe /set {current} testsigning off", "Disable operation is incorrect");

        foreach (bool? secureBoot in new bool?[] { true, null }) {
            fake = new FakeSystem(); fake.Security.SecureBoot = secureBoot;
            engine = new DriverEngine(fake, delegate { });
            Reject(() => engine.SetTestSigning(true), "Test mode enabled with Secure Boot enabled or unknown");
            Check(fake.Commands.Count == 0, "Secure Boot guard executed a command");
        }
        fake = new FakeSystem(); fake.Security.Firmware = "Legacy"; fake.Security.SecureBoot = null;
        engine = new DriverEngine(fake, delegate { });
        Check(engine.SetTestSigning(true).BootTestSigning == true, "Legacy BIOS test-mode setting was blocked");

        fake = new FakeSystem { AdministratorAvailable = false };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.SetTestSigning(true), "Test mode allowed without administrator privileges");
        Check(fake.Commands.Count == 0, "Unprivileged test-mode request executed a command");
        fake = new FakeSystem();
        engine = new DriverEngine(fake, delegate { }); engine.Session.NeedsRestore = true;
        Reject(() => engine.SetTestSigning(false), "Boot change allowed during pending DSE recovery");
        Check(fake.Commands.Count == 0, "Pending recovery guard executed a command");

        fake = new FakeSystem { FailBootWrite = true }; fake.Security.BootTestSigning = false;
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.SetTestSigning(true), "Failed BCD command was reported successful");
        Check(fake.Security.BootTestSigning == false, "Failed BCD command fabricated a new value");
        fake = new FakeSystem { PreserveBootSetting = true }; fake.Security.BootTestSigning = false;
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.SetTestSigning(true), "Mismatched BCD readback was accepted");
        fake = new FakeSystem { PreserveBootSetting = true };
        engine = new DriverEngine(fake, delegate { });
        Reject(() => engine.SetTestSigning(true), "Unknown BCD readback was accepted");
        Check(fake.Commands.Count == 1, "Unverified boot write was automatically retried");
    }

    private sealed class FakeSystem : ISystemServices
    {
        public readonly List<string> Commands = new List<string>();
        public readonly SecurityState Security = new SecurityState { HvciRunning = false, CodeIntegrityEnabled = true, VbsRunning = false, SecureBoot = false, Firmware = "UEFI" };
        public bool FailStart, FailRestore;
        public bool CreateCollision, CollisionRunning, CollisionUnqueryable;
        public bool AdministratorAvailable = true;
        public bool FailBootWrite, PreserveBootSetting;
        public bool EfiAvailable = true;
        public uint Flags = 14;
        public ProcessResult ControllerResponse;
        public ProcessResult StatusResponse;
        public int OemWatts = 175;
        public DriverService Driver;
        public bool Administrator { get { return AdministratorAvailable; } }
        public SecurityState ReadSecurity() { return Security; }
        public GpuInfo ReadGpu() { return new GpuInfo { Name = "NVIDIA GeForce RTX 5090 Laptop GPU", InstanceId = @"PCI\VEN_10DE&TEST" }; }
        public DriverService ReadDriver() { return Driver; }
        public void WaitDriver(string state) { if (Driver == null || Driver.State != state) throw new InvalidOperationException("Unexpected service state"); }
        public string Asset(string key) { return @"C:\Nvpwr test\" + key; }
        public static string Status(int baseline)
        {
            string value = baseline * 1000 + " (" + baseline + ".000 W)";
            return "State : STOCK_BASELINE (9)\nLast NTSTATUS : 0x00000000\nOEM baseline : " + value + "\nUPPER (+3D24) : " + value +
                "\nMAX effective : " + value + "\nCurrent effective : " + value + "\nCurrent F7 : " + value;
        }
        public ProcessResult Run(string executable, string arguments)
        {
            string name = System.IO.Path.GetFileName(executable);
            Commands.Add(name + " " + arguments);
            if (name == "bcdedit.exe") {
                if (FailBootWrite) return new ProcessResult { ExitCode = 5, Output = "Access is denied." };
                if (!PreserveBootSetting) Security.BootTestSigning = arguments.EndsWith(" on", StringComparison.Ordinal);
                return new ProcessResult { ExitCode = 0, Output = "Boot configuration saved." };
            }
            if (name == "kdu") {
                uint requested = uint.Parse(arguments.Split(' ')[1]);
                if (requested != 0 && FailRestore) return new ProcessResult { ExitCode = 0, Output = "restore failure" };
                uint old = Flags; Flags = requested;
                return new ProcessResult { ExitCode = 1, Output = "DSE flags (0xFFFF) value: " + old.ToString("X") + ", new value to be written: " + requested + "\nWrite result verification succeeded" };
            }
            if (name == "efifix") {
                if (arguments == "-c") return new ProcessResult { ExitCode = EfiAvailable ? 0 : -1, Output = EfiAvailable ? "Success." : "hook not installed" };
                if (arguments == "-r") return new ProcessResult { ExitCode = 0, Output = "Success. g_CiOptions value: 0x" + Flags.ToString("X") };
                if (arguments == "-d") { Flags = 0; return new ProcessResult { ExitCode = 0, Output = "Successfully disabled DSE. Original g_CiOptions value: 0xE" }; }
                if (arguments.StartsWith("-e ")) {
                    if (FailRestore) return new ProcessResult { ExitCode = -1, Output = "restore failed" };
                    Flags = uint.Parse(arguments.Substring(3), System.Globalization.NumberStyles.HexNumber);
                    return new ProcessResult { ExitCode = 0, Output = "Successfully (re)enabled DSE." };
                }
            }
            if (name == "sc.exe") {
                if (arguments.StartsWith("create ")) {
                    if (CreateCollision) {
                        if (!CollisionUnqueryable) Driver = new DriverService { State = CollisionRunning ? "Running" : "Stopped", Path = @"C:\Old Nvpwr\Nvpwr.sys" };
                        return new ProcessResult { ExitCode = 1073, Output = "The specified service already exists." };
                    }
                    Driver = new DriverService { State = "Stopped", Path = Asset("driver") };
                }
                if (arguments.StartsWith("config ")) Driver.Path = Asset("driver");
                if (arguments == "start Nvpwr") { if (FailStart) throw new InvalidOperationException("simulated start failure"); Driver.State = "Running"; }
                if (arguments == "stop Nvpwr") Driver.State = "Stopped";
            }
            if (name == "controller") {
                if (arguments == "status") return StatusResponse ?? new ProcessResult { ExitCode = 0, Output = Status(OemWatts) };
                if (ControllerResponse != null) return ControllerResponse;
                int watts = int.Parse(arguments.Split(' ')[2]);
                return new ProcessResult { ExitCode = 0, Output = "Current F7: " + watts * 1000 + " (" + watts + ".000 W)" };
            }
            return new ProcessResult { ExitCode = 0, Output = "OK" };
        }
    }
}