using System;
using System.Collections.Generic;

namespace PrinterUtility
{
    /// <summary>
    /// Executes actions described as strings. Used in-process and by the elevated helper
    /// (PrinterUtility.exe --elevated --log file --do action ...).
    ///
    ///   fix:&lt;id&gt;[=param]      apply a fix
    ///   revert:&lt;id&gt;           revert a fix
    ///   spooler:restart
    ///   spooler:clear          stop spooler, delete stuck jobs, start
    ///   purge:&lt;printer&gt;       cancel all jobs of one printer
    ///   machine-add:&lt;unc&gt;     add printer connection for all users (+ spooler restart)
    ///   machine-remove:&lt;unc&gt;
    ///   delete-local:&lt;name&gt;   delete a local printer
    ///   share:&lt;printer&gt;\&lt;share&gt;\&lt;0|1 publish in AD&gt;
    ///   unshare:&lt;printer&gt;
    ///   netprofile:private     switch connected Public networks to Private
    /// </summary>
    internal static class ActionRunner
    {
        public static int Run(IEnumerable<string> actions)
        {
            if (Session.IsDemo)
            {
                foreach (var a in actions) Logger.Ok("[demo] would run: " + a);
                return 0;
            }

            int failures = 0;
            bool restartSpooler = false;

            foreach (var action in actions)
            {
                int colon = action.IndexOf(':');
                string verb = colon < 0 ? action : action.Substring(0, colon);
                string arg = colon < 0 ? "" : action.Substring(colon + 1);

                try
                {
                    switch (verb.ToLowerInvariant())
                    {
                        case "fix":
                        {
                            string id = arg, param = null;
                            int eq = arg.IndexOf('=');
                            if (eq >= 0) { id = arg.Substring(0, eq); param = arg.Substring(eq + 1); }
                            var fix = FixCatalog.Find(id) ?? throw new ArgumentException($"Unknown fix '{id}'.");
                            if (fix.RequiresAdmin && !SystemTools.IsAdmin) throw new UnauthorizedAccessException("Administrator rights required.");
                            Logger.Info($"Applying: {fix.Title}");
                            fix.Apply(param);
                            restartSpooler |= fix.RestartSpoolerAfter;
                            Logger.Ok($"Applied [{fix.Id}]");
                            break;
                        }
                        case "revert":
                        {
                            var fix = FixCatalog.Find(arg) ?? throw new ArgumentException($"Unknown fix '{arg}'.");
                            if (!fix.CanRevert) { Logger.Warn($"[{fix.Id}] has nothing to revert – skipped."); break; }
                            Logger.Info($"Reverting: {fix.Title}");
                            fix.Revert();
                            restartSpooler |= fix.RestartSpoolerAfter;
                            Logger.Ok($"Reverted [{fix.Id}]");
                            break;
                        }
                        case "spooler":
                            if (arg == "restart") SystemTools.RestartSpooler();
                            else if (arg == "clear") SystemTools.ClearSpoolFolder();
                            else throw new ArgumentException($"Unknown spooler action '{arg}'.");
                            break;
                        case "purge":
                            PrinterService.PurgeQueue(arg);
                            Logger.Ok($"All jobs cancelled on \"{arg}\".");
                            break;
                        case "machine-add":
                        {
                            Logger.Info($"Adding {arg} for all users of this PC...");
                            int code = PrinterService.AddMachineConnection(arg);
                            if (code != 0) throw new InvalidOperationException($"printui /ga returned {code}.");
                            restartSpooler = true;
                            Logger.Ok($"Per-machine connection added: {arg} (users see it after spooler restart / next logon).");
                            break;
                        }
                        case "machine-remove":
                        {
                            Logger.Info($"Removing per-machine connection {arg}...");
                            int code = PrinterService.RemoveMachineConnection(arg);
                            if (code != 0) throw new InvalidOperationException($"printui /gd returned {code}.");
                            restartSpooler = true;
                            Logger.Ok($"Per-machine connection removed: {arg}");
                            break;
                        }
                        case "share":
                        {
                            // share:<printer>\<share name>\<publish 0|1>  ('\' is illegal in local printer and share names)
                            var parts = arg.Split('\\');
                            if (parts.Length != 3) throw new ArgumentException("Expected share:<printer>\\<share>\\<0|1>");
                            var error = PrinterService.ValidateShareName(parts[1], parts[0]);
                            if (error != null) throw new ArgumentException(error);
                            Logger.Info($"Sharing \"{parts[0]}\" as \"{parts[1]}\"...");
                            PrinterService.SetSharing(parts[0], true, parts[1], parts[2] == "1");
                            Logger.Ok($"Shared: \\\\{Environment.MachineName}\\{parts[1].Trim()}");
                            break;
                        }
                        case "unshare":
                            Logger.Info($"Stopping sharing of \"{arg}\"...");
                            PrinterService.SetSharing(arg, false, null, false);
                            Logger.Ok($"\"{arg}\" is no longer shared.");
                            break;
                        case "netprofile":
                            if (arg != "private") throw new ArgumentException("Only netprofile:private is supported.");
                            int n = SystemTools.SetPublicNetworksPrivate();
                            Logger.Ok(n == 0 ? "No Public networks to change." : $"{n} network(s) switched to Private.");
                            break;
                        case "delete-local":
                            PrinterService.DeleteLocalPrinter(arg);
                            Logger.Ok($"Printer deleted: {arg}");
                            break;
                        default:
                            throw new ArgumentException($"Unknown action '{action}'.");
                    }
                }
                catch (Exception ex)
                {
                    failures++;
                    Logger.Error($"{action}: {ex.Message}");
                }
            }

            if (restartSpooler)
            {
                try { SystemTools.RestartSpooler(); }
                catch (Exception ex) { failures++; Logger.Error("Spooler restart failed: " + ex.Message); }
            }
            return failures == 0 ? 0 : 1;
        }
    }
}
