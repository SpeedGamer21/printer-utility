using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PrinterUtility
{
    /// <summary>
    /// Runs admin-only actions in a short-lived elevated copy of this exe so the main window keeps
    /// running as the logged-on user (important: the default printer is a per-user setting).
    /// </summary>
    internal static class Elevation
    {
        /// <summary>Runs actions as admin. If already elevated, runs in-process.</summary>
        public static async Task<bool> RunAdminAsync(IList<string> actions)
        {
            if (actions.Count == 0) return true;
            if (Session.IsDemo)
            {
                foreach (var a in actions) Logger.Ok("[demo] would run as admin: " + a);
                return true;
            }
            if (SystemTools.IsAdmin)
                return await Task.Run(() => ActionRunner.Run(actions)) == 0;

            string log = Path.Combine(Path.GetTempPath(), $"PrinterUtility-{Guid.NewGuid():N}.log");
            var args = new StringBuilder("--elevated --log ").Append(Quote(log));
            foreach (var a in actions) args.Append(" --do ").Append(Quote(a));

            var psi = new ProcessStartInfo(System.Reflection.Assembly.GetEntryAssembly().Location, args.ToString())
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Logger.Info("Requesting administrator rights (UAC)...");
            try
            {
                int exit = await Task.Run(() =>
                {
                    using (var p = Process.Start(psi))
                    {
                        p.WaitForExit();
                        return p.ExitCode;
                    }
                });
                if (File.Exists(log))
                {
                    foreach (var line in File.ReadAllLines(log)) Logger.Raw(line);
                    try { File.Delete(log); } catch { /* ignore */ }
                }
                else
                {
                    Logger.Warn("The elevated helper produced no log (it may not have been able to write to your TEMP folder).");
                }
                return exit == 0;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == Native.ERROR_CANCELLED)
            {
                Logger.Warn("UAC prompt was cancelled – nothing changed.");
                return false;
            }
        }

        /// <summary>Entry point for the elevated helper process.</summary>
        public static int RunHelper(string[] args)
        {
            string log = null;
            var actions = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--log" && i + 1 < args.Length) log = args[++i];
                else if (args[i] == "--do" && i + 1 < args.Length) actions.Add(args[++i]);
            }
            if (log != null) Logger.UseFile(log);
            try
            {
                return ActionRunner.Run(actions);
            }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString());
                return 1;
            }
            finally
            {
                Logger.CloseFile();
            }
        }

        /// <summary>Quotes a single argument per the MSVCRT / CommandLineToArgvW rules.</summary>
        public static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
                else sb.Append('\\', backslashes).Append(c);
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
