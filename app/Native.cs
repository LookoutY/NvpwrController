using System;
using System.Runtime.InteropServices;

namespace Nvpwr
{
    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct CodeIntegrityInformation
        {
            public uint Length;
            public uint Options;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceStatus
        {
            public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode;
            public uint CheckPoint, WaitHint, ProcessId, ServiceFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ServiceConfiguration
        {
            public uint ServiceType, StartType, ErrorControl;
            public IntPtr BinaryPath, LoadOrderGroup;
            public uint TagId;
            public IntPtr Dependencies, StartName, DisplayName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr service, int informationLevel, out ServiceStatus status, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryServiceConfig(IntPtr service, IntPtr configuration, uint bufferSize, out uint bytesNeeded);

        [DllImport("advapi32.dll")]
        private static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int informationClass, ref CodeIntegrityInformation information,
            uint informationLength, out uint returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFirmwareType(out uint firmwareType);

        public static uint QueryCodeIntegrity()
        {
            var information = new CodeIntegrityInformation { Length = 8 };
            uint returned;
            int status = NtQuerySystemInformation(103, ref information, 8, out returned);
            if (status != 0)
                throw new InvalidOperationException("Code Integrity query failed: 0x" + status.ToString("X8"));
            return information.Options;
        }

        public static uint QueryFirmwareType()
        {
            uint firmwareType;
            if (!GetFirmwareType(out firmwareType))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return firmwareType;
        }

        public static DriverService QueryDriverService(string name)
        {
            IntPtr manager = OpenSCManager(null, null, 0x0001);
            if (manager == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            IntPtr service = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                service = OpenService(manager, name, 0x0001 | 0x0004);
                if (service == IntPtr.Zero) {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 1060) return null;
                    throw new System.ComponentModel.Win32Exception(error);
                }
                ServiceStatus status;
                uint needed;
                if (!QueryServiceStatusEx(service, 0, out status, (uint)Marshal.SizeOf(typeof(ServiceStatus)), out needed))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (!QueryServiceConfig(service, IntPtr.Zero, 0, out needed) && Marshal.GetLastWin32Error() != 122)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                if (needed == 0 || needed > 1024 * 1024) throw new InvalidOperationException("Invalid service configuration size.");
                buffer = Marshal.AllocHGlobal((int)needed);
                if (!QueryServiceConfig(service, buffer, needed, out needed))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                var configuration = (ServiceConfiguration)Marshal.PtrToStructure(buffer, typeof(ServiceConfiguration));
                return new DriverService {
                    State = ((System.ServiceProcess.ServiceControllerStatus)status.CurrentState).ToString(),
                    Path = Marshal.PtrToStringUni(configuration.BinaryPath) ?? "",
                    Type = configuration.ServiceType
                };
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (service != IntPtr.Zero) CloseServiceHandle(service);
                CloseServiceHandle(manager);
            }
        }
    }
}