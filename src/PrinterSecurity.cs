using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace PrinterUtility
{
    /// <summary>One account on a printer's Security tab (public: WPF binds to it).</summary>
    public sealed class PermissionEntry
    {
        internal PermissionEntry(SecurityIdentifier sid, string name, string rights)
        {
            Sid = sid; Name = name; Rights = rights;
        }
        internal SecurityIdentifier Sid { get; }
        public string Name { get; }
        public string Rights { get; set; }
        public bool CanRemove => !PrinterSecurity.IsProtected(Sid);
        public bool Pending { get; set; }
    }

    /// <summary>
    /// Print permission on a local (shared) printer – the printer's Properties → Security tab.
    ///
    /// On a domain, giving "Domain Users" the Print permission lets every domain user print with their own
    /// sign-in: no saved credentials are needed on the clients. Accounts are passed around as SIDs, so this
    /// works on non-English domains where the group isn't called "Domain Users".
    /// </summary>
    internal static class PrinterSecurity
    {
        /// <summary>"Print" in the Security tab: PRINTER_ACCESS_USE | READ_CONTROL (SDDL "SWRC").</summary>
        private const int PrintMask = 0x00020008;
        private const int PRINTER_ACCESS_USE = 0x8;
        private const int PRINTER_ACCESS_ADMINISTER = 0x4;
        private const int JOB_ACCESS_ADMINISTER = 0x10;
        private const int GENERIC_ALL = 0x10000000;

        /// <summary>The joined domain's "Domain Users" group, or null when this PC isn't on a domain.</summary>
        public static SecurityIdentifier DomainUsersSid()
        {
            var domain = PrimaryDomainSid();
            return domain == null ? null : new SecurityIdentifier(WellKnownSidType.AccountDomainUsersSid, domain);
        }

        /// <summary>
        /// "domain-users" (or "Domain Users"), "authenticated-users", a SID (S-1-…), or an account name
        /// such as CONTOSO\Printing-Staff.
        /// </summary>
        public static SecurityIdentifier Resolve(string account)
        {
            account = (account ?? "").Trim();
            var key = account.Replace(" ", "-").ToLowerInvariant();
            if (key == "domain-users" || key.EndsWith("\\domain-users"))
                return DomainUsersSid() ?? throw new InvalidOperationException("This PC is not joined to a domain, so there is no Domain Users group.");
            if (key == "authenticated-users")
                return new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            if (account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
                return new SecurityIdentifier(account);
            try
            {
                return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
            }
            catch (IdentityNotMappedException)
            {
                throw new ArgumentException($"Account '{account}' was not found. Use DOMAIN\\name.");
            }
        }

        public static string NameOf(SecurityIdentifier sid)
        {
            try { return ((NTAccount)sid.Translate(typeof(NTAccount))).Value; }
            catch { return sid.Value; }
        }

        /// <summary>
        /// Who has access to the printer, as the Security tab shows it: one row per account with Print /
        /// Manage this printer / Manage documents. Deny entries are listed too.
        /// </summary>
        public static System.Collections.Generic.List<PermissionEntry> List(string printer)
        {
            var rows = new System.Collections.Generic.List<PermissionEntry>();
            if (Session.IsDemo)
            {
                rows.Add(new PermissionEntry(new SecurityIdentifier(WellKnownSidType.WorldSid, null), "Everyone", "Print"));
                rows.Add(new PermissionEntry(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), @"BUILTIN\Administrators", "Print, Manage this printer, Manage documents"));
                return rows;
            }
            var bySid = new System.Collections.Generic.Dictionary<SecurityIdentifier, (bool print, bool manage, bool docs, bool deny)>();
            var order = new System.Collections.Generic.List<SecurityIdentifier>();
            foreach (GenericAce ace in ReadDacl(printer))
            {
                if (!(ace is CommonAce c)) continue;
                if (!bySid.TryGetValue(c.SecurityIdentifier, out var r)) { r = default; order.Add(c.SecurityIdentifier); }
                bool inheritOnly = (c.AceFlags & AceFlags.InheritOnly) != 0;
                if (c.AceQualifier == AceQualifier.AccessDenied) r.deny = true;
                else if (inheritOnly) r.docs |= (c.AccessMask & (JOB_ACCESS_ADMINISTER | GENERIC_ALL)) != 0;
                else
                {
                    r.print |= (c.AccessMask & PRINTER_ACCESS_USE) != 0;
                    r.manage |= (c.AccessMask & PRINTER_ACCESS_ADMINISTER) != 0;
                }
                bySid[c.SecurityIdentifier] = r;
            }
            foreach (var sid in order)
            {
                var r = bySid[sid];
                var rights = new System.Collections.Generic.List<string>();
                if (r.print) rights.Add("Print");
                if (r.manage) rights.Add("Manage this printer");
                if (r.docs) rights.Add("Manage documents");
                if (r.deny) rights.Add("Deny (some rights)");
                rows.Add(new PermissionEntry(sid, NameOf(sid), rights.Count > 0 ? string.Join(", ", rights) : "Special"));
            }
            return rows;
        }

        /// <summary>Accounts whose entries the printer needs to work and be managed – never removed from the UI.</summary>
        public static bool IsProtected(SecurityIdentifier sid) =>
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) ||
            sid.Value.StartsWith("S-1-15-") ||     // app packages / capabilities (Store apps, modern print dialog)
            sid.Value.StartsWith("S-1-5-80-") ||   // service SIDs
            sid.Value.StartsWith("S-1-5-99-");     // restricted services (PrintSpoolerService)

        /// <summary>True when the account has its own Print permission entry on the printer.</summary>
        public static bool CanPrint(string printer, SecurityIdentifier sid)
        {
            foreach (GenericAce ace in ReadDacl(printer))
                if (ace is CommonAce c && c.AceQualifier == AceQualifier.AccessAllowed && c.SecurityIdentifier == sid &&
                    (c.AceFlags & AceFlags.InheritOnly) == 0 && (c.AccessMask & PRINTER_ACCESS_USE) != 0)
                    return true;
            return false;
        }

        /// <summary>Adds (allow) or removes (allow = false) the account's Print permission. Needs admin.</summary>
        public static void SetPrint(string printer, SecurityIdentifier sid, bool allow)
        {
            var dacl = ReadDacl(printer);
            var name = NameOf(sid);
            bool changed = false;

            if (allow)
            {
                CommonAce existing = null;
                foreach (GenericAce ace in dacl)
                    if (ace is CommonAce c && c.AceQualifier == AceQualifier.AccessAllowed && c.SecurityIdentifier == sid &&
                        (c.AceFlags & AceFlags.InheritOnly) == 0) { existing = c; break; }

                if (existing == null)
                {
                    // allow ACEs go after any deny ACEs (canonical order)
                    int index = 0;
                    while (index < dacl.Count && dacl[index] is CommonAce d && d.AceQualifier == AceQualifier.AccessDenied) index++;
                    dacl.InsertAce(index, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, PrintMask, sid, false, null));
                    changed = true;
                }
                else if ((existing.AccessMask & PrintMask) != PrintMask)
                {
                    existing.AccessMask |= PrintMask;
                    changed = true;
                }
                if (!changed) { Logger.Info($"{name} can already print to \"{printer}\"."); return; }
            }
            else
            {
                for (int i = dacl.Count - 1; i >= 0; i--)
                    if (dacl[i] is CommonAce c && c.AceQualifier == AceQualifier.AccessAllowed && c.SecurityIdentifier == sid)
                    {
                        dacl.RemoveAce(i);
                        changed = true;
                    }
                if (!changed) { Logger.Info($"{name} has no permission entry on \"{printer}\"."); return; }
            }

            WriteDacl(printer, dacl);
            Logger.Ok(allow ? $"{name} can now print to \"{printer}\"." : $"Removed {name} from \"{printer}\"'s permissions.");
            if (!allow)
                Logger.Info("Users in other groups that have Print (for example Everyone) can still print.");
        }

        // ---------------------------------------------------------------- security descriptor I/O

        private static RawAcl ReadDacl(string printer)
        {
            uint rc = GetNamedSecurityInfo(printer, SE_PRINTER, DACL_SECURITY_INFORMATION,
                                           IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out IntPtr sd);
            if (rc != 0) throw new Win32Exception((int)rc, $"Could not read the permissions of \"{printer}\": {new Win32Exception((int)rc).Message}");
            try
            {
                var bytes = new byte[GetSecurityDescriptorLength(sd)];
                Marshal.Copy(sd, bytes, 0, bytes.Length);
                var raw = new RawSecurityDescriptor(bytes, 0);
                return raw.DiscretionaryAcl ?? new RawAcl(GenericAcl.AclRevision, 1);
            }
            finally { LocalFree(sd); }
        }

        private static void WriteDacl(string printer, RawAcl dacl)
        {
            var bytes = new byte[dacl.BinaryLength];
            dacl.GetBinaryForm(bytes, 0);
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                uint rc = SetNamedSecurityInfo(printer, SE_PRINTER, DACL_SECURITY_INFORMATION,
                                               IntPtr.Zero, IntPtr.Zero, handle.AddrOfPinnedObject(), IntPtr.Zero);
                if (rc != 0) throw new Win32Exception((int)rc, $"Could not change the permissions of \"{printer}\": {new Win32Exception((int)rc).Message}");
            }
            finally { handle.Free(); }
        }

        /// <summary>SID of the domain this PC is joined to (LSA primary domain), or null.</summary>
        private static SecurityIdentifier PrimaryDomainSid()
        {
            var attributes = new LSA_OBJECT_ATTRIBUTES { Length = Marshal.SizeOf(typeof(LSA_OBJECT_ATTRIBUTES)) };
            uint status = LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_VIEW_LOCAL_INFORMATION, out IntPtr policy);
            if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
            try
            {
                status = LsaQueryInformationPolicy(policy, PolicyPrimaryDomainInformation, out IntPtr info);
                if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status));
                try
                {
                    var domain = Marshal.PtrToStructure<POLICY_PRIMARY_DOMAIN_INFO>(info);
                    return domain.Sid == IntPtr.Zero ? null : new SecurityIdentifier(domain.Sid);   // no SID = workgroup
                }
                finally { LsaFreeMemory(info); }
            }
            finally { LsaClose(policy); }
        }

        // ---------------------------------------------------------------- P/Invoke

        private const int SE_PRINTER = 3;
        private const uint DACL_SECURITY_INFORMATION = 0x4;
        private const uint POLICY_VIEW_LOCAL_INFORMATION = 0x1;
        private const int PolicyPrimaryDomainInformation = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_OBJECT_ATTRIBUTES
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LSA_UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POLICY_PRIMARY_DOMAIN_INFO
        {
            public LSA_UNICODE_STRING Name;
            public IntPtr Sid;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetNamedSecurityInfoW")]
        private static extern uint GetNamedSecurityInfo(string name, int type, uint info, IntPtr owner, IntPtr group,
                                                        IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetNamedSecurityInfoW")]
        private static extern uint SetNamedSecurityInfo(string name, int type, uint info, IntPtr owner, IntPtr group,
                                                        IntPtr dacl, IntPtr sacl);

        [DllImport("advapi32.dll")]
        private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [DllImport("advapi32.dll")]
        private static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attributes, uint access, out IntPtr policy);

        [DllImport("advapi32.dll")]
        private static extern uint LsaQueryInformationPolicy(IntPtr policy, int infoClass, out IntPtr buffer);

        [DllImport("advapi32.dll")]
        private static extern uint LsaFreeMemory(IntPtr buffer);

        [DllImport("advapi32.dll")]
        private static extern uint LsaClose(IntPtr policy);

        [DllImport("advapi32.dll")]
        private static extern int LsaNtStatusToWinError(uint status);
    }
}
