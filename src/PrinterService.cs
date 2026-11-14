using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace PrinterUtility
{
    internal sealed class PrinterInfo
    {
        public string Name;
        public string ServerName;
        public string ShareName;
        public string Port;
        public string Driver;
        public string Location;
        public string Comment;
        public uint Attributes;
        public uint Status;
        public uint Jobs;
        public bool IsDefault;

        public bool IsNetwork =>
            (Attributes & Native.PRINTER_ATTRIBUTE_NETWORK) != 0 ||
            (Name != null && Name.StartsWith(@"\\", StringComparison.Ordinal));

        public bool IsShared => (Attributes & Native.PRINTER_ATTRIBUTE_SHARED) != 0;

        public bool IsPublished => (Attributes & Native.PRINTER_ATTRIBUTE_PUBLISHED) != 0;

        public string LocalUnc => @"\\" + Session.MachineName + @"\" + ShareName;

        public override string ToString() => Name;

        public string TypeText => IsNetwork ? "Network" : (IsShared ? "Local (shared)" : "Local");

        public string StatusText => PrinterService.DescribeStatus(Status);

        /// <summary>UNC path for a printer listed on a remote server.</summary>
        public string UncPath(string server)
        {
            if (string.IsNullOrEmpty(ShareName)) return Name;
            var s = server.StartsWith(@"\\") ? server : @"\\" + server;
            return s.TrimEnd('\\') + @"\" + ShareName;
        }
    }

    /// <summary>Printer operations via the spooler API. Nothing here needs Control Panel.</summary>
    internal static class PrinterService
    {
        private const string WindowsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Windows";

        public static List<PrinterInfo> GetPrinters()
        {
            if (Session.IsDemo) return Session.DemoPrinters();
            var list = Enumerate(Native.PRINTER_ENUM_LOCAL | Native.PRINTER_ENUM_CONNECTIONS, null);
            var def = GetDefaultPrinter();
            foreach (var p in list)
                p.IsDefault = string.Equals(p.Name, def, StringComparison.OrdinalIgnoreCase);
            list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>Lists the shared printers published by a print server / PC.</summary>
        public static List<PrinterInfo> GetServerPrinters(string server)
        {
            if (Session.IsDemo) return Session.DemoServerPrinters();
            var name = NormalizeServer(server);
            var all = Enumerate(Native.PRINTER_ENUM_NAME, name);
            var shared = new List<PrinterInfo>();
            foreach (var p in all)
                if (p.IsShared && !string.IsNullOrEmpty(p.ShareName)) shared.Add(p);
            shared.Sort((a, b) => string.Compare(a.ShareName, b.ShareName, StringComparison.OrdinalIgnoreCase));
            return shared;
        }

        public static string NormalizeServer(string server)
        {
            server = (server ?? "").Trim().TrimEnd('\\');
            if (!server.StartsWith(@"\\")) server = @"\\" + server.TrimStart('\\');
            return server;
        }

        private static List<PrinterInfo> Enumerate(uint flags, string name)
        {
            var result = new List<PrinterInfo>();
            Native.EnumPrinters(flags, name, 2, IntPtr.Zero, 0, out uint needed, out _);
            if (needed == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 0 && err != Native.ERROR_INSUFFICIENT_BUFFER) throw new Win32Exception(err);
                return result;
            }

            IntPtr buffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!Native.EnumPrinters(flags, name, 2, buffer, needed, out _, out uint count))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                int size = Marshal.SizeOf(typeof(Native.PRINTER_INFO_2));
                for (int i = 0; i < count; i++)
                {
                    var ptr = new IntPtr(buffer.ToInt64() + (long)i * size);
                    var pi = (Native.PRINTER_INFO_2)Marshal.PtrToStructure(ptr, typeof(Native.PRINTER_INFO_2));
                    result.Add(new PrinterInfo
                    {
                        Name = pi.pPrinterName,
                        ServerName = pi.pServerName,
                        ShareName = pi.pShareName,
                        Port = pi.pPortName,
                        Driver = pi.pDriverName,
                        Location = pi.pLocation,
                        Comment = pi.pComment,
                        Attributes = pi.Attributes,
                        Status = pi.Status,
                        Jobs = pi.cJobs,
                    });
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return result;
        }

        public static string GetDefaultPrinter()
        {
            int size = 0;
            Native.GetDefaultPrinter(null, ref size);
            if (size == 0) return null;
            var sb = new StringBuilder(size);
            return Native.GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
        }

        public static void SetDefaultPrinter(string name)
        {
            if (Session.IsDemo) { Logger.Ok("[demo] nothing changed"); return; }
            if (IsWindowsManagingDefault())
            {
                SetWindowsManagesDefault(false);
                Logger.Info("Turned off \"Let Windows manage my default printer\" so the choice sticks.");
            }
            if (!Native.SetDefaultPrinter(name))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"SetDefaultPrinter failed (0x{err:X8}). " +
                    (err == 0x709 ? "Try the fix \"Error 0x00000709 when setting the default printer\"." : ""));
            }
        }

        /// <summary>HKCU LegacyDefaultPrinterMode: 0/absent = Windows manages, 1 = user chooses.</summary>
        public static bool IsWindowsManagingDefault()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(WindowsKey))
            {
                var v = k?.GetValue("LegacyDefaultPrinterMode");
                return !(v is int i && i == 1);
            }
        }

        public static void SetWindowsManagesDefault(bool managed)
        {
            if (Session.IsDemo) { Logger.Ok("[demo] nothing changed"); return; }
            using (var k = Registry.CurrentUser.CreateSubKey(WindowsKey))
                k.SetValue("LegacyDefaultPrinterMode", managed ? 0 : 1, RegistryValueKind.DWord);
        }

        /// <summary>Per-user connection to a shared printer (\\server\share). Installs the driver from the server.</summary>
        public static void AddConnection(string unc)
        {
            if (Session.IsDemo) { Logger.Ok("[demo] nothing changed"); return; }
            if (!Native.AddPrinterConnection(unc))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"AddPrinterConnection failed: {Describe(err)}");
            }
        }

        public static void RemoveConnection(string unc)
        {
            if (Session.IsDemo) { Logger.Ok("[demo] nothing changed"); return; }
            if (!Native.DeletePrinterConnection(unc))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"DeletePrinterConnection failed: {Describe(err)}");
            }
        }

        /// <summary>Deletes a local (non-connection) printer. Requires admin.</summary>
        public static void DeleteLocalPrinter(string name)
        {
            var defaults = new Native.PRINTER_DEFAULTS { DesiredAccess = Native.PRINTER_ALL_ACCESS };
            if (!Native.OpenPrinter(name, out IntPtr h, ref defaults))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"OpenPrinter failed: {Describe(Marshal.GetLastWin32Error())}");
            try
            {
                if (!Native.DeletePrinter(h))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"DeletePrinter failed: {Describe(Marshal.GetLastWin32Error())}");
            }
            finally { Native.ClosePrinter(h); }
        }

        /// <summary>Cancels all jobs in a printer queue.</summary>
        public static void PurgeQueue(string name)
        {
            if (Session.IsDemo) { Logger.Ok("[demo] nothing changed"); return; }
            var defaults = new Native.PRINTER_DEFAULTS { DesiredAccess = Native.PRINTER_ACCESS_ADMINISTER };
            if (!Native.OpenPrinter(name, out IntPtr h, ref defaults))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Cannot open \"{name}\" for administration: {Describe(err)}");
            }
            try
            {
                if (!Native.SetPrinter(h, 0, IntPtr.Zero, Native.PRINTER_CONTROL_PURGE))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, $"Purge failed: {Describe(err)}");
                }
            }
            finally { Native.ClosePrinter(h); }
        }

        // ------------------------------------------------------------ sharing

        /// <summary>Returns an error message, or null when the share name is valid.</summary>
        public static string ValidateShareName(string share, string forPrinter)
        {
            share = (share ?? "").Trim();
            if (share.Length == 0) return "Enter a share name.";
            if (share.Length > 80) return "Share names can be at most 80 characters.";
            if (share.IndexOfAny("\\/:*?\"<>|,;=[]+".ToCharArray()) >= 0)
                return "Share names cannot contain \\ / : * ? \" < > | , ; = [ ] +";
            foreach (var p in GetPrinters())
                if (!p.IsNetwork && p.IsShared && !string.Equals(p.Name, forPrinter, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.ShareName, share, StringComparison.OrdinalIgnoreCase))
                    return $"\"{share}\" is already used by \"{p.Name}\".";
            return null;
        }

        /// <summary>Suggests a share name: printer name without spaces/illegal characters.</summary>
        public static string SuggestShareName(string printer)
        {
            var sb = new StringBuilder();
            foreach (char c in printer ?? "")
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') sb.Append(c);
            var s = sb.ToString();
            return s.Length > 40 ? s.Substring(0, 40) : s;
        }

        /// <summary>Shares / unshares a local printer and optionally publishes it in Active Directory. Requires admin.</summary>
        public static void SetSharing(string printer, bool share, string shareName, bool publish)
        {
            var defaults = new Native.PRINTER_DEFAULTS { DesiredAccess = Native.PRINTER_ALL_ACCESS };
            if (!Native.OpenPrinter(printer, out IntPtr h, ref defaults))
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Cannot open \"{printer}\": {Describe(err)}");
            }
            try
            {
                Native.GetPrinter(h, 2, IntPtr.Zero, 0, out uint needed);
                IntPtr buf = Marshal.AllocHGlobal((int)needed);
                uint attributes;
                try
                {
                    if (!Native.GetPrinter(h, 2, buf, needed, out _))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    var pi = (Native.PRINTER_INFO_2)Marshal.PtrToStructure(buf, typeof(Native.PRINTER_INFO_2));
                    pi.pSecurityDescriptor = IntPtr.Zero;   // leave permissions unchanged
                    if (share)
                    {
                        pi.Attributes |= Native.PRINTER_ATTRIBUTE_SHARED;
                        pi.pShareName = shareName.Trim();
                    }
                    else
                    {
                        pi.Attributes &= ~Native.PRINTER_ATTRIBUTE_SHARED;
                    }
                    attributes = pi.Attributes;

                    IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.PRINTER_INFO_2)));
                    try
                    {
                        Marshal.StructureToPtr(pi, p, false);
                        if (!Native.SetPrinter(h, 2, p, 0))
                        {
                            int err = Marshal.GetLastWin32Error();
                            throw new Win32Exception(err, $"Could not change sharing: {Describe(err)}");
                        }
                    }
                    finally
                    {
                        Marshal.DestroyStructure(p, typeof(Native.PRINTER_INFO_2));
                        Marshal.FreeHGlobal(p);
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }

                bool published = (attributes & Native.PRINTER_ATTRIBUTE_PUBLISHED) != 0;
                if (share && publish) Publish(h, Native.DSPRINT_PUBLISH);
                else if (published) Publish(h, Native.DSPRINT_UNPUBLISH);
            }
            finally { Native.ClosePrinter(h); }
        }

        private static void Publish(IntPtr h, uint action)
        {
            var info = new Native.PRINTER_INFO_7 { dwAction = action };
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.PRINTER_INFO_7)));
            try
            {
                Marshal.StructureToPtr(info, p, false);
                if (!Native.SetPrinter(h, 7, p, 0))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING)
                    {
                        Logger.Warn((action == Native.DSPRINT_PUBLISH ? "Publishing in Active Directory" : "Removing from Active Directory") +
                                    $" failed: {Describe(err)}");
                        return;
                    }
                }
                Logger.Info(action == Native.DSPRINT_PUBLISH ? "  published in Active Directory" : "  removed from Active Directory");
            }
            finally
            {
                Marshal.DestroyStructure(p, typeof(Native.PRINTER_INFO_7));
                Marshal.FreeHGlobal(p);
            }
        }

        // printui.dll is the print UI engine itself (not a Control Panel applet), so it keeps working
        // when the NoControlPanel policy is applied.
        public static void PrintTestPage(string name) => PrintUi($"/k /n {Quote(name)}");
        public static void OpenQueue(string name) => PrintUi($"/o /n {Quote(name)}");
        public static void OpenPreferences(string name) => PrintUi($"/e /n {Quote(name)}");
        public static void OpenProperties(string name) => PrintUi($"/p /n {Quote(name)}");

        /// <summary>Adds a per-machine connection (all users of this PC). Requires admin; appears after spooler restart / next logon.</summary>
        public static int AddMachineConnection(string unc) => PrintUiWait($"/ga /n {Quote(unc)}");
        public static int RemoveMachineConnection(string unc) => PrintUiWait($"/gd /n {Quote(unc)}");

        private static void PrintUi(string args)
        {
            if (Session.IsDemo) { Logger.Ok("[demo] nothing changed"); return; }
            Process.Start(new ProcessStartInfo("rundll32.exe", "printui.dll,PrintUIEntry " + args) { UseShellExecute = false });
        }

        private static int PrintUiWait(string args)
        {
            using (var p = Process.Start(new ProcessStartInfo("rundll32.exe", "printui.dll,PrintUIEntry " + args)
                   { UseShellExecute = false, CreateNoWindow = true }))
            {
                p.WaitForExit(120000);
                return p.HasExited ? p.ExitCode : -1;
            }
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

        public static string Describe(int err)
        {
            string hint;
            switch (err)
            {
                case 0x11b: hint = " → apply \"Error 0x0000011b\" fix on the client (RPC over named pipes) or server"; break;
                case 0x709: hint = " → check the name/share; apply the client named-pipes fix, or the 0x709 default-printer fix"; break;
                case 0x7c: hint = " → apply the client named-pipes fix (RPC over named pipes)"; break;
                case 0xbcb: hint = " → driver not found on server / Point and Print restriction"; break;
                case 5: hint = " → access denied (Point and Print restriction or permissions; try 'Add for all users' as admin)"; break;
                case 1722: hint = " → RPC server unavailable (server off, firewall, or spooler stopped)"; break;
                case 53: hint = " → network path not found (name/DNS/firewall)"; break;
                case 1326: case 86: case 1244:
                    hint = " → credentials rejected: save SERVER\\user for this server (Tools → Credential Manager); on a domain-joined PC also apply 'client-domain-rpc-auth'"; break;
                case 1219: hint = " → already connected with other credentials: save the right ones under Tools → Credential Manager (closes the old session)"; break;
                default: hint = ""; break;
            }
            return $"0x{err:X8} {new Win32Exception(err).Message}{hint}";
        }

        public static string DescribeStatus(uint s)
        {
            if (s == 0) return "Ready";
            var map = new (uint flag, string text)[]
            {
                (0x1, "Paused"), (0x2, "Error"), (0x4, "Deleting"), (0x8, "Paper jam"), (0x10, "Paper out"),
                (0x20, "Manual feed"), (0x40, "Paper problem"), (0x80, "Offline"), (0x100, "I/O active"),
                (0x200, "Busy"), (0x400, "Printing"), (0x800, "Output bin full"), (0x1000, "Not available"),
                (0x2000, "Waiting"), (0x4000, "Processing"), (0x8000, "Initializing"), (0x10000, "Warming up"),
                (0x20000, "Toner low"), (0x40000, "No toner"), (0x80000, "Page punt"), (0x100000, "User intervention"),
                (0x200000, "Out of memory"), (0x400000, "Door open"), (0x800000, "Server unknown"), (0x1000000, "Power save"),
            };
            var parts = new List<string>();
            foreach (var (flag, text) in map)
                if ((s & flag) != 0) parts.Add(text);
            return parts.Count > 0 ? string.Join(", ", parts) : $"0x{s:X}";
        }
    }
}
