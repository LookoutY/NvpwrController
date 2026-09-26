using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Nvpwr
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] arguments)
        {
            string mode = arguments.Length == 0 ? "" : arguments[0].ToLowerInvariant();
            bool diagnostic = mode == "--check" || mode == "--self-test" || mode == "--live-self-test";
            try
            {
                if (arguments.Length > 1 || Array.IndexOf(new[] { "", "--preview", "--read-only", "--manual-load", "--check", "--self-test", "--live-self-test" }, mode) < 0)
                    throw new ArgumentException("Use --preview, --read-only, --manual-load, --check, --self-test or --live-self-test.");
                var assets = new BundleAssets();
                assets.VerifyBundle();
                if (mode == "--check") { Report("PASS: native bundle has seven embedded runtime/EFI components; no script dependency."); return 0; }
                if (mode == "--self-test") {
                    if (NativeTests.Run() != 0) throw new InvalidOperationException("Native engine regression tests failed.");
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    var window = new UiController(true, true, true, true);
                    string screenshots = AppPaths.Beside("diagnostics", "ui");
                    window.RunUiChecks(screenshots);
                    app.Shutdown();
                    Report("PASS: native protocol/backends, grouped logs, 175-300 W input, fixed pages and KDU/EFI UI renders. " + screenshots);
                    return 0;
                }
                bool readOnly = mode == "--read-only" || mode == "--live-self-test";
                bool preview = mode == "--preview";
                bool administrator;
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
                if (!administrator && !readOnly && !preview) {
                    try { Elevate(mode == "--manual-load"); return 0; }
                    catch (System.ComponentModel.Win32Exception error) { if (error.NativeErrorCode != 1223) throw; readOnly = true; }
                }
                var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var controller = new UiController(preview, readOnly, diagnostic, mode == "--manual-load");
                application.MainWindow = controller.Window;
                DispatcherTimer timer = null;
                if (mode == "--live-self-test") {
                    controller.Window.WindowStartupLocation = WindowStartupLocation.Manual;
                    controller.Window.Left = -20000; controller.Window.Top = -20000;
                    controller.Window.ShowInTaskbar = false; controller.Window.ShowActivated = false;
                    DateTime deadline = DateTime.UtcNow.AddSeconds(30);
                    timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    timer.Tick += delegate {
                        if (controller.StartupComplete || DateTime.UtcNow > deadline) { timer.Stop(); controller.Window.Close(); }
                    };
                    timer.Start();
                }
                application.Run(controller.Window);
                if (timer != null) timer.Stop();
                if (mode == "--live-self-test") {
                    if (!controller.StartupComplete || controller.LastError != null) throw new InvalidOperationException("Live startup failed: " + controller.LastError);
                    Report("PASS: native WPF live read-only startup completed. No PowerShell or driver load was used.");
                }
                return 0;
            }
            catch (Exception error)
            {
                string path = Report(error.ToString());
                if (!diagnostic) MessageBox.Show(error.Message + "\n\n" + path, "Nvpwr", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        public static void Elevate(bool manualLoad)
        {
            var info = new ProcessStartInfo(System.Reflection.Assembly.GetExecutingAssembly().Location, manualLoad ? "--manual-load" : "") { UseShellExecute = true, Verb = "runas" };
            Process.Start(info);
        }

        internal static string Report(string text)
        {
            string path = AppPaths.Beside("startup.log");
            try {
                Directory.CreateDirectory(AppPaths.Root);
                File.AppendAllText(path, DateTime.Now.ToString("s") + " / native " + BundleAssets.Version + "\n" + text + "\n\n", Encoding.UTF8);
                return path;
            }
            catch (IOException error) { return "Cannot write log beside executable: " + path + "\n" + error.Message; }
            catch (UnauthorizedAccessException error) { return "Program directory is not writable: " + AppPaths.Root + "\n" + error.Message; }
        }
    }
}