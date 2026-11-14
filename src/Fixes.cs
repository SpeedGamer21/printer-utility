using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace PrinterUtility
{
    internal enum FixScope { Client, Server, Both }

    internal enum FixState { Unknown, NotApplied, Partial, Applied }

    internal abstract class Fix
    {
        public string Id;
        public string Title;
        public string Description;
        public FixScope Scope;
        public bool RequiresAdmin = true;
        public bool Recommended;
        public string SecurityNote;          // shown as a warning before applying
        public bool RestartSpoolerAfter;
        public string ParameterPrompt;       // non-null => Apply needs a parameter from the user

        public abstract FixState GetState();
        public abstract void Apply(string parameter);
        public virtual bool CanRevert => false;
        public virtual void Revert() => throw new NotSupportedException("This fix cannot be reverted automatically.");

        public FixState SafeState()
        {
            try { return GetState(); }
            catch { return FixState.Unknown; }
        }
    }

    // ------------------------------------------------------------------ registry-based fixes

    internal sealed class RegValue
    {
        public RegistryHive Hive;
        public string Key;
        public string Name;
        public object Value;                 // ignored when FromParameter
        public RegistryValueKind Kind = RegistryValueKind.DWord;
        public bool FromParameter;
        public bool KeepOnRevert;            // shared with another fix – leave it in place on revert

        public RegValue(RegistryHive hive, string key, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord)
        {
            Hive = hive; Key = key; Name = name; Value = value; Kind = kind;
        }

        public static RegistryKey OpenBase(RegistryHive hive) =>
            RegistryKey.OpenBaseKey(hive, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);

        public string Path => (Hive == RegistryHive.LocalMachine ? "HKLM\\" : "HKCU\\") + Key + " : " + Name;
    }

    internal sealed class RegistryFix : Fix
    {
        public List<RegValue> Values = new List<RegValue>();

        public override FixState GetState()
        {
            int match = 0;
            foreach (var v in Values)
            {
                using (var b = RegValue.OpenBase(v.Hive))
                using (var k = b.OpenSubKey(v.Key))
                {
                    var cur = k?.GetValue(v.Name);
                    if (cur == null) continue;
                    if (v.FromParameter) { if (cur.ToString().Length > 0) match++; continue; }
                    if (Equals(Convert.ToString(cur), Convert.ToString(v.Value))) match++;
                }
            }
            if (match == Values.Count) return FixState.Applied;
            return match == 0 ? FixState.NotApplied : FixState.Partial;
        }

        public override void Apply(string parameter)
        {
            foreach (var v in Values)
            {
                var value = v.FromParameter ? (object)(parameter ?? "") : v.Value;
                using (var b = RegValue.OpenBase(v.Hive))
                using (var k = b.CreateSubKey(v.Key))
                    k.SetValue(v.Name, value, v.Kind);
                Logger.Info($"  set {v.Path} = {value}");
            }
        }

        public override bool CanRevert => true;

        public override void Revert()
        {
            foreach (var v in Values)
            {
                if (v.KeepOnRevert) { Logger.Info($"  kept {v.Path} (used by other fixes)"); continue; }
                using (var b = RegValue.OpenBase(v.Hive))
                using (var k = b.OpenSubKey(v.Key, true))
                {
                    if (k?.GetValue(v.Name) != null)
                    {
                        k.DeleteValue(v.Name, false);
                        Logger.Info($"  removed {v.Path}");
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ code-based fixes

    internal sealed class ActionFix : Fix
    {
        public Func<FixState> State;
        public Action<string> ApplyAction;
        public Action RevertAction;

        public override FixState GetState() => State();
        public override void Apply(string parameter) => ApplyAction(parameter);
        public override bool CanRevert => RevertAction != null;
        public override void Revert() { if (RevertAction == null) base.Revert(); else RevertAction(); }
    }

    /// <summary>Fixes applied when a PC is turned into a print server ("Prepare this PC" on the Share page).</summary>
    internal static class PrintServerPrep
    {
        public static readonly string[] Actions =
        {
            "fix:services-core", "fix:server-firewall", "fix:server-remote-endpoint", "fix:server-rpc-listener",
        };
    }

    // ------------------------------------------------------------------ catalog

    internal static class FixCatalog
    {
        private const string PrintersPolicy = @"SOFTWARE\Policies\Microsoft\Windows NT\Printers";
        private const string RpcPolicy = PrintersPolicy + @"\RPC";
        private const string PnPPolicy = PrintersPolicy + @"\PointAndPrint";
        private const string PrintControl = @"SYSTEM\CurrentControlSet\Control\Print";
        private const string UserWindows = @"Software\Microsoft\Windows NT\CurrentVersion\Windows";

        private const RegistryHive HKLM = RegistryHive.LocalMachine;

        public static readonly List<Fix> All = Build();

        public static Fix Find(string id) =>
            All.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

        private static List<Fix> Build()
        {
            var list = new List<Fix>();

            // ============================== CLIENT ==============================

            list.Add(new RegistryFix
            {
                Id = "client-rpc-namedpipes",
                Title = "Errors 0x0000011b / 0x00000709 / 0x0000007c when connecting to a shared printer",
                Scope = FixScope.Client,
                Recommended = true,
                Description =
                    "Windows 11 (22H2, 23H2, 24H2 and later) connects to remote spoolers with RPC over TCP by default. Many " +
                    "print servers / sharing PCs (Windows 10, older servers, PCs with the RPC listener locked down) only " +
                    "accept RPC over named pipes (SMB, port 445), so 'Add printer' fails with 0x0000011b, 0x00000709 or " +
                    "0x0000007c.\r\n\r\n" +
                    "Sets the 'Configure RPC connection settings' policy on THIS PC: Protocol = RPC over named pipes, " +
                    "Authentication = Default (the pre-22H2 behaviour). Not needed on Windows 10.\r\n\r\n" +
                    "Registry: HKLM\\" + RpcPolicy + "\\RpcUseNamedPipeProtocol = 1\r\n" +
                    "The sharing PC must accept named pipes (fix 'server-rpc-listener' there).\r\n" +
                    "Domain-joined PC and the credential prompt rejects every password? Also apply " +
                    "'client-domain-rpc-auth' and save the server's credentials under Tools.\r\n" +
                    "Note: if a GPO also manages this key, the GPO wins at the next refresh.",
                RestartSpoolerAfter = true,
                Values = { new RegValue(HKLM, RpcPolicy, "RpcUseNamedPipeProtocol", 1) },
            });

            list.Add(new RegistryFix
            {
                Id = "client-domain-rpc-auth",
                Title = "Domain-joined PC: 'Enter network credentials' rejects every user name / password when adding a shared printer",
                Scope = FixScope.Client,
                RestartSpoolerAfter = true,
                SecurityNote =
                    "Turns off RPC-level authentication for outgoing RPC over named pipes on THIS PC. The SMB connection " +
                    "underneath is still authenticated with the saved credentials. Only use it on clients that print to " +
                    "workgroup PCs / PCs outside your domain.",
                Description =
                    "On an AD-joined Windows 11 PC, RPC over named pipes authenticates with the logged-on DOMAIN account by " +
                    "default. A printer shared from a workgroup PC (or a PC in another domain/forest, or reached by IP) " +
                    "doesn't know that account, so the credential prompt keeps coming back no matter what you type, or " +
                    "the connection fails with 0x0000011b / 0x00000709 / 0x0000052e (bad user name or password).\r\n\r\n" +
                    "Sets 'Configure RPC connection settings' on THIS PC:\r\n" +
                    "  RpcUseNamedPipeProtocol = 1 (RPC over named pipes)\r\n" +
                    "  RpcAuthentication = 2 (authentication disabled for outgoing named-pipe RPC)\r\n" +
                    "Registry: HKLM\\" + RpcPolicy + "\r\n\r\n" +
                    "Then, under Tools → Credential Manager → 'Add credential', save an account that exists ON the sharing " +
                    "PC (SHARINGPC\\user, with a password). That also clears the cached connection that causes error 1219.\r\n\r\n" +
                    "If the SHARING PC is the domain-joined one and clients can't log on, apply 'server-rpc-listener' " +
                    "there (it sets Negotiate instead of Kerberos-only, so NTLM / IP connections work).\r\n" +
                    "Revert removes RpcAuthentication only (named pipes stay on).",
                Values =
                {
                    new RegValue(HKLM, RpcPolicy, "RpcUseNamedPipeProtocol", 1) { KeepOnRevert = true },
                    new RegValue(HKLM, RpcPolicy, "RpcAuthentication", 2),
                },
            });

            list.Add(new ActionFix
            {
                Id = "client-default-0x709",
                Title = "Error 0x00000709 when setting the default printer / default keeps changing",
                Scope = FixScope.Client,
                RequiresAdmin = false,
                Recommended = true,
                Description =
                    "Per-user fix for the CURRENT user (no admin needed):\r\n" +
                    " • turns off 'Let Windows manage my default printer' (LegacyDefaultPrinterMode = 1), which otherwise " +
                    "switches the default to the last-used printer;\r\n" +
                    " • removes a stale 'Device' value that points to a printer that no longer exists (a common cause of " +
                    "0x00000709 'operation could not be completed');\r\n" +
                    " • re-applies the current default printer.\r\n\r\n" +
                    "Registry: HKCU\\" + UserWindows,
                State = () =>
                {
                    if (PrinterService.IsWindowsManagingDefault()) return FixState.NotApplied;
                    return DeviceValueIsStale() ? FixState.Partial : FixState.Applied;
                },
                ApplyAction = _ =>
                {
                    PrinterService.SetWindowsManagesDefault(false);
                    Logger.Info("  LegacyDefaultPrinterMode = 1");
                    if (DeviceValueIsStale())
                    {
                        using (var k = Registry.CurrentUser.OpenSubKey(UserWindows, true))
                            k?.DeleteValue("Device", false);
                        Logger.Info("  removed stale 'Device' value");
                    }
                    var def = PrinterService.GetDefaultPrinter();
                    if (!string.IsNullOrEmpty(def))
                    {
                        try { PrinterService.SetDefaultPrinter(def); Logger.Info($"  default printer re-applied: {def}"); }
                        catch (Exception ex) { Logger.Warn("  " + ex.Message); }
                    }
                },
                RevertAction = () => { PrinterService.SetWindowsManagesDefault(true); Logger.Info("  LegacyDefaultPrinterMode = 0"); },
            });

            list.Add(new RegistryFix
            {
                Id = "client-pointandprint",
                Title = "Users get 'Do you trust this printer?' / admin prompt when adding a shared printer",
                Scope = FixScope.Client,
                ParameterPrompt =
                    "Enter the print servers users may install drivers from (semicolon separated, FQDN and short names):\r\n" +
                    "e.g.  printsrv01;printsrv01.contoso.local",
                SecurityNote =
                    "This relaxes the PrintNightmare mitigation (KB5005652) so NON-admins can install printer drivers, " +
                    "limited to the servers you list. Only list print servers you control. " +
                    "Better long-term: deploy printers with GPO/Print Management, or use 'Add for all users' (admin) in this tool.",
                Description =
                    "Since August 2021 only administrators can install printer drivers via Point and Print, so standard " +
                    "users get an elevation prompt or 'access denied' when adding a shared printer.\r\n\r\n" +
                    "Sets Point and Print restrictions to allow silent driver install ONLY from the listed trusted servers:\r\n" +
                    "  RestrictDriverInstallationToAdministrators = 0\r\n" +
                    "  Restricted = 1, TrustedServers = 1, ServerList = <your servers>, InForest = 0\r\n" +
                    "  NoWarningNoElevationOnInstall = 1, UpdatePromptSettings = 2\r\n\r\n" +
                    "Registry: HKLM\\" + PnPPolicy + "\r\n" +
                    "Tip: you usually don't need this – as an admin, use 'Add for all users' on the Printers tab instead.",
                Values =
                {
                    new RegValue(HKLM, PnPPolicy, "RestrictDriverInstallationToAdministrators", 0),
                    new RegValue(HKLM, PnPPolicy, "Restricted", 1),
                    new RegValue(HKLM, PnPPolicy, "TrustedServers", 1),
                    new RegValue(HKLM, PnPPolicy, "ServerList", null, RegistryValueKind.String) { FromParameter = true },
                    new RegValue(HKLM, PnPPolicy, "InForest", 0),
                    new RegValue(HKLM, PnPPolicy, "NoWarningNoElevationOnInstall", 1),
                    new RegValue(HKLM, PnPPolicy, "UpdatePromptSettings", 2),
                },
            });

            // ============================== BOTH ==============================

            list.Add(new ActionFix
            {
                Id = "services-core",
                Title = "Print Spooler / Workstation / Server services stopped or disabled",
                Scope = FixScope.Both,
                Recommended = true,
                Description =
                    "Sets these services to Automatic and starts them:\r\n" +
                    " • Spooler (Print Spooler) – required on both sides\r\n" +
                    " • LanmanWorkstation (Workstation) – required to connect to shares\r\n" +
                    " • LanmanServer (Server) – required on the PC that shares the printer",
                State = () =>
                {
                    var names = new[] { "Spooler", "LanmanWorkstation", "LanmanServer" };
                    int ok = names.Count(SystemTools.ServiceIsAutoAndRunning);
                    return ok == names.Length ? FixState.Applied : ok == 0 ? FixState.NotApplied : FixState.Partial;
                },
                ApplyAction = _ =>
                {
                    foreach (var s in new[] { "Spooler", "LanmanWorkstation", "LanmanServer" })
                        SystemTools.EnsureServiceAutoAndRunning(s);
                },
            });

            list.Add(new ActionFix
            {
                Id = "network-discovery",
                Title = "Shared printers / PCs not visible on the network (Network Discovery)",
                Scope = FixScope.Both,
                Description =
                    "Enables the services behind Network Discovery and the matching firewall rules (Domain/Private only):\r\n" +
                    " • FDResPub (Function Discovery Resource Publication)\r\n" +
                    " • fdPHost (Function Discovery Provider Host)\r\n" +
                    " • SSDPSRV (SSDP Discovery), upnphost (UPnP Device Host)\r\n" +
                    " • Firewall group 'Network Discovery'\r\n\r\n" +
                    "Only needed for browsing; connecting by \\\\server\\printer works without it.",
                State = () =>
                {
                    var names = new[] { "FDResPub", "fdPHost", "SSDPSRV", "upnphost" };
                    int ok = names.Count(SystemTools.ServiceIsAutoAndRunning);
                    var (en, total) = SystemTools.FirewallGroupState(SystemTools.FirewallGroupNetworkDiscovery);
                    bool fw = total > 0 && en == total;
                    if (ok == names.Length && fw) return FixState.Applied;
                    return ok == 0 && en == 0 ? FixState.NotApplied : FixState.Partial;
                },
                ApplyAction = _ =>
                {
                    foreach (var s in new[] { "FDResPub", "fdPHost", "SSDPSRV", "upnphost" })
                        SystemTools.EnsureServiceAutoAndRunning(s);
                    int n = SystemTools.EnableFirewallGroup(SystemTools.FirewallGroupNetworkDiscovery, true);
                    Logger.Info($"  enabled {n} 'Network Discovery' firewall rule(s)");
                },
                RevertAction = () =>
                {
                    int n = SystemTools.EnableFirewallGroup(SystemTools.FirewallGroupNetworkDiscovery, false);
                    Logger.Info($"  disabled {n} 'Network Discovery' firewall rule(s) (services left running)");
                },
            });

            // ============================== SERVER ==============================

            list.Add(new ActionFix
            {
                Id = "server-firewall",
                Title = "Clients can't reach the shared printer (File and Printer Sharing firewall rules)",
                Scope = FixScope.Server,
                Recommended = true,
                Description =
                    "Enables the built-in Windows Firewall group 'File and Printer Sharing' for the Domain and Private " +
                    "profiles (SMB 445, spooler RPC, etc.). Language-independent – works on non-English Windows.\r\n\r\n" +
                    "Run this on the PC/server that SHARES the printer.",
                State = () =>
                {
                    var (en, total) = SystemTools.FirewallGroupState(SystemTools.FirewallGroupFilePrinterSharing);
                    if (total == 0) return FixState.Unknown;
                    return en == total ? FixState.Applied : en == 0 ? FixState.NotApplied : FixState.Partial;
                },
                ApplyAction = _ =>
                {
                    int n = SystemTools.EnableFirewallGroup(SystemTools.FirewallGroupFilePrinterSharing, true);
                    Logger.Info($"  enabled {n} 'File and Printer Sharing' firewall rule(s)");
                },
                RevertAction = () =>
                {
                    int n = SystemTools.EnableFirewallGroup(SystemTools.FirewallGroupFilePrinterSharing, false);
                    Logger.Info($"  disabled {n} 'File and Printer Sharing' firewall rule(s)");
                },
            });

            list.Add(new RegistryFix
            {
                Id = "server-remote-endpoint",
                Title = "Spooler refuses remote connections (PrintNightmare 'Allow client connections' = disabled)",
                Scope = FixScope.Server,
                Recommended = true,
                RestartSpoolerAfter = true,
                Description =
                    "A common PrintNightmare mitigation disables the spooler's remote endpoint, which silently breaks " +
                    "printer sharing (clients get 0x00000709 / 'RPC server unavailable').\r\n\r\n" +
                    "Sets 'Allow Print Spooler to accept client connections' = Enabled:\r\n" +
                    "Registry: HKLM\\" + PrintersPolicy + "\\RegisterSpoolerRemoteRpcEndPoint = 1\r\n" +
                    "The spooler is restarted afterwards. If a GPO sets this to Disabled, fix the GPO instead.",
                Values = { new RegValue(HKLM, PrintersPolicy, "RegisterSpoolerRemoteRpcEndPoint", 1) },
            });

            list.Add(new RegistryFix
            {
                Id = "server-rpc-listener",
                Title = "Older / mixed clients fail to connect (RPC listener: accept TCP + named pipes)",
                Scope = FixScope.Server,
                Recommended = true,
                RestartSpoolerAfter = true,
                Description =
                    "Makes the sharing PC's spooler listen on BOTH RPC over TCP and RPC over named pipes, with Negotiate " +
                    "authentication, so Windows 10, Windows 11 22H2+ and older clients can all connect.\r\n\r\n" +
                    "Registry: HKLM\\" + RpcPolicy + "\r\n" +
                    "  RpcProtocols = 7 (TCP + named pipes)\r\n" +
                    "  ForceKerberosForRpc = 0 (Negotiate – needed for workgroup / IP connections)\r\n" +
                    "The spooler is restarted afterwards.",
                Values =
                {
                    new RegValue(HKLM, RpcPolicy, "RpcProtocols", 7),
                    new RegValue(HKLM, RpcPolicy, "ForceKerberosForRpc", 0),
                },
            });

            list.Add(new RegistryFix
            {
                Id = "server-011b",
                Title = "Error 0x0000011b (legacy workaround on the sharing PC: RpcAuthnLevelPrivacyEnabled = 0)",
                Scope = FixScope.Server,
                RestartSpoolerAfter = true,
                SecurityNote =
                    "This lowers RPC authentication on the print server (CVE-2021-1678 hardening). " +
                    "On fully patched Windows it may be ignored. Prefer the client-side named-pipes fix.",
                Description =
                    "The classic 0x0000011b workaround applied on the PC/server that SHARES the printer. It disables the " +
                    "RPC privacy authentication level requirement introduced by the January 2021+ updates.\r\n\r\n" +
                    "Registry: HKLM\\" + PrintControl + "\\RpcAuthnLevelPrivacyEnabled = 0\r\n\r\n" +
                    "Recent cumulative updates enforce the hardening regardless of this value – if it doesn't help, use " +
                    "'client-rpc-namedpipes' on the clients and make sure server and clients are fully updated.",
                Values = { new RegValue(HKLM, PrintControl, "RpcAuthnLevelPrivacyEnabled", 0) },
            });

            return list;
        }

        private static bool DeviceValueIsStale()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(UserWindows))
            {
                var device = k?.GetValue("Device") as string;
                if (string.IsNullOrEmpty(device)) return false;
                var name = device.Split(',')[0];
                try
                {
                    return !PrinterService.GetPrinters().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                }
                catch { return false; }
            }
        }
    }
}
