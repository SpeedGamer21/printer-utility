using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PrinterUtility
{
    public partial class AddPrinterPanel : UserControl
    {
        public AddPrinterPanel(string server)
        {
            InitializeComponent();
            Server.Text = server ?? "";
            if (!string.IsNullOrWhiteSpace(server)) Loaded += async (s, e) => await Browse();
        }

        public string UncPath => Path.Text.Trim();
        public bool SetAsDefault => DefaultBox.IsChecked == true;
        public bool AllUsers => AllUsersBox.IsChecked == true;

        public bool Validate()
        {
            var p = UncPath;
            bool ok = p.StartsWith(@"\\") && p.TrimStart('\\').IndexOf('\\') > 0 && !p.EndsWith(@"\");
            PathError.Text = @"Enter a path like \\server\printer, or pick a printer from the list.";
            PathError.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok) Path.Focus();
            return ok;
        }

        private async void Browse_Click(object sender, RoutedEventArgs e) => await Browse();

        private async void Server_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await Browse();
            }
        }

        private async Task Browse()
        {
            var server = Server.Text.Trim();
            if (server.Length == 0) { Server.Focus(); return; }
            BrowseBtn.IsEnabled = false;
            Shares.ItemsSource = null;
            Status.Visibility = Visibility.Visible;
            Status.Text = $"Looking for shared printers on {server}…";
            try
            {
                var unc = PrinterService.NormalizeServer(server);
                var printers = await Task.Run(() => PrinterService.GetServerPrinters(server));
                Shares.ItemsSource = printers.Select(p => new ShareRowVm { ShareName = p.ShareName, Name = p.UncPath(unc) + "   •   " + p.Driver }).ToList();
                Status.Visibility = printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                Status.Text = "The server answered, but it doesn't share any printers.";
                if (printers.Count > 0 && UncPath.Length == 0) Path.Text = unc + @"\";
            }
            catch (Exception ex)
            {
                int code = (ex as Win32Exception)?.NativeErrorCode ?? 0;
                Status.Text = "Could not list printers: " + (code != 0 ? PrinterService.Describe(code) : ex.Message) +
                              "\nTip: Tools → Diagnose a print server.";
            }
            finally
            {
                BrowseBtn.IsEnabled = true;
            }
        }

        private void Shares_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Shares.SelectedItem is ShareRowVm row)
            {
                Path.Text = PrinterService.NormalizeServer(Server.Text) + @"\" + row.ShareName;
                PathError.Visibility = Visibility.Collapsed;
            }
        }

        private void Shares_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // Enter / Add button closes the dialog; double-click just makes the choice obvious.
            if (Shares.SelectedItem != null) DefaultBox.Focus();
        }
    }
}
