using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PrinterUtility
{
    /// <summary>P/Invoke declarations for winspool.drv and a few kernel32 helpers.</summary>
    internal static class Native
    {
        public const uint PRINTER_ENUM_LOCAL = 0x00000002;
        public const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;
        public const uint PRINTER_ENUM_NAME = 0x00000008;

        public const uint PRINTER_ATTRIBUTE_SHARED = 0x00000008;
        public const uint PRINTER_ATTRIBUTE_NETWORK = 0x00000010;
        public const uint PRINTER_ATTRIBUTE_LOCAL = 0x00000040;

        public const int PRINTER_ACCESS_ADMINISTER = 0x00000004;
        public const int PRINTER_ACCESS_USE = 0x00000008;
        public const int PRINTER_ALL_ACCESS = 0x000F000C;

        public const uint PRINTER_CONTROL_PURGE = 3;

        public const int ERROR_INSUFFICIENT_BUFFER = 122;
        public const int ERROR_CANCELLED = 1223;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PRINTER_INFO_2
        {
            public string pServerName;
            public string pPrinterName;
            public string pShareName;
            public string pPortName;
            public string pDriverName;
            public string pComment;
            public string pLocation;
            public IntPtr pDevMode;
            public string pSepFile;
            public string pPrintProcessor;
            public string pDatatype;
            public string pParameters;
            public IntPtr pSecurityDescriptor;
            public uint Attributes;
            public uint Priority;
            public uint DefaultPriority;
            public uint StartTime;
            public uint UntilTime;
            public uint Status;
            public uint cJobs;
            public uint AveragePPM;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct PRINTER_DEFAULTS
        {
            public IntPtr pDatatype;
            public IntPtr pDevMode;
            public int DesiredAccess;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "EnumPrintersW")]
        public static extern bool EnumPrinters(uint flags, string name, uint level, IntPtr pPrinterEnum,
            uint cbBuf, out uint pcbNeeded, out uint pcReturned);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetDefaultPrinterW")]
        public static extern bool GetDefaultPrinter(StringBuilder pszBuffer, ref int pcchBuffer);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetDefaultPrinterW")]
        public static extern bool SetDefaultPrinter(string pszPrinter);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "AddPrinterConnectionW")]
        public static extern bool AddPrinterConnection(string pName);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "DeletePrinterConnectionW")]
        public static extern bool DeletePrinterConnection(string pName);

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenPrinterW")]
        public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, ref PRINTER_DEFAULTS pDefault);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true, EntryPoint = "SetPrinterW")]
        public static extern bool SetPrinter(IntPtr hPrinter, uint level, IntPtr pPrinter, uint command);

        [DllImport("winspool.drv", SetLastError = true)]
        public static extern bool DeletePrinter(IntPtr hPrinter);

        public const uint PRINTER_ATTRIBUTE_PUBLISHED = 0x00002000;
        public const uint DSPRINT_PUBLISH = 0x00000001;
        public const uint DSPRINT_UNPUBLISH = 0x00000004;
        public const int ERROR_IO_PENDING = 997;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PRINTER_INFO_7
        {
            public string pszObjectGUID;
            public uint dwAction;
        }

        [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetPrinterW")]
        public static extern bool GetPrinter(IntPtr hPrinter, uint level, IntPtr pPrinter, uint cbBuf, out uint pcbNeeded);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("kernel32.dll")]
        public static extern bool AttachConsole(int dwProcessId);

        public const int ATTACH_PARENT_PROCESS = -1;
    }
}
