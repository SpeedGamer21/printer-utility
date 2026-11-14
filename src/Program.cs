using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace PrinterUtility
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--elevated")
                return Elevation.RunHelper(args);

            if (args.Length > 0 && args[0] == "--demo")
                Session.EnableDemo(args.Length > 1 ? args[1] : null);   // sample data for screenshots; changes nothing
            else if (args.Length > 0)
                return Cli(args);

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources.MergedDictionaries.Add((ResourceDictionary)Application.LoadComponent(
                new Uri("/PrinterUtility;component/Themes/Styles.xaml", UriKind.Relative)));
            Theme.Apply(app.Resources);
            app.DispatcherUnhandledException += (s, e) =>
            {
                Logger.Error(e.Exception.Message);
                e.Handled = true;
            };
            try
            {
                return app.Run(new MainWindow());
            }
            catch (Exception ex)
            {
                var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PrinterUtility-crash.log");
                try { System.IO.File.WriteAllText(path, ex.ToString()); } catch { /* ignore */ }
                MessageBox.Show("Printer Utility could not start:\n\n" + ex.GetBaseException().Message + "\n\nDetails: " + path,
                    "Printer Utility", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }

        private const string Help =
@"Printer Utility – command line

  PrinterUtility.exe                          open the GUI
  PrinterUtility.exe --list                   list installed printers (* = default)
  PrinterUtility.exe --set-default ""Name""     set the default printer for the current user
  PrinterUtility.exe --add \\server\printer [--default]
                                              connect a shared printer for the current user
  PrinterUtility.exe --remove \\server\printer
  PrinterUtility.exe --list-fixes             list available fixes and their status
  PrinterUtility.exe --fix id[,id...] [--param ""value""]
                                              apply fixes (admin fixes need an elevated prompt)
  PrinterUtility.exe --revert id[,id...]
  PrinterUtility.exe --restart-spooler        (admin)
  PrinterUtility.exe --clear-spooler          stop spooler, delete stuck jobs, start (admin)
  PrinterUtility.exe --add-all-users \\server\printer    per-machine connection (admin)
  PrinterUtility.exe --share ""Printer name"" ShareName [--publish] [--prepare]
                                              share a local printer (admin); --prepare also applies
                                              the recommended print-server fixes
  PrinterUtility.exe --unshare ""Printer name""   (admin)
  PrinterUtility.exe --save-credential server user --password ""pw""
                                              save the sharing PC's account for the CURRENT user
                                              (a plain user becomes server\user), then test it
  PrinterUtility.exe --list-credentials        saved Windows credentials for servers (current user)
  PrinterUtility.exe --remove-credential server
";

        private static int Cli(string[] args)
        {
            Native.AttachConsole(Native.ATTACH_PARENT_PROCESS);
            Console.WriteLine();
            Logger.Line += Console.WriteLine;

            string Arg(int i) => i < args.Length ? args[i] : throw new ArgumentException($"Missing value after {args[i - 1]}");
            bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            string Param()
            {
                int i = Array.FindIndex(args, a => string.Equals(a, "--param", StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? Arg(i + 1) : null;
            }

            try
            {
                switch (args[0].ToLowerInvariant())
                {
                    case "--list":
                        foreach (var p in PrinterService.GetPrinters())
                            Console.WriteLine($"{(p.IsDefault ? "*" : " ")} {p.Name,-50} {p.TypeText,-15} {p.StatusText}");
                        return 0;

                    case "--set-default":
                        PrinterService.SetDefaultPrinter(Arg(1));
                        Logger.Ok($"Default printer: {Arg(1)}");
                        return 0;

                    case "--add":
                        PrinterService.AddConnection(Arg(1));
                        Logger.Ok($"Connected: {Arg(1)}");
                        if (Has("--default")) { PrinterService.SetDefaultPrinter(Arg(1)); Logger.Ok("Set as default."); }
                        return 0;

                    case "--remove":
                        PrinterService.RemoveConnection(Arg(1));
                        Logger.Ok($"Removed: {Arg(1)}");
                        return 0;

                    case "--list-fixes":
                        foreach (var f in FixCatalog.All)
                            Console.WriteLine($"{f.Id,-26} {f.Scope,-7} {(f.RequiresAdmin ? "admin" : "user "),-6} {f.SafeState(),-11} {f.Title}");
                        return 0;

                    case "--fix":
                    {
                        var p = Param();
                        var ids = Arg(1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var id in ids)
                        {
                            var f = FixCatalog.Find(id);
                            if (f?.ParameterPrompt != null && string.IsNullOrWhiteSpace(p))
                                throw new ArgumentException($"Fix '{id}' needs --param. {f.ParameterPrompt}");
                        }
                        return ActionRunner.Run(ids.Select(id => FixCatalog.Find(id)?.ParameterPrompt != null ? $"fix:{id}={p}" : $"fix:{id}"));
                    }

                    case "--revert":
                        return ActionRunner.Run(Arg(1).Split(',').Select(id => "revert:" + id));

                    case "--restart-spooler":
                        return ActionRunner.Run(new[] { "spooler:restart" });

                    case "--clear-spooler":
                        return ActionRunner.Run(new[] { "spooler:clear" });

                    case "--add-all-users":
                        return ActionRunner.Run(new[] { "machine-add:" + Arg(1) });

                    case "--share":
                    {
                        var actions = new List<string>();
                        if (Has("--prepare")) actions.AddRange(PrintServerPrep.Actions);
                        actions.Add($"share:{Arg(1)}\\{Arg(2)}\\{(Has("--publish") ? 1 : 0)}");
                        return ActionRunner.Run(actions);
                    }

                    case "--unshare":
                        return ActionRunner.Run(new[] { "unshare:" + Arg(1) });

                    case "--save-credential":
                    {
                        // per-user: runs in this process, never through the elevated helper
                        int i = Array.FindIndex(args, a => string.Equals(a, "--password", StringComparison.OrdinalIgnoreCase));
                        if (i < 0) throw new ArgumentException("Missing --password.");
                        return Credentials.Save(Arg(1), Arg(2), Arg(i + 1)) ? 0 : 1;
                    }

                    case "--list-credentials":
                        foreach (var c in Credentials.List()) Console.WriteLine($"{c.Target,-40} {c.User}");
                        return 0;

                    case "--remove-credential":
                        Credentials.Remove(Arg(1));
                        return 0;

                    default:
                        Console.WriteLine(Help);
                        return args[0] == "--help" || args[0] == "/?" ? 0 : 2;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                return 1;
            }
        }
    }
}
