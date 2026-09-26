using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Nvpwr
{
    public sealed class UiController
    {
        public readonly Window Window;
        private readonly BundleAssets assets;
        private readonly WindowsServices system;
        private readonly DriverEngine engine;
        private readonly LogBook logBook = new LogBook();
        private readonly bool preview, diagnostic, manualLoad;
        private bool readOnly, busy, syncing, allowClose, autoAttempted;
        private Mutex writerLock;
        private bool ownsLock;
        private LogSession activeLog;
        private SystemSnapshot snapshot;
        private DriverBackend backend;
        private string readOnlyReason = "ReadOnlyMode";
        private string lastHardwareError;
        private string lastOutcome;
        public bool StartupComplete { get; private set; }
        public string LastError { get; private set; }

        private T Find<T>(string name) where T : class
        {
            T control = Window.FindName(name) as T;
            if (control == null) throw new InvalidDataException("Missing UI control: " + name);
            return control;
        }
        private TextBlock Text(string name) { return Find<TextBlock>(name); }
        private Button Button(string name) { return Find<Button>(name); }
        private string Message(string key) { return Convert.ToString(Window.Resources[key]); }
        private Brush Brush(string key) { return (Brush)Window.Resources[key]; }
        private bool Writable { get { return system.Administrator && !readOnly && !preview && !diagnostic; } }
        private string State(bool? state) { return Message(state.HasValue ? state.Value ? "On" : "Off" : "Unknown"); }

        public UiController(bool preview, bool readOnly, bool diagnostic, bool manualLoad)
        {
            this.preview = preview; this.readOnly = readOnly || preview || diagnostic; this.diagnostic = diagnostic; this.manualLoad = manualLoad;
            assets = new BundleAssets(); system = new WindowsServices(assets);
            using (var stream = new MemoryStream(assets.ReadResource("Nvpwr.MainWindow.xaml"))) Window = (Window)XamlReader.Load(stream);
            using (var stream = new MemoryStream(assets.ReadResource("Nvpwr.AppIcon"))) {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                BitmapFrame icon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
                icon.Freeze(); Window.Icon = icon;
            }
            engine = new DriverEngine(system, AppendLog);
            ReadBackend();
            Find<ComboBox>("WattInput").ItemsSource = PowerProtocol.Targets();
            Find<ComboBox>("WattInput").Text = "175";
            Text("RangeLabel").Text = "175-300 W / 5 W";
            Find<ComboBox>("LogSessions").ItemsSource = logBook.Sessions;
            Find<ComboBox>("LogSessions").DisplayMemberPath = "Label";
            Find<ComboBox>("LogSessions").SelectionChanged += delegate { RenderLog(); };
            Find<RadioButton>("NavPower").Checked += delegate { ShowPage("Power"); };
            Find<RadioButton>("NavSecurity").Checked += delegate { ShowPage("Security"); };
            Find<RadioButton>("NavFiles").Checked += delegate { ShowPage("Files"); };
            Find<RadioButton>("BackendKdu").Checked += delegate { ChangeBackend(DriverBackend.Kdu); };
            Find<RadioButton>("BackendEfi").Checked += delegate { ChangeBackend(DriverBackend.EfiGuard); };
            Button("BackendHelpButton").Click += delegate { ExportDocument(); };
            syncing = true;
            Find<RadioButton>(backend == DriverBackend.Kdu ? "BackendKdu" : "BackendEfi").IsChecked = true;
            syncing = false;
            Find<ComboBox>("WattInput").AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(delegate { ValidateInput(); }));
            Find<CheckBox>("RiskAccepted").Click += delegate { UpdateControls(); };
            Button("RefreshButton").Click += async delegate { await Refresh(); };
            Button("LoadButton").Click += async delegate { if (Writable && Confirm(Message("LoadConfirm"))) await Load(); };
            Button("UnloadButton").Click += async delegate { await Perform("StopTitle", () => engine.Stop(), state => { snapshot.ServiceState = state; SetStatus(Message("DriverStopped")); }); };
            Button("ApplyButton").Click += async delegate { await Apply(); };
            Button("RestartGpuButton").Click += async delegate { await Restart(false); };
            Button("RecoverButton").Click += async delegate { await Perform("RecoveryTitle", () => { engine.Restore(); return true; }, ignored => SetStatus(Message("DseRecovered"))); };
            Button("TestModeButton").Click += delegate { OpenTestModeMenu(); };
            TestModeChoice(true).Click += async delegate { await ChangeTestMode(true); };
            TestModeChoice(false).Click += async delegate { await ChangeTestMode(false); };
            Button("FirmwareButton").Click += delegate { if (!preview) { Alert(Message("FirmwareNotice"), "Nvpwr", MessageBoxImage.Information); Open("ms-settings:recovery"); } };
            Button("MemoryButton").Click += delegate { if (!preview) Open("windowsdefender://coreisolation"); };
            Button("OpenFolderButton").Click += delegate { Open(AppDomain.CurrentDomain.BaseDirectory); };
            Button("DocsButton").Click += delegate { ExportDocument(); };
            Button("ExportEfiButton").Click += delegate { ExportEfi(); };
            Button("AdminButton").Click += delegate { if (!preview && !diagnostic) { try { Program.Elevate(manualLoad); Window.Close(); } catch (Exception error) { Alert(error.Message, Message("AdminFailureTitle")); } } };
            Button("CopyLogButton").Click += delegate { try { Clipboard.SetText(SelectedLog()); SetStatus(Message("LogCopied")); } catch (Exception error) { Alert(error.Message, "Nvpwr"); } };
            Button("SaveLogButton").Click += delegate { SaveLog(); };
            Button("ClearLogButton").Click += delegate { LogSession entry = Find<ComboBox>("LogSessions").SelectedItem as LogSession; if (entry != null) { entry.Text.Clear(); logBook.Append(entry, Message("LogCleared")); RenderLog(); } };
            Window.Closing += Closing;
            Window.Closed += delegate { if (ownsLock) writerLock.ReleaseMutex(); if (writerLock != null) writerLock.Dispose(); };
            Window.ContentRendered += async delegate { if (snapshot == null && !busy) await Refresh(); };
            if (Writable) {
                try {
                    writerLock = new Mutex(false, "Global\\Nvpwr.PowerConsole.Writer");
                    try { ownsLock = writerLock.WaitOne(0); } catch (AbandonedMutexException) { ownsLock = true; }
                    if (!ownsLock) { this.readOnly = true; readOnlyReason = "WriterOwnedElsewhere"; }
                } catch { this.readOnly = true; readOnlyReason = "WriterUnavailable"; }
            }
            FillFiles();
            UpdateControls();
        }

        private void ReadBackend()
        {
            backend = DriverBackend.Kdu;
            if (preview || diagnostic) return;
            string path = AppPaths.Beside("backend.txt");
            DriverBackend value;
            try { if (File.Exists(path) && Enum.TryParse(File.ReadAllText(path).Trim(), out value) && Enum.IsDefined(typeof(DriverBackend), value)) backend = value; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private string BackendSwitchBlocker()
        {
            if (busy) return "BackendSwitchBusy";
            if (engine.Session.NeedsRestore) return "BackendSwitchRecovery";
            if (engine.Session.Ready || engine.Session.OwnsDriver) return "BackendSwitchActive";
            return null;
        }

        private void ChangeBackend(DriverBackend value)
        {
            if (syncing) return;
            if (BackendSwitchBlocker() != null) {
                syncing = true; Find<RadioButton>(backend == DriverBackend.Kdu ? "BackendKdu" : "BackendEfi").IsChecked = true; syncing = false; return;
            }
            backend = value;
            if (Writable) {
                try {
                    Directory.CreateDirectory(AppPaths.Root);
                    File.WriteAllText(AppPaths.Beside("backend.txt"), value.ToString());
                } catch (Exception error) { Alert(error.Message, "Nvpwr"); }
            }
            autoAttempted = true;
            FillFiles(); UpdateControls();
        }

        private void ShowPage(string page)
        {
            foreach (string name in new[] { "Power", "Security", "Files" }) Find<FrameworkElement>(name + "Page").Visibility = name == page ? Visibility.Visible : Visibility.Collapsed;
            Text("PageTitle").Text = "NVPWR / " + Message(page + "Title");
        }

        private void BeginLog(string title)
        {
            activeLog = logBook.Begin(title + " / " + backend);
            Find<ComboBox>("LogSessions").SelectedItem = activeLog;
            RenderLog();
        }
        private void AppendLog(string message)
        {
            LogSession target = activeLog;
            if (!Window.Dispatcher.CheckAccess()) { Window.Dispatcher.BeginInvoke(new Action(() => AppendTo(target, message))); return; }
            AppendTo(target, message);
        }
        private void AppendTo(LogSession entry, string message) { logBook.Append(entry, message); if (Find<ComboBox>("LogSessions").SelectedItem == entry) RenderLog(); }
        private string SelectedLog() { LogSession entry = Find<ComboBox>("LogSessions").SelectedItem as LogSession; return entry == null ? "" : entry.Text.ToString(); }
        private void RenderLog() { TextBox box = Find<TextBox>("LogBox"); box.Text = SelectedLog(); box.ScrollToEnd(); }
        private void FinishLog(bool success) { activeLog.Complete(success); try { logBook.Save(activeLog); } catch (IOException error) { AppendLog(error.Message); } catch (UnauthorizedAccessException error) { AppendLog(error.Message); } }

        private async Task<bool> Perform<T>(string title, Func<T> action, Action<T> completed, bool inspection = false)
        {
            if (busy || (!inspection && !Writable)) return false;
            BeginLog(Message(title)); busy = true; LastError = null; UpdateControls();
            try {
                T result = await Task.Run(action);
                busy = false;
                completed(result);
                UpdateControls();
                FinishLog(true);
                return true;
            } catch (Exception error) {
                busy = false; LastError = error.Message; AppendLog(error.ToString()); UpdateControls(); FinishLog(false);
                string heading = engine.Session.NeedsRestore ? Message("DseFailureTitle") : Message(title) + Message("FailedSuffix");
                Alert(error.Message, heading);
                return false;
            } finally { busy = false; UpdateControls(); }
        }

        private async Task Refresh()
        {
            bool succeeded = await Perform("RefreshTitle", () => preview ? PreviewSnapshot() : system.Snapshot(), ApplySnapshot, true);
            StartupComplete = true;
            if (succeeded && !autoAttempted && !manualLoad && Writable) {
                autoAttempted = true;
                if (CanLoad()) await Load();
            }
        }

        private SystemSnapshot PreviewSnapshot()
        {
            return new SystemSnapshot {
                Gpu = new GpuInfo { Name = "NVIDIA GeForce RTX 5090 Laptop GPU", DriverVersion = "32.0.16.1692" }, ServiceState = "Stopped",
                Security = new SecurityState { TestSigning = false, BootTestSigning = false, SecureBoot = false, Firmware = "UEFI", HvciRunning = false, HvciConfigured = false, VbsRunning = false, CodeIntegrityEnabled = true, BlocklistConfigured = false }
            };
        }

        private void ApplySnapshot(SystemSnapshot value)
        {
            snapshot = value;
            Text("GpuName").Text = snapshot.Gpu == null ? Message("GpuUnavailable") : snapshot.Gpu.Name;
            Text("VersionLabel").Text = "Nvpwr " + BundleAssets.Version + (snapshot.Gpu == null ? "" : " / NVIDIA " + snapshot.Gpu.DriverVersion);
            SecurityState security = snapshot.Security;
            SetState("SigningSummary", security.TestSigning, false); SetState("SecureSummary", security.SecureBoot, true); SetState("HvciSummary", security.HvciRunning, true);
            Text("SigningRuntime").Text = Message("CurrentState") + State(security.TestSigning);
            Text("SigningBoot").Text = Message("BootState") + (security.BootTestSigning.HasValue ? State(security.BootTestSigning) : Message("NotExplicit"));
            bool signingDiffers = security.BootTestSigning.HasValue && security.TestSigning.HasValue && security.BootTestSigning != security.TestSigning;
            Text("SigningPending").Text = (signingDiffers ? Message("Pending") + "\n" : "") + Message("SigningDescription");
            Text("SecureRuntime").Text = security.Firmware == "Legacy" ? Message("NotSupported") : State(security.SecureBoot);
            Text("FirmwareLabel").Text = "Firmware: " + security.Firmware;
            Text("HvciRuntime").Text = Message("CurrentState") + State(security.HvciRunning);
            Text("HvciConfigured").Text = Message("ConfiguredState") + State(security.HvciConfigured);
            Text("HvciPending").Text = security.HvciConfigured.HasValue && security.HvciRunning.HasValue && security.HvciConfigured != security.HvciRunning ? Message("Pending") : Message("RuntimeOnly");
            Text("CiLabel").Text = "Kernel CI: " + State(security.CodeIntegrityEnabled);
            Text("BlocklistLabel").Text = "Driver blocklist: " + State(security.BlocklistConfigured);
            Text("DriverState").Text = Message(snapshot.ServiceState == "Running" ? "Running" : snapshot.ServiceState == "Stopped" ? "Stopped" : snapshot.ServiceState == "NotInstalled" ? "NotLoaded" : "Unknown");
            AppendLog(Message("Detected"));
            AppendLog("SCM Nvpwr: " + snapshot.ServiceState + (string.IsNullOrEmpty(snapshot.ServicePath) ? "" : "\nImagePath: " + snapshot.ServicePath));
            foreach (string error in security.Errors) AppendLog(error);
            if (snapshot.Gpu == null && snapshot.GpuError != lastHardwareError) { lastHardwareError = snapshot.GpuError; Alert(snapshot.GpuError, Message("HardwareAlertTitle")); }
            SetStatus(Message("Detected")); FillFiles();
        }

        private void SetState(string name, bool? value, bool protective)
        {
            Text(name).Text = State(value); Text(name).Foreground = Brush(!value.HasValue ? "Muted" : value.Value == protective ? "Good" : "Warning");
        }
        private bool CanLoad()
        {
            return snapshot != null && snapshot.Gpu != null && snapshot.Security.HvciRunning == false && snapshot.Security.CodeIntegrityEnabled == true &&
                (backend != DriverBackend.EfiGuard || (snapshot.Security.Firmware == "UEFI" && snapshot.Security.VbsRunning == false));
        }

        private void UpdateControls()
        {
            bool writable = Writable && !busy;
            bool knownGpu = snapshot != null && snapshot.Gpu != null;
            bool acknowledged = Find<CheckBox>("RiskAccepted").IsChecked == true;
            DriverSession session = engine.Session;
            Button("LoadButton").IsEnabled = writable && CanLoad() && !session.Ready && !session.NeedsRestore && !session.RestartRequired;
            Button("UnloadButton").IsEnabled = writable && (session.Ready || session.OwnsDriver || session.NeedsRestore);
            Button("ApplyButton").IsEnabled = writable && session.Ready && acknowledged && !session.NeedsRestore && !session.RestartRequired;
            Button("RestartGpuButton").IsEnabled = writable && knownGpu;
            Button("RecoverButton").IsEnabled = writable;
            Button("RecoverButton").Visibility = session.NeedsRestore ? Visibility.Visible : Visibility.Collapsed;
            Button("RefreshButton").IsEnabled = !busy;
            Find<ComboBox>("WattInput").IsEnabled = !busy && knownGpu;
            Find<CheckBox>("RiskAccepted").IsEnabled = !busy && knownGpu;
            string switchBlocker = BackendSwitchBlocker();
            bool switchable = switchBlocker == null;
            Find<RadioButton>("BackendKdu").IsEnabled = switchable;
            Find<RadioButton>("BackendEfi").IsEnabled = switchable;
            Find<RadioButton>("BackendKdu").ToolTip = Message(switchBlocker ?? "KduNotice");
            Find<RadioButton>("BackendEfi").ToolTip = Message(switchBlocker ?? "EfiNotice");
            Text("BackendLabel").Text = backend == DriverBackend.Kdu ? "KDU" : "EFI";
            Text("BackendNotice").Text = Message(switchBlocker ?? (backend == DriverBackend.Kdu ? "KduNotice" : "EfiNotice"));
            Button("AdminButton").Visibility = !system.Administrator && !preview && !diagnostic ? Visibility.Visible : Visibility.Collapsed;
            Button("AdminButton").IsEnabled = !busy;
            Button("FirmwareButton").IsEnabled = !busy && !preview && !diagnostic;
            Button("MemoryButton").IsEnabled = !busy && !preview && !diagnostic;
            Button("TestModeButton").IsEnabled = !busy && !preview && !diagnostic && snapshot != null;
            UpdateTestModeMenu();
            Button("ExportEfiButton").IsEnabled = !busy && !diagnostic;
            Find<ProgressBar>("BusyIndicator").Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Text("AccessLabel").Text = Message(preview ? "PreviewMode" : Writable ? "AdminMode" : "ReadOnlyMode");
            string reason = preview ? "PreviewNotice" : readOnly ? readOnlyReason : !system.Administrator ? "NeedAdministrator" : busy ? "Busy" :
                session.NeedsRestore ? "RecoveryRequired" : snapshot == null ? "Detecting" : !knownGpu ? "GpuUnavailable" :
                session.RestartRequired ? "GpuRestartRequired" : session.Ready && !acknowledged ? "RiskNotAccepted" : session.Ready ? "SessionAvailable" :
                snapshot.Security.HvciRunning != false ? "HvciActive" : snapshot.Security.CodeIntegrityEnabled != true ? "CiUnavailable" :
                backend == DriverBackend.EfiGuard && (snapshot.Security.Firmware != "UEFI" || snapshot.Security.VbsRunning != false) ? "EfiBlocked" : "DriverAvailable";
            Text("ModeText").Text = Message(reason); Find<Border>("ModeBanner").Visibility = Visibility.Visible;
            string tone = reason == "RecoveryRequired" || reason == "CiUnavailable" || reason == "GpuUnavailable" ? "Danger" : reason == "HvciActive" || reason == "RiskNotAccepted" || reason == "EfiBlocked" ? "Warning" : "Good";
            Text("ModeText").Foreground = Brush(tone);
            Find<Border>("ModeBanner").Background = Brush(tone == "Good" ? "NoticeSurface" : tone + "Surface");
            Button("LoadButton").ToolTip = Message(reason); Button("ApplyButton").ToolTip = Message(reason);
            Text("ReadbackValue").Text = session.LastReadback.HasValue ? session.LastReadback.Value.ToString("0.###", CultureInfo.InvariantCulture) + " W" : "-- W";
            Text("ReadbackDetail").Text = !session.LastReadback.HasValue ? Message("ReadbackEmpty") : lastOutcome == "StockBaseline" ? Message("OemBaseline") : "Current F7 / NvpwrCtl";
        }

        private int? Target()
        {
            int watts;
            return int.TryParse(Find<ComboBox>("WattInput").Text, out watts) && PowerProtocol.IsValid(watts) ? (int?)watts : null;
        }
        private void ValidateInput()
        {
            if (syncing) return;
            Text("InputError").Text = Message("InvalidWattsFixed"); Text("InputError").Visibility = Target().HasValue ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task Load()
        {
            await Perform("LoadTitle", () => { engine.Load(backend); return true; }, ignored => {
                snapshot.ServiceState = "Running"; Text("DriverState").Text = Message("Running"); SetStatus(Message(engine.Session.OwnsDriver ? "DriverReady" : "DriverAttached"), "Good");
            });
        }

        private async Task Apply()
        {
            if (!Button("ApplyButton").IsEnabled || !Writable) return;
            int? target = Target();
            if (!target.HasValue) { Alert(Message("InvalidWattsFixed"), Message("InvalidPowerTitle"), MessageBoxImage.Warning); return; }
            PowerResult actual = null;
            await Perform("ApplyTitle", () => engine.SetPower(target.Value), result => {
                actual = result; lastOutcome = result.Outcome; UpdateControls();
                if (result.Verified) { SetStatus(string.Format(Message(result.Outcome == "StockBaseline" ? "StockBaseline" : "Applied"), result.Watts), "Good"); AppendLog(Text("StatusText").Text); }
                else if (result.Outcome == "Mismatch") { SetStatus(string.Format(Message("Mismatch"), result.Watts), "Warning"); }
                else throw new InvalidOperationException(PowerFailureMessage(result));
            });
            if (actual != null && actual.Outcome == "Mismatch") {
                AppendLog(string.Format(Message("Mismatch"), actual.Watts)); FinishLog(false);
                if (Confirm(string.Format(Message("MismatchConfirm"), actual.Watts, actual.Target))) await Restart(true);
            }
        }

        private string PowerFailureMessage(PowerResult result)
        {
            if (result.Outcome == "NvidiaTimeout")
                return string.Format(CultureInfo.InvariantCulture, Message("NvidiaTimeout"), result.Target, result.Watts, result.ExitCode, result.DurationMilliseconds);
            return Message(result.Outcome == "ReadbackUnavailable" ? "ReadbackFailure" : "PowerFailure") + "\nExit: " + result.ExitCode +
                (result.NvidiaStatus.HasValue ? "\nNVIDIA: 0x" + result.NvidiaStatus.Value.ToString("X8") : "") +
                (result.NtStatus.HasValue ? "\nNTSTATUS: 0x" + result.NtStatus.Value.ToString("X8") : "");
        }

        private async Task Restart(bool confirmed)
        {
            if (!Writable || busy || (!confirmed && !Confirm(Message("RestartConfirm")))) return;
            await Perform("RestartTitle", () => engine.RestartGpu(), reboot => {
                snapshot.ServiceState = "Stopped"; Text("DriverState").Text = Message("Stopped");
                SetStatus(Message(reboot ? "RebootRequired" : "Restarted"), reboot ? "Warning" : "Muted");
                if (reboot) Alert(Message("RebootRequired"), "Nvpwr", MessageBoxImage.Warning);
            });
        }

        private MenuItem TestModeChoice(bool enabled)
        {
            return (MenuItem)Button("TestModeButton").ContextMenu.Items[enabled ? 0 : 1];
        }

        private void UpdateTestModeMenu()
        {
            bool permitted = Writable && !busy && snapshot != null && !engine.Session.NeedsRestore;
            bool secureBootAllows = snapshot != null && (snapshot.Security.Firmware == "Legacy" || snapshot.Security.SecureBoot == false);
            TestModeChoice(true).IsEnabled = permitted && secureBootAllows;
            TestModeChoice(false).IsEnabled = permitted;
            bool? configured = snapshot == null ? (bool?)null : snapshot.Security.BootTestSigning;
            TestModeChoice(true).IsChecked = configured == true;
            TestModeChoice(false).IsChecked = configured == false;
            string reason = !Writable ? (system.Administrator ? "ReadOnlyMode" : "NeedAdministrator") : busy ? "Busy" :
                engine.Session.NeedsRestore ? "RecoveryRequired" : snapshot == null ? "Detecting" : "TestModeRestartHint";
            TestModeChoice(false).ToolTip = Message(reason);
            TestModeChoice(true).ToolTip = Message(permitted && !secureBootAllows ? "TestModeSecureBoot" : reason);
        }

        private void OpenTestModeMenu()
        {
            if (busy || preview || diagnostic || snapshot == null) return;
            UpdateTestModeMenu();
            ContextMenu menu = Button("TestModeButton").ContextMenu;
            menu.PlacementTarget = Button("TestModeButton");
            menu.IsOpen = true;
        }

        private async Task ChangeTestMode(bool enabled)
        {
            if (!Writable || busy || snapshot == null || engine.Session.NeedsRestore) return;
            UpdateTestModeMenu();
            if (!TestModeChoice(enabled).IsEnabled) return;
            string action = Message(enabled ? "TestModeEnable" : "TestModeDisable");
            if (!Confirm(string.Format(Message("TestModeConfirm"), action))) return;
            await Perform("TestModeTitle", () => engine.SetTestSigning(enabled), security => {
                snapshot.Security = security;
                ApplySnapshot(snapshot);
                string notice = Message(security.TestSigning == enabled ? "TestModeActive" : "TestModePending");
                SetStatus(notice, security.TestSigning == enabled ? "Good" : "Warning");
                AppendLog(notice);
            });
        }

        private void FillFiles()
        {
            string[] keys = backend == DriverBackend.Kdu ? new[] { "driver", "controller", "kdu", "database" } : new[] { "driver", "controller", "efifix", "efiboot", "efidriver" };
            Find<ItemsControl>("FileList").ItemsSource = BundleAssets.Files.Where(file => keys.Contains(file.Key)).Select(file => new {
                Name = Path.GetFileName(file.RelativePath), RelativePath = Message("EmbeddedAsset"), RoleLabel = file.Key,
                StateLabel = assets.Exists(file.Key) ? "OK" : "MISSING", StateColor = assets.Exists(file.Key) ? "#315FBF" : "#BA3939"
            }).ToArray();
            Text("FileSummary").Text = keys.Count(key => assets.Exists(key)).ToString() + " / " + keys.Length;
        }

        private void SetStatus(string value, string tone = "Muted") { Text("StatusText").Text = value; Text("StatusText").Foreground = Brush(tone); }
        private bool Confirm(string message) { return !diagnostic && MessageBox.Show(Window, message, "Nvpwr", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes; }
        private void Alert(string message, string title, MessageBoxImage kind = MessageBoxImage.Error)
        {
            SetStatus(title, kind == MessageBoxImage.Error ? "Danger" : "Warning"); AppendLog(title + "\n" + message);
            if (!diagnostic) MessageBox.Show(Window, message, title, MessageBoxButton.OK, kind);
        }
        private void Open(string target)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception error) { Alert(error.Message, Message("SystemEntryFailureTitle")); }
        }

        private void SaveLog()
        {
            var dialog = new SaveFileDialog { Filter = "Text log|*.txt", FileName = "nvpwr-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt" };
            if (dialog.ShowDialog(Window) == true) { try { File.WriteAllText(dialog.FileName, SelectedLog(), Encoding.UTF8); } catch (Exception error) { Alert(error.Message, "Nvpwr"); } }
        }
        private void ExportDocument(bool open = true)
        {
            try {
                string path = AppPaths.Beside("README.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, assets.ReadResource("Nvpwr.Readme"));
                if (open) Open(path);
            } catch (Exception error) { Alert(error.Message, "Nvpwr"); }
        }
        private void ExportEfi()
        {
            if (diagnostic) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog()) {
                dialog.Description = Message("ExportEfiNotice");
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                try { assets.ExportEfi(dialog.SelectedPath); Alert(Message("ExportEfiComplete"), "EfiGuard", MessageBoxImage.Information); }
                catch (Exception error) { Alert(error.Message, "EfiGuard"); }
            }
        }

        private async void Closing(object sender, CancelEventArgs args)
        {
            if (allowClose) return;
            if (busy) { args.Cancel = true; Alert(Message("BusyClose"), Message("BusyTitle"), MessageBoxImage.Information); return; }
            if (engine.Session.OwnsDriver || engine.Session.NeedsRestore) {
                args.Cancel = true;
                bool stopped = await Perform("StopTitle", () => engine.Stop(), ignored => { });
                if (stopped || Confirm(Message("CloseFailed"))) { allowClose = true; Window.Close(); }
            }
        }

        public void RunUiChecks(string directory)
        {
            Directory.CreateDirectory(directory);
            Window.WindowStartupLocation = WindowStartupLocation.Manual; Window.Left = -20000; Window.Top = -20000; Window.ShowInTaskbar = false; Window.ShowActivated = false;
            ApplySnapshot(PreviewSnapshot()); StartupComplete = true;
            Window.Show();
            engine.Session.Ready = true;
            UpdateControls();
            if (Find<RadioButton>("BackendEfi").IsEnabled || Text("BackendNotice").Text != Message("BackendSwitchActive") ||
                Convert.ToString(Find<RadioButton>("BackendEfi").ToolTip) != Message("BackendSwitchActive") ||
                !ToolTipService.GetShowOnDisabled(Find<RadioButton>("BackendEfi")))
                throw new InvalidOperationException("An active driver session does not explain why backend switching is locked.");
            if (!Button("BackendHelpButton").IsEnabled) throw new InvalidOperationException("Backend guidance must remain accessible during an active session.");
            ChangeBackend(DriverBackend.EfiGuard);
            if (backend != DriverBackend.Kdu) throw new InvalidOperationException("Backend switched during an active driver session.");
            engine.Session.Ready = false; engine.Session.OwnsDriver = true;
            UpdateControls();
            if (BackendSwitchBlocker() != "BackendSwitchActive") throw new InvalidOperationException("Owned driver cleanup was not protected.");
            engine.Session.OwnsDriver = false; engine.Session.NeedsRestore = true;
            UpdateControls();
            if (Text("BackendNotice").Text != Message("BackendSwitchRecovery")) throw new InvalidOperationException("Pending DSE recovery lost its backend warning.");
            engine.Session.NeedsRestore = false; busy = true;
            UpdateControls();
            if (Text("BackendNotice").Text != Message("BackendSwitchBusy")) throw new InvalidOperationException("Busy backend warning is missing.");
            if (!Button("BackendHelpButton").IsEnabled) throw new InvalidOperationException("Backend guidance was disabled with the backend selector.");
            busy = false;
            UpdateControls();
            if (!Find<RadioButton>("BackendEfi").IsEnabled || BackendSwitchBlocker() != null) throw new InvalidOperationException("Backend selection did not unlock after ending the session.");
            string backendGuide = Encoding.UTF8.GetString(assets.ReadResource("Nvpwr.Readme"));
            foreach (string topic in new[] { "加载方式选用指南", "结束会话", "FAT32", "EFI/Boot/bootx64.efi", "EFI/Boot/EfiGuardDxe.efi", "Windows Boot Manager", "BitLocker", "VBS", "0x65" })
                if (!backendGuide.Contains(topic)) throw new InvalidOperationException("Embedded backend guidance is missing: " + topic);
            ExportDocument(false);
            if (File.ReadAllText(AppPaths.Beside("README.txt")) != backendGuide.TrimStart('\uFEFF'))
                throw new InvalidOperationException("The embedded backend guide did not export beside the application data.");
            if (Window.FindName("TestSigningToggle") != null) throw new InvalidOperationException("An inline test-signing toggle must not replace the settings entry.");
            if (Button("TestModeButton").Content.ToString() != "测试模式设置" || Button("TestModeButton").IsEnabled || TestModeChoice(true).IsEnabled || TestModeChoice(false).IsEnabled)
                throw new InvalidOperationException("The test-mode entry is missing or exposes writes during diagnostics.");
            if (!Equals(TestModeChoice(true).Tag, "on") || !Equals(TestModeChoice(false).Tag, "off"))
                throw new InvalidOperationException("Test-mode menu choices are not mapped to explicit actions.");
            Button("TestModeButton").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            if (Button("TestModeButton").ContextMenu.IsOpen) throw new InvalidOperationException("Diagnostics opened a system-setting menu.");
            if (!Message("TestModeConfirm").Contains("BitLocker") || !Message("TestModeConfirm").Contains("TESTSIGNING"))
                throw new InvalidOperationException("Test-mode confirmation is missing security information.");
            if (Text("SigningRuntime").Text != Message("CurrentState") + Message("Off") || Text("SigningBoot").Text != Message("BootState") + Message("Off"))
                throw new InvalidOperationException("Read-only test-signing states are missing.");
            if (Text("SigningPending").Text != Message("SigningDescription")) throw new InvalidOperationException("Test-signing explanation is missing.");
            snapshot.Security.TestSigning = true; snapshot.Security.BootTestSigning = true;
            ApplySnapshot(snapshot);
            if (Text("SigningRuntime").Text != Message("CurrentState") + Message("On") || Text("SigningBoot").Text != Message("BootState") + Message("On"))
                throw new InvalidOperationException("Enabled test-signing states were not displayed.");
            snapshot.Security.BootTestSigning = false;
            ApplySnapshot(snapshot);
            if (!Text("SigningPending").Text.Contains(Message("Pending")) || !Text("SigningPending").Text.Contains(Message("SigningDescription")))
                throw new InvalidOperationException("Pending boot state lost its read-only explanation.");
            snapshot.Security.TestSigning = null; snapshot.Security.BootTestSigning = null;
            ApplySnapshot(snapshot);
            if (Text("SigningRuntime").Text != Message("CurrentState") + Message("Unknown") || Text("SigningBoot").Text != Message("BootState") + Message("NotExplicit"))
                throw new InvalidOperationException("Unavailable test-signing state was reported as off.");
            ApplySnapshot(PreviewSnapshot());
            if (Window.Icon == null || Window.Icon.Width < 256) throw new InvalidOperationException("High-resolution window icon was not loaded.");
            using (var stream = new MemoryStream(assets.ReadResource("Nvpwr.AppIcon"))) {
                var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                int[] required = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
                if (required.Any(size => !decoder.Frames.Any(frame => frame.PixelWidth == size && frame.PixelHeight == size)))
                    throw new InvalidOperationException("Embedded icon is missing a required DPI size.");
            }
            foreach (AssetInfo file in BundleAssets.Files.Where(item => item.Key != "efiboot" && item.Key != "efidriver")) {
                string extracted = assets.Ensure(file.Key);
                if (!string.Equals(Path.GetDirectoryName(extracted), AppPaths.Root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Runtime extraction escaped NvpwrData.");
            }
            if (Window.ResizeMode != ResizeMode.CanMinimize || Button("ApplyButton").IsEnabled || Button("LoadButton").IsEnabled) throw new InvalidOperationException("Diagnostic write guard failed.");
            string timeoutMessage = PowerFailureMessage(new PowerResult { Target = 175, Watts = 175, ExitCode = 6, NvidiaStatus = 0x65, NtStatus = 0, DurationMilliseconds = 2000, Outcome = "NvidiaTimeout" });
            if (!timeoutMessage.Contains("NV_ERR_TIMEOUT") || !timeoutMessage.Contains("175 W") || !timeoutMessage.Contains("2000 ms"))
                throw new InvalidOperationException("Specific NVIDIA timeout diagnostics are missing.");
            Find<ComboBox>("WattInput").Text = "305"; if (Target().HasValue) throw new InvalidOperationException("Input range exceeds 300 W.");
            Find<ComboBox>("WattInput").Text = "200"; if (Target() != 200) throw new InvalidOperationException("Power input failed.");
            BeginLog("First operation"); AppendLog("first-only"); FinishLog(true);
            BeginLog("Second operation"); AppendLog("second-only"); FinishLog(true);
            if (SelectedLog().Contains("first-only")) throw new InvalidOperationException("Log groups are mixed.");
            foreach (DriverBackend mode in new[] { DriverBackend.Kdu, DriverBackend.EfiGuard }) {
                backend = mode; Find<RadioButton>(mode == DriverBackend.Kdu ? "BackendKdu" : "BackendEfi").IsChecked = true; FillFiles(); UpdateControls();
                foreach (string page in new[] { "Power", "Security", "Files" }) {
                    Find<RadioButton>("Nav" + page).IsChecked = true; Window.UpdateLayout();
                    FrameworkElement host = Find<FrameworkElement>("PageHost"); FrameworkElement view = Find<FrameworkElement>(page + "Page");
                    view.Measure(new Size(host.ActualWidth, double.PositiveInfinity));
                    if (view.DesiredSize.Height > host.ActualHeight + 1) throw new InvalidOperationException(page + " page overflows: " + view.DesiredSize.Height + " / " + host.ActualHeight);
                    Window.UpdateLayout();
                    if (page == "Security" &&
                        (Math.Abs(Button("TestModeButton").ActualWidth - Button("FirmwareButton").ActualWidth) > 1 ||
                         Math.Abs(Button("TestModeButton").ActualWidth - Button("MemoryButton").ActualWidth) > 1))
                        throw new InvalidOperationException("The three security settings entries are not aligned consistently.");
                    if (Find<TextBox>("LogBox").ActualHeight < 320) throw new InvalidOperationException("Log pane is too short.");
                    FrameworkElement visual = Find<FrameworkElement>("RootLayout");
                    var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (FileStream output = File.Create(Path.Combine(directory, mode + "-" + page + ".png"))) encoder.Save(output);
                }
            }
            allowClose = true; Window.Close();
        }
    }
}