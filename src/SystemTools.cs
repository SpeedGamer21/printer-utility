using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;

namespace PrinterUtility
{
    internal static class SystemTools
    {
        public const string FirewallGroupFilePrinterSharing = "@FirewallAPI.dll,-28502";
        public const string FirewallGroupNetworkDiscovery = "@FirewallAPI.dll,-32752";

        public static bool IsAdmin
        {
            get
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
        }

        // ---------------------------------------------------------------- spooler

        public static void RestartSpooler()
        {
            using (var sc = new ServiceController("Spooler"))
            {
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    Logger.Info("Stopping Print Spooler...");
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }
                Logger.Info("Starting Print Spooler...");
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                Logger.Ok("Print Spooler restarted.");
            }
        }

        /// <summary>Stops the spooler, deletes stuck spool files, starts it again.</summary>
        public static void ClearSpoolFolder()
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"spool\PRINTERS");
            using (var sc = new ServiceController("Spooler"))
            {
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    Logger.Info("Stopping Print Spooler...");
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                }

                int deleted = 0, failed = 0;
                if (Directory.Exists(folder))
                {
                    foreach (var f in Directory.GetFiles(folder))
                    {
                        try { File.Delete(f); deleted++; }
                        catch (Exception ex) { failed++; Logger.Warn($"Could not delete {Path.GetFileName(f)}: {ex.Message}"); }
                    }
                }
                Logger.Info($"Deleted {deleted} spool file(s) from {folder}" + (failed > 0 ? $", {failed} failed" : ""));

                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                Logger.Ok("Print Spooler running with an empty queue.");
            }
        }

        // ---------------------------------------------------------------- services

        public static bool ServiceIsAutoAndRunning(string name)
        {
            try
            {
                using (var sc = new ServiceController(name))
                    return sc.Status == ServiceControllerStatus.Running && sc.StartType == ServiceStartMode.Automatic;
            }
            catch (InvalidOperationException) { return false; }
        }

        public static bool ServiceExists(string name) =>
            ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));

        public static void EnsureServiceAutoAndRunning(string name)
        {
            if (!ServiceExists(name))
            {
                Logger.Warn($"Service {name} not found on this PC – skipped.");
                return;
            }
            {
                using (var sc = new ServiceController(name))
                {
                    string display = sc.DisplayName;
                    if (sc.StartType != ServiceStartMode.Automatic)
                    {
                        int code = Run("sc.exe", $"config {name} start= auto", out var output);
                        if (code != 0) throw new InvalidOperationException(output.Trim());
                        Logger.Info($"{display}: startup type set to Automatic.");
                    }
                    sc.Refresh();
                    if (sc.Status != ServiceControllerStatus.Running)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                        Logger.Info($"{display}: started.");
                    }
                    Logger.Ok($"{display} ({name}) is Automatic and running.");
                }
            }
        }

        // ---------------------------------------------------------------- firewall (COM, no PowerShell needed)

        /// <summary>Returns (enabled, total) for rules of a firewall group on the Domain/Private profiles.</summary>
        public static (int enabled, int total) FirewallGroupState(string group)
        {
            int enabled = 0, total = 0;
            ForEachFirewallRule(group, rule =>
            {
                total++;
                if ((bool)rule.Enabled) enabled++;
            });
            return (enabled, total);
        }

        public static int EnableFirewallGroup(string group, bool enable)
        {
            int changed = 0;
            ForEachFirewallRule(group, rule =>
            {
                if ((bool)rule.Enabled != enable)
                {
                    rule.Enabled = enable;
                    changed++;
                }
            });
            return changed;
        }

        private static void ForEachFirewallRule(string group, Action<dynamic> action)
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true);
            dynamic policy = Activator.CreateInstance(type);
            foreach (dynamic rule in policy.Rules)
            {
                string g = rule.Grouping;
                if (!string.Equals(g, group, StringComparison.OrdinalIgnoreCase)) continue;
                int profiles = rule.Profiles;
                // 1 = Domain, 2 = Private, 4 = Public. Skip Public-only rules.
                if ((profiles & 0x3) == 0) continue;
                action(rule);
            }
        }

        // ---------------------------------------------------------------- network profile

        public sealed class NetworkProfile
        {
            public string Name;
            public int Category;    // 0 = Public, 1 = Private, 2 = Domain
            public string CategoryText => Category == 2 ? "Domain" : Category == 1 ? "Private" : "Public";
        }

        private static readonly Guid NetworkListManagerClsid = new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B");

        public static System.Collections.Generic.List<NetworkProfile> GetConnectedNetworks()
        {
            if (Session.IsDemo) return Session.DemoNetworks();
            var list = new System.Collections.Generic.List<NetworkProfile>();
            dynamic nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClsid, true));
            foreach (dynamic n in nlm.GetNetworks(1 /* NLM_ENUM_NETWORK_CONNECTED */))
                list.Add(new NetworkProfile { Name = n.GetName(), Category = n.GetCategory() });
            return list;
        }

        /// <summary>Switches connected Public networks to Private. Requires admin.</summary>
        public static int SetPublicNetworksPrivate()
        {
            int changed = 0;
            dynamic nlm = Activator.CreateInstance(Type.GetTypeFromCLSID(NetworkListManagerClsid, true));
            foreach (dynamic n in nlm.GetNetworks(1))
            {
                if ((int)n.GetCategory() == 0)
                {
                    n.SetCategory(1);
                    Logger.Info($"  network \"{(string)n.GetName()}\" set to Private");
                    changed++;
                }
            }
            return changed;
        }

        public static string DomainName
        {
            get
            {
                try { return IPGlobalProperties.GetIPGlobalProperties().DomainName; }
                catch { return ""; }
            }
        }

        // ---------------------------------------------------------------- misc

        public static int Run(string file, string args, out string output)
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage),
            };
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                p.WaitForExit();
                output = stdout.Result + stderr.Result;
                return p.ExitCode;
            }
        }

        public static void RunAndLog(string file, string args)
        {
            Logger.Info($"Running {file} {args} ...");
            int code = Run(file, args, out var output);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (!string.IsNullOrWhiteSpace(line)) Logger.Info("  " + line.Trim());
            if (code == 0) Logger.Ok($"{file} finished."); else Logger.Warn($"{file} exited with code {code}.");
        }

        /// <summary>Checks name resolution, ping, SMB (445), RPC (135) and lists shared printers.</summary>
        public static void DiagnoseServer(string server)
        {
            var host = server.Trim().TrimStart('\\').TrimEnd('\\');
            if (host.Contains("\\")) host = host.Substring(0, host.IndexOf('\\'));
            Logger.Info($"=== Diagnosing print server '{host}' ===");

            IPAddress[] addrs;
            try
            {
                addrs = Dns.GetHostAddresses(host);
                Logger.Ok("DNS: " + string.Join(", ", addrs.Select(a => a.ToString())));
            }
            catch (Exception ex)
            {
                Logger.Error($"DNS: cannot resolve '{host}': {ex.Message}. Try the FQDN or IP, or flush DNS.");
                return;
            }

            try
            {
                using (var ping = new Ping())
                {
                    var reply = ping.Send(host, 2000);
                    if (reply.Status == IPStatus.Success) Logger.Ok($"Ping: {reply.RoundtripTime} ms");
                    else Logger.Warn($"Ping: {reply.Status} (ICMP may just be blocked)");
                }
            }
            catch (Exception ex) { Logger.Warn("Ping: " + ex.Message); }

            CheckPort(host, 445, "SMB (file & printer sharing)");
            CheckPort(host, 135, "RPC endpoint mapper");

            try
            {
                var printers = PrinterService.GetServerPrinters(host);
                if (printers.Count == 0)
                    Logger.Warn("Spooler answered but no shared printers were found.");
                else
                {
                    Logger.Ok($"Spooler: {printers.Count} shared printer(s):");
                    foreach (var p in printers) Logger.Info($"   \\\\{host}\\{p.ShareName}   ({p.Driver})");
                }
            }
            catch (Exception ex)
            {
                int code = (ex as System.ComponentModel.Win32Exception)?.NativeErrorCode ?? 0;
                Logger.Error("Spooler: " + (code != 0 ? PrinterService.Describe(code) : ex.Message));
            }
            Logger.Info("=== Diagnosis finished ===");
        }

        private static void CheckPort(string host, int port, string label)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var t = c.ConnectAsync(host, port);
                    if (t.Wait(2500) && c.Connected) Logger.Ok($"TCP {port} {label}: open");
                    else Logger.Error($"TCP {port} {label}: no response (firewall on server? service stopped?)");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"TCP {port} {label}: {ex.GetBaseException().Message}");
            }
        }
    }
}
