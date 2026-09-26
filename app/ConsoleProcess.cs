using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Nvpwr
{
    internal static class ConsoleProcess
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Coordinate { public short X, Y; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved, Desktop, Title;
            public int X, Y, Width, Height, XChars, YChars, FillAttribute, Flags;
            public short ShowWindow, ReservedSize;
            public IntPtr ReservedPointer, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo { public IntPtr Process, Thread; public int ProcessId, ThreadId; }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out IntPtr read, out IntPtr write, IntPtr attributes, int size);
        [DllImport("kernel32.dll")]
        private static extern int CreatePseudoConsole(Coordinate size, IntPtr input, IntPtr output, uint flags, out IntPtr console);
        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr console);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessW(string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        public static ProcessResult Run(string executable, string arguments)
        {
            IntPtr inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero, outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero, console = IntPtr.Zero, attributes = IntPtr.Zero;
            bool attributesReady = false;
            ProcessInfo process = new ProcessInfo();
            StreamReader reader = null;
            try
            {
                if (!CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0) || !CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                int status = CreatePseudoConsole(new Coordinate { X = 180, Y = 60 }, inputRead, outputWrite, 0, out console);
                if (status != 0) Marshal.ThrowExceptionForHR(status);
                Close(inputRead); inputRead = IntPtr.Zero;
                Close(outputWrite); outputWrite = IntPtr.Zero;
                reader = new StreamReader(new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read), Encoding.UTF8);
                outputRead = IntPtr.Zero;
                StreamReader activeReader = reader;
                Task<string> captured = Task.Run(() => activeReader.ReadToEnd());
                IntPtr bytes = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                attributes = Marshal.AllocHGlobal(bytes);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)) throw new Win32Exception(Marshal.GetLastWin32Error());
                attributesReady = true;
                if (!UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x00020016), console, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                var startup = new StartupInfoEx { Attributes = attributes };
                startup.Startup.Size = Marshal.SizeOf(typeof(StartupInfoEx));
                if (!CreateProcessW(executable, new StringBuilder(WindowsServices.Quote(executable) + " " + arguments), IntPtr.Zero, IntPtr.Zero, false,
                    0x00080000, IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out process))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                uint waited = WaitForSingleObject(process.Process, 90000);
                if (waited != 0) { TerminateProcess(process.Process, 1); throw new System.TimeoutException("EFI helper did not complete."); }
                uint exitCode;
                if (!GetExitCodeProcess(process.Process, out exitCode)) throw new Win32Exception(Marshal.GetLastWin32Error());
                ClosePseudoConsole(console); console = IntPtr.Zero;
                string output = captured.GetAwaiter().GetResult();
                output = Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
                output = Regex.Replace(output, @"\x1B\][^\x07]*(?:\x07|\x1B\\)", "");
                return new ProcessResult { ExitCode = unchecked((int)exitCode), Output = output.Trim() };
            }
            finally
            {
                if (console != IntPtr.Zero) ClosePseudoConsole(console);
                if (reader != null) reader.Dispose();
                if (attributesReady) DeleteProcThreadAttributeList(attributes);
                if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
                Close(inputRead); Close(inputWrite); Close(outputRead); Close(outputWrite); Close(process.Thread); Close(process.Process);
            }
        }

        private static void Close(IntPtr handle) { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }
}