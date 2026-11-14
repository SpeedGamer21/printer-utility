using System;
using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace PrinterUtility
{
    /// <summary>One Windows credential row (public: WPF binds to it).</summary>
    public sealed class SavedCredential
    {
        public string Target { get; set; }
        public string User { get; set; }
    }

    /// <summary>
    /// Saved Windows credentials for a print server / sharing PC (what Credential Manager stores),
    /// written straight to the user's credential vault so it works when Control Panel is blocked.
    ///
    /// Typical case: a domain-joined PC connecting to a printer shared by a workgroup PC (or a PC in
    /// another domain). Windows first tries the logged-on DOMAIN account, the credential prompt then
    /// rejects every password because a bare "user" is sent as DOMAIN\user, and an existing session to
    /// the server under the wrong account causes error 1219. Saving "SERVER\user" for the server and
    /// dropping the old session fixes it.
    ///
    /// Always runs as the logged-on user (never in the elevated helper): credentials are per user.
    /// </summary>
    internal static class Credentials
    {
        /// <summary>Strips \\, a share part and whitespace: "\\srv01\HP" → "srv01".</summary>
        public static string HostOf(string server)
        {
            var host = (server ?? "").Trim().TrimStart('\\').TrimEnd('\\');
            int slash = host.IndexOf('\\');
            return slash >= 0 ? host.Substring(0, slash) : host;
        }

        /// <summary>
        /// "jdoe" → "SRV01\jdoe" (a local account on the sharing PC). Leaves "DOMAIN\user", "user@domain"
        /// and Microsoft accounts (e-mail) untouched.
        /// </summary>
        public static string QualifyUser(string host, string user)
        {
            user = (user ?? "").Trim();
            if (user.Length == 0 || user.Contains("\\") || user.Contains("@")) return user;
            var shortName = host;
            if (!IPAddress.TryParse(host, out _) && host.Contains(".")) shortName = host.Substring(0, host.IndexOf('.'));
            return shortName + "\\" + user;
        }

        /// <summary>Saves the credential for the server (and its other name form), drops stale sessions, tests it.</summary>
        public static bool Save(string server, string user, string password)
        {
            var host = HostOf(server);
            if (host.Length == 0) throw new ArgumentException("Enter the server name or IP.");
            if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("Enter a user name.");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Enter the password. Accounts with a blank password can't log on over the network.");

            var qualified = QualifyUser(host, user);
            if (qualified != user.Trim()) Logger.Info($"User name '{user.Trim()}' → '{qualified}' (local account on {host}).");

            if (Session.IsDemo) { Logger.Ok($"[demo] would save credentials for {host} as {qualified}"); return true; }

            if (SystemTools.IsAdmin)
                Logger.Warn("Running elevated: the credential is saved for the account running this window. " +
                            "If that isn't the user who prints, run Printer Utility as that user.");

            foreach (var target in Targets(host))
            {
                Write(target, qualified, password);
                Logger.Ok($"Saved Windows credential for '{target}' (user {qualified}).");
            }

            DropSessions(host);
            return Test(host);
        }

        public static void Remove(string server)
        {
            var host = HostOf(server);
            if (host.Length == 0) throw new ArgumentException("Enter the server name or IP.");
            if (Session.IsDemo) { Logger.Ok($"[demo] would remove saved credentials for {host}"); return; }
            int removed = 0;
            foreach (var target in Targets(host))
            {
                if (CredDelete(target, CRED_TYPE_DOMAIN_PASSWORD, 0)) { removed++; Logger.Ok($"Removed saved credential for '{target}'."); }
                else if (Marshal.GetLastWin32Error() != ERROR_NOT_FOUND)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (removed == 0) Logger.Info($"No saved credential for {host}.");
            DropSessions(host);
        }

        /// <summary>
        /// Windows (domain password) credentials of the current user that point at a server – what Credential
        /// Manager lists under "Windows Credentials", minus RDP / app entries.
        /// </summary>
        public static System.Collections.Generic.List<SavedCredential> List()
        {
            var list = new System.Collections.Generic.List<SavedCredential>();
            if (Session.IsDemo)
            {
                list.Add(new SavedCredential { Target = "pc-0311", User = @"PC-0311\printuser" });
                list.Add(new SavedCredential { Target = "pc-0311.contoso.local", User = @"PC-0311\printuser" });
                return list;
            }
            if (!CredEnumerate(null, 0, out uint count, out IntPtr items))
            {
                if (Marshal.GetLastWin32Error() == ERROR_NOT_FOUND) return list;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                for (int i = 0; i < count; i++)
                {
                    var c = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(items, i * IntPtr.Size));
                    if (c.Type != CRED_TYPE_DOMAIN_PASSWORD || string.IsNullOrEmpty(c.TargetName)) continue;
                    if (c.TargetName.IndexOfAny(new[] { '/', ':', '*' }) >= 0) continue;   // TERMSRV/…, app entries, wildcards
                    list.Add(new SavedCredential { Target = c.TargetName, User = c.UserName });
                }
            }
            finally { CredFree(items); }
            list.Sort((a, b) => string.Compare(a.Target, b.Target, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        /// <summary>Error codes that mean "the server didn't accept who you are" – offer to save credentials.</summary>
        public static bool IsCredentialError(int code) =>
            code == 1326 || code == 86 || code == 1244 || code == 1219 || code == 1327 || code == 1331 || code == 1909;

        /// <summary>The name as entered, plus the short name / FQDN counterpart, so both ways of connecting work.</summary>
        private static string[] Targets(string host)
        {
            if (IPAddress.TryParse(host, out _)) return new[] { host };
            try
            {
                var fqdn = Dns.GetHostEntry(host).HostName;
                if (!string.IsNullOrEmpty(fqdn) && !string.Equals(fqdn, host, StringComparison.OrdinalIgnoreCase))
                {
                    var shortName = fqdn.Split('.')[0];
                    var other = host.Contains(".") ? shortName : fqdn;
                    if (!string.Equals(other, host, StringComparison.OrdinalIgnoreCase)) return new[] { host, other };
                }
            }
            catch { /* name doesn't resolve – save just what was typed */ }
            return new[] { host };
        }

        private static void Write(string target, string user, string password)
        {
            var blob = Encoding.Unicode.GetBytes(password);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_DOMAIN_PASSWORD,
                TargetName = target,
                UserName = user,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = Marshal.AllocCoTaskMem(blob.Length),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            try
            {
                Marshal.Copy(blob, 0, cred.CredentialBlob, blob.Length);
                if (!CredWrite(ref cred, 0))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new Win32Exception(err, $"Could not save the credential for '{target}': {new Win32Exception(err).Message}");
                }
            }
            finally
            {
                for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(cred.CredentialBlob, i, 0);
                Marshal.FreeCoTaskMem(cred.CredentialBlob);
                Array.Clear(blob, 0, blob.Length);
            }
        }

        /// <summary>Disconnects \\host\IPC$ so the next connection uses the saved credential (avoids error 1219).</summary>
        private static void DropSessions(string host)
        {
            int rc = WNetCancelConnection2($@"\\{host}\IPC$", 0, true);
            if (rc == 0) Logger.Info($"Closed the existing connection to \\\\{host} (it used other credentials).");
        }

        /// <summary>Opens \\host\IPC$ with the saved credential to prove it is accepted.</summary>
        private static bool Test(string host)
        {
            var res = new NETRESOURCE { dwType = RESOURCETYPE_ANY, lpRemoteName = $@"\\{host}\IPC$" };
            int rc = WNetAddConnection2(ref res, null, null, 0);
            switch (rc)
            {
                case 0:
                    Logger.Ok($"\\\\{host} accepted the saved credential. Add the printer again now.");
                    return true;
                case 1326: case 86: case 1327: case 1331: case 1909:
                    Logger.Error($"\\\\{host} rejected the user name or password: {new Win32Exception(rc).Message} " +
                                 "Use an account that exists on that PC (SERVER\\user), with a password.");
                    return false;
                case 1219:
                    Logger.Error($"Another connection to \\\\{host} with different credentials is still open (1219). " +
                                 "Close mapped drives / Explorer windows to that PC, or sign out and back in, then try again.");
                    return false;
                default:
                    Logger.Warn($"Saved, but the test connection to \\\\{host} failed: 0x{rc:X8} {new Win32Exception(rc).Message}");
                    return false;
            }
        }

        // ---------------------------------------------------------------- P/Invoke

        private const uint CRED_TYPE_DOMAIN_PASSWORD = 2;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
        private const int ERROR_NOT_FOUND = 1168;
        private const uint RESOURCETYPE_ANY = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NETRESOURCE
        {
            public uint dwScope;
            public uint dwType;
            public uint dwDisplayType;
            public uint dwUsage;
            public string lpLocalName;
            public string lpRemoteName;
            public string lpComment;
            public string lpProvider;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
        private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredEnumerateW")]
        private static extern bool CredEnumerate(string filter, uint flags, out uint count, out IntPtr credentials);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
        private static extern int WNetAddConnection2(ref NETRESOURCE netResource, string password, string username, uint flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetCancelConnection2W")]
        private static extern int WNetCancelConnection2(string name, uint flags, bool force);
    }
}
