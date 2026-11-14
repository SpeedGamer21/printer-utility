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

        private bool UseCredentials => CredBox.IsChecked == true;

        /// <summary>The entered user/password (or nulls), clearing the password box.</summary>
        public (string user, string password) TakeCredentials()
        {
            if (!UseCredentials) return (null, null);
            var result = (CredUser.Text.Trim(), CredPassword.Password);
            CredPassword.Clear();
            return result;
        }

        public bool Validate()
        {
            var p = UncPath;
            bool ok = p.StartsWith(@"\\") && p.TrimStart('\\').IndexOf('\\') > 0 && !p.EndsWith(@"\");
            PathError.Text = @"Enter a path like \\server\printer, or pick a printer from the list.";
            PathError.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            if (!ok) { Path.Focus(); return false; }
            return ValidateCredentials();
        }

        private bool ValidateCredentials()
        {
            string error = null;
            if (UseCredentials && CredUser.Text.Trim().Length == 0) { error = "Enter the user name."; CredUser.Focus(); }
            else if (UseCredentials && CredPassword.Password.Length == 0) { error = "Enter the password (accounts without one can't log on over the network)."; CredPassword.Focus(); }
            CredError.Text = error;
            CredError.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            return error == null;
        }

        private void CredBox_Changed(object sender, RoutedEventArgs e)
        {
            CredFields.Visibility = UseCredentials ? Visibility.Visible : Visibility.Collapsed;
            CredError.Visibility = Visibility.Collapsed;
            if (UseCredentials) CredUser.Focus();
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
                if (UseCredentials)
                {
                    if (!ValidateCredentials()) { Status.Text = "Enter the user name and password first."; return; }
                    string user = CredUser.Text.Trim(), password = CredPassword.Password;
                    Status.Text = $"Signing in to {server}…";
                    if (!await Task.Run(() => Credentials.Save(server, user, password)))
                    {
                        Status.Text = $"{server} didn't accept that user name or password. Details are in Activity.";
                        return;
                    }
                    Status.Text = $"Looking for shared printers on {server}…";
                }
                var printers = await Task.Run(() => PrinterService.GetServerPrinters(server));
                Shares.ItemsSource = printers.Select(p => new ShareRowVm { ShareName = p.ShareName, Name = p.UncPath(unc) + "   •   " + p.Driver }).ToList();
                Status.Visibility = printers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                Status.Text = "The server answered, but it doesn't share any printers.";
                if (printers.Count > 0 && UncPath.Length == 0) Path.Text = unc + @"\";
            }
            catch (Exception ex)
            {
                int code = (ex as Win32Exception)?.NativeErrorCode ?? 0;
                if (Credentials.IsCredentialError(code) && !UseCredentials)
                {
                    Status.Text = $"{server} didn't accept your Windows account. Tick 'Connect with a different user name " +
                                  "and password' below, enter an account of that PC, and browse again.";
                    CredBox.IsChecked = true;
                    return;
                }
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
