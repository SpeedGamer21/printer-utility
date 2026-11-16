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

    internal enum CredentialResult
    {
        /// <summary>Saved, and the server accepted it.</summary>
        Accepted,
        /// <summary>The server rejected the account; nothing was saved.</summary>
        Rejected,
        /// <summary>A connection under another account is still open (1219) – restart the spooler and try again.</summary>
        SessionConflict,
        /// <summary>Saved, but the server couldn't be reached to check it.</summary>
        Unverified,
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

        /// <summary>
        /// The accounts to try for what was typed. "DOMAIN\user", "user@domain" and e-mail addresses are used as is.
        /// A plain "user" is tried as a local account of the server (SERVER\user) and, when this PC's user is
        /// signed in to a domain, as a domain account (DOMAIN\user) – "admin" can mean either.
        /// </summary>
        public static string[] Candidates(string host, string user)
        {
            user = (user ?? "").Trim();
            var local = QualifyUser(host, user);
            if (local == user) return new[] { user };
            var domain = Environment.UserDomainName;
            if (string.IsNullOrEmpty(domain) || string.Equals(domain, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(domain + "\\" + user, local, StringComparison.OrdinalIgnoreCase))
                return new[] { local };
            return new[] { local, domain + "\\" + user };
        }

        /// <summary>
        /// Checks the user name / password against the server first (a plain user name as SERVER\user, then as
        /// DOMAIN\user), saves the account it accepts for the server (and its other name form), drops stale
        /// sessions and tests the saved credential. Rejected accounts are not saved.
        /// </summary>
        public static CredentialResult Save(string server, string user, string password)
        {
            var host = HostOf(server);
            if (host.Length == 0) throw new ArgumentException("Enter the server name or IP.");
            if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("Enter a user name.");
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Enter the password. Accounts with a blank password can't log on over the network.");

            var candidates = Candidates(host, user);
            if (Session.IsDemo) { Logger.Ok($"[demo] would save credentials for {host} as {candidates[0]}"); return CredentialResult.Accepted; }

            if (SystemTools.IsAdmin)
                Logger.Warn("Running elevated: the credential is saved for the account running this window. " +
                            "If that isn't the user who prints, run Printer Utility as that user.");

            var names = Targets(host);   // DNS once: short name + FQDN
            string chosen = null;
            bool unreachable = false;
            foreach (var candidate in candidates)
            {
                DropSessions(names);
                Logger.Info($"Signing in to \\\\{host} as {candidate}...");
                int rc = Connect(host, candidate, password);
                if (rc == 0)
                {
                    WNetCancelConnection2($@"\\{host}\IPC$", 0, true);
                    chosen = candidate;
                    break;
                }
                if (rc == 1219)
                {
                    Logger.Warn($"\\\\{host} still has an open connection under another account (1219), " +
                                "usually held by the Print Spooler or a mapped drive.");
                    return CredentialResult.SessionConflict;
                }
                if (IsCredentialError(rc)) { Logger.Error($"\\\\{host} rejected {candidate}: {Reason(rc)}"); continue; }

                Logger.Warn($"Could not check the account with \\\\{host}: 0x{rc:X8} {new Win32Exception(rc).Message}");
                unreachable = true;
                break;
            }

            if (chosen == null && !unreachable)
            {
                Logger.Error($"Nothing was saved: \\\\{host} didn't accept " + string.Join(" or ", candidates) + ". " +
                             "For a domain account type DOMAIN\\user; for an account of that PC type " +
                             HostShortName(host) + "\\user. The account must have a password.");
                return CredentialResult.Rejected;
            }
            chosen = chosen ?? candidates[0];

            uint persist = AllowedPersist();
            if (persist == CRED_PERSIST_NONE)
            {
                Logger.Error("Group Policy doesn't allow saving passwords for network logons on this PC " +
                             "('Network access: Do not allow storage of passwords and credentials for network authentication'). " +
                             "Nothing was saved. Disable that policy for these PCs, or give the account Print permission " +
                             "on the printer so users print with their own sign-in.");
                return CredentialResult.Rejected;
            }
            if (persist == CRED_PERSIST_SESSION)
                Logger.Warn("Group Policy only allows saving this credential until you sign out. " +
                            "After a sign-out or restart it has to be saved again.");

            foreach (var target in names)
            {
                Write(target, chosen, password, persist);
                Logger.Ok($"Saved Windows credential for '{target}' (user {chosen}).");
            }

            DropSessions(names);
            return unreachable ? CredentialResult.Unverified : Test(host);
        }

        /// <summary>Plain-language reason for a logon error from the server.</summary>
        public static string Reason(int code)
        {
            switch (code)
            {
                case 1326: case 86: return "wrong user name or password (or the account doesn't exist on that PC / domain).";
                case 1327: return "account restriction – a blank password, logon hours, or allowed-workstations limit.";
                case 1331: return "the account is disabled. The built-in Administrator account of a PC is disabled by default.";
                case 1385: return "that PC's policy doesn't allow this account to log on over the network " +
                                  "('Deny access to this computer from the network'). Security baselines block local " +
                                  "administrator accounts and often Domain Admins on workstations – use a normal account.";
                case 1330: return "the password has expired.";
                case 1907: return "the password must be changed before the first logon.";
                case 1793: return "the account has expired.";
                case 1909: return "the account is locked out.";
                case 1244: return "the server requires authentication.";
                default: return new Win32Exception(code).Message;
            }
        }

        public static void Remove(string server)
        {
            var host = HostOf(server);
            if (host.Length == 0) throw new ArgumentException("Enter the server name or IP.");
            if (Session.IsDemo) { Logger.Ok($"[demo] would remove saved credentials for {host}"); return; }
            var names = Targets(host);
            int removed = 0;
            foreach (var target in names)
            {
                if (CredDelete(target, CRED_TYPE_DOMAIN_PASSWORD, 0)) { removed++; Logger.Ok($"Removed saved credential for '{target}'."); }
                else if (Marshal.GetLastWin32Error() != ERROR_NOT_FOUND)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (removed == 0) Logger.Info($"No saved credential for {host}.");
            DropSessions(names);
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
            code == 1326 || code == 86 || code == 1244 || code == 1219 || code == 1327 || code == 1331 || code == 1909 ||
            code == 1385 || code == 1330 || code == 1907 || code == 1793;

        /// <summary>The name as entered, plus the short name / FQDN counterpart, so both ways of connecting work.</summary>
        private static string[] Targets(string host)
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                // Windows matches a saved credential by the exact name in the printer path, so an IP alone
                // doesn't cover \\PC-07\printer. Add the computer's names from reverse DNS when there are any.
                try
                {
                    var fqdn = Dns.GetHostEntry(ip).HostName;
                    // forward-confirmed: a stale PTR record must not attach the password to another PC's name
                    if (!string.IsNullOrEmpty(fqdn) && !IPAddress.TryParse(fqdn, out _) &&
                        Array.Exists(Dns.GetHostAddresses(fqdn), a => a.Equals(ip)))
                    {
                        var shortName = fqdn.Split('.')[0];
                        return string.Equals(shortName, fqdn, StringComparison.OrdinalIgnoreCase)
                            ? new[] { host, shortName }
                            : new[] { host, shortName, fqdn };
                    }
                }
                catch { /* no reverse DNS – save just the IP */ }
                return new[] { host };
            }
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

        /// <summary>
        /// How long Windows lets this user keep a domain (network logon) credential: normally on this PC for
        /// good, but the GPO "Do not allow storage of passwords and credentials for network authentication"
        /// (common in domain security baselines) cuts it to the sign-in session or blocks it entirely.
        /// </summary>
        private static uint AllowedPersist()
        {
            var max = new uint[CRED_TYPE_MAXIMUM];
            if (!CredGetSessionTypes((uint)max.Length, max)) return CRED_PERSIST_LOCAL_MACHINE;   // can't tell: try the normal way
            return Math.Min(max[CRED_TYPE_DOMAIN_PASSWORD], CRED_PERSIST_LOCAL_MACHINE);
        }

        private static void Write(string target, string user, string password, uint persist)
        {
            var blob = Encoding.Unicode.GetBytes(password);
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_DOMAIN_PASSWORD,
                TargetName = target,
                UserName = user,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = Marshal.AllocCoTaskMem(blob.Length),
                Persist = persist,
            };
            try
            {
                Marshal.Copy(blob, 0, cred.CredentialBlob, blob.Length);
                if (!CredWrite(ref cred, 0))
                {
                    int err = Marshal.GetLastWin32Error();
                    string why = err == 1312   // ERROR_NO_SUCH_LOGON_SESSION
                        ? "Windows has no credential store for this sign-in (Group Policy blocks saved network passwords, or the app runs as another user)."
                        : new Win32Exception(err).Message;
                    throw new Win32Exception(err, $"Could not save the credential for '{target}': {why}");
                }
            }
            finally
            {
                for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(cred.CredentialBlob, i, 0);
                Marshal.FreeCoTaskMem(cred.CredentialBlob);
                Array.Clear(blob, 0, blob.Length);
            }
        }

        /// <summary>
        /// Disconnects \\host\IPC$ – under every name of the server (short name, FQDN) – so the next connection
        /// uses the saved credential (avoids error 1219).
        /// </summary>
        private static void DropSessions(string[] names)
        {
            foreach (var name in names)
            {
                int rc = WNetCancelConnection2($@"\\{name}\IPC$", 0, true);
                if (rc == 0) Logger.Info($"Closed the existing connection to \\\\{name} (it used other credentials).");
            }
        }

        private static string HostShortName(string host) =>
            !IPAddress.TryParse(host, out _) && host.Contains(".") ? host.Substring(0, host.IndexOf('.')).ToUpperInvariant() : host.ToUpperInvariant();

        /// <summary>Opens \\host\IPC$ (with the given account, or the saved credential when user is null).</summary>
        private static int Connect(string host, string user, string password)
        {
            var res = new NETRESOURCE { dwType = RESOURCETYPE_ANY, lpRemoteName = $@"\\{host}\IPC$" };
            return WNetAddConnection2(ref res, password, user, 0);
        }

        /// <summary>Opens \\host\IPC$ with the saved credential to prove Windows picks it up.</summary>
        private static CredentialResult Test(string host)
        {
            int rc = Connect(host, null, null);
            if (rc == 0)
            {
                Logger.Ok($"\\\\{host} accepted the saved credential.");
                return CredentialResult.Accepted;
            }
            if (rc == 1219)
            {
                Logger.Warn($"Saved, but another connection to \\\\{host} under a different account is still open (1219).");
                return CredentialResult.SessionConflict;
            }
            if (IsCredentialError(rc))
            {
                Logger.Error($"\\\\{host} rejected the saved credential: {Reason(rc)}");
                return CredentialResult.Rejected;
            }
            Logger.Warn($"Saved, but the test connection to \\\\{host} failed: 0x{rc:X8} {new Win32Exception(rc).Message}");
            return CredentialResult.Unverified;
        }

        // ---------------------------------------------------------------- P/Invoke

        private const uint CRED_TYPE_DOMAIN_PASSWORD = 2;
        private const uint CRED_PERSIST_NONE = 0;
        private const uint CRED_PERSIST_SESSION = 1;
        private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
        private const int CRED_TYPE_MAXIMUM = 7;
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

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CredGetSessionTypes(uint maximumPersistCount, [Out] uint[] maximumPersist);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection2W")]
        private static extern int WNetAddConnection2(ref NETRESOURCE netResource, string password, string username, uint flags);

        [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetCancelConnection2W")]
        private static extern int WNetCancelConnection2(string name, uint flags, bool force);
    }
}
