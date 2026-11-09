using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PrinterUtility
{
    public partial class ToolsPage : UserControl, IPage
    {
        private readonly ObservableCollection<LogEntry> _results = new ObservableCollection<LogEntry>();
        private static MainWindow Shell => MainWindow.Shell;

        public ToolsPage()
        {
            InitializeComponent();
            Results.ItemsSource = _results;
        }

        public void OnNavigatedTo(object arg) { }

        private async void RestartSpooler_Click(object sender, RoutedEventArgs e) =>
            await Shell.Busy(() => Shell.RunAdmin("Print Spooler restarted.", "spooler:restart"));

        private async void ClearSpooler_Click(object sender, RoutedEventArgs e)
        {
            if (!await Shell.Confirm("Clear all stuck print jobs?",
                    "The spooler is stopped and every pending print job on this PC is deleted, for all users.", "Clear jobs", danger: true))
                return;
            await Shell.Busy(() => Shell.RunAdmin("Stuck jobs cleared.", "spooler:clear"));
        }

        private async void Server_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { e.Handled = true; await Diagnose(); }
        }

        private async void Diagnose_Click(object sender, RoutedEventArgs e) => await Diagnose();

        private async Task Diagnose()
        {
            var server = Server.Text.Trim();
            if (server.Length == 0) { Server.Focus(); return; }
            _results.Clear();

            void Capture(string line)
            {
                var entry = LogEntry.Parse(line);
                if (entry.Message.StartsWith("===")) return;
                Dispatcher.BeginInvoke((Action)(() => _results.Add(entry)));
            }

            await Shell.Busy(async () =>
            {
                Logger.Line += Capture;
                try { await Task.Run(() => SystemTools.DiagnoseServer(server)); }
                finally { Logger.Line -= Capture; }
            });

            await Dispatcher.InvokeAsync(() =>
            {
                int errors = _results.Count(r => r.Tone == Tone.Error);
                if (errors == 0) Shell.Notify(Tone.Success, $"{server} looks healthy.");
                else Shell.Notify(Tone.Warning, $"{errors} problem(s) found with {server}.");
            });
        }

        private async void Browse_Click(object sender, RoutedEventArgs e) => await PrintersPage.AddPrinter(Server.Text.Trim());

        private async void Gpupdate_Click(object sender, RoutedEventArgs e) =>
            await Shell.Busy(async () =>
            {
                await Task.Run(() => SystemTools.RunAndLog("gpupdate.exe", "/force"));
                Shell.Notify(Tone.Success, "Group Policy refreshed. Details are in Activity.", "View", () => Shell.Navigate("activity"));
            });

        private async void FlushDns_Click(object sender, RoutedEventArgs e) =>
            await Shell.Busy(async () =>
            {
                await Task.Run(() => SystemTools.RunAndLog("ipconfig.exe", "/flushdns"));
                Shell.Notify(Tone.Success, "DNS cache flushed.");
            });

        private void CopyList_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var sb = new StringBuilder("Default\tName\tType\tStatus\tJobs\tPort/Server\tDriver\tShare\r\n");
                foreach (var p in PrinterService.GetPrinters())
                    sb.AppendLine(string.Join("\t", p.IsDefault ? "*" : "", p.Name, p.TypeText, p.StatusText, p.Jobs,
                        p.IsNetwork ? p.ServerName : p.Port, p.Driver, p.IsShared && !p.IsNetwork ? p.LocalUnc : ""));
                MainWindow.CopyText(sb.ToString(), "Printer list");
            }
            catch (Exception ex) { Shell.Notify(Tone.Error, ex.Message); }
        }

        private void PrintMgmt_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start("printmanagement.msc"); }
            catch (Exception ex)
            {
                Logger.Error("printmanagement.msc: " + ex.Message);
                Shell.Notify(Tone.Error, "Print Management isn't available on this edition of Windows.");
            }
        }
    }
}
