using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PrinterUtility
{
    public partial class PrintersPage : UserControl, IPage
    {
        private List<PrinterVm> _all = new List<PrinterVm>();
        private static MainWindow Shell => MainWindow.Shell;

        public PrintersPage()
        {
            InitializeComponent();
            Subtitle.Text = $"Printers for {Session.UserName} on {Session.MachineName}. Changes to the default printer apply to this user only.";
            PreviewKeyDown += (s, e) => { if (e.Key == Key.F5) { Reload(); e.Handled = true; } };
        }

        public void OnNavigatedTo(object arg) => Reload();

        private PrinterVm Selected => List.SelectedItem as PrinterVm;

        internal void Reload()
        {
            string keep = Selected?.Name;
            try
            {
                _all = PrinterService.GetPrinters().Select(p => new PrinterVm(p)).ToList();
            }
            catch (Exception ex)
            {
                _all = new List<PrinterVm>();
                Logger.Error("Could not list printers: " + ex.Message);
                Shell.Notify(Tone.Error, "Could not list printers – is the Print Spooler running?", "Open Tools", () => Shell.Navigate("tools"));
            }
            ApplyFilter();
            List.SelectedItem = _all.FirstOrDefault(p => p.Name == keep) ?? _all.FirstOrDefault(p => p.IsDefault);

            try { ManagedToggle.IsChecked = PrinterService.IsWindowsManagingDefault(); } catch { /* ignore */ }
        }

        private void ApplyFilter()
        {
            var q = Search.Text.Trim();
            var items = q.Length == 0 ? _all
                : _all.Where(p => p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  p.Subtitle.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var keep = Selected;
            List.ItemsSource = items;
            if (keep != null && items.Contains(keep)) List.SelectedItem = keep;
            EmptyList.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyList.Text = _all.Count == 0 ? "No printers installed. Click Add printer to connect one." : "No printers match your search.";
        }

        private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetail();

        private void ShowDetail()
        {
            var p = Selected;
            EmptyDetail.Visibility = p == null ? Visibility.Visible : Visibility.Collapsed;
            Detail.Visibility = p == null ? Visibility.Collapsed : Visibility.Visible;
            if (p == null) return;

            DGlyph.Text = p.Glyph;
            DName.Text = p.Name;
            DDefaultPill.Visibility = p.IsDefault ? Visibility.Visible : Visibility.Collapsed;
            DStatusPill.Tag = p.StatusTone;
            DStatus.Text = p.StatusText;
            BtnDefault.IsEnabled = !p.IsDefault;
            BtnDefault.Content = p.IsDefault ? "This is your default" : "Set as default";
            BtnShare.Visibility = p.Info.IsNetwork ? Visibility.Collapsed : Visibility.Visible;
            BtnShare.Content = p.IsShared ? "Sharing settings" : "Share on the network";

            var i = p.Info;
            var rows = new List<(string, string)>
            {
                ("Type", i.TypeText),
                (i.IsNetwork ? "Server" : "Port", i.IsNetwork ? i.ServerName : i.Port),
                ("Driver", i.Driver),
                ("Jobs in queue", i.Jobs.ToString()),
            };
            if (p.IsShared) rows.Add(("Shared as", i.LocalUnc));
            if (!string.IsNullOrWhiteSpace(i.Location)) rows.Add(("Location", i.Location));
            if (!string.IsNullOrWhiteSpace(i.Comment)) rows.Add(("Comment", i.Comment));

            Props.Children.Clear();
            Props.RowDefinitions.Clear();
            foreach (var (label, value) in rows)
            {
                Props.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 4, 16, 4) };
                var v = new TextBlock { Text = string.IsNullOrEmpty(value) ? "—" : value, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
                Grid.SetRow(l, Props.RowDefinitions.Count - 1);
                Grid.SetRow(v, Props.RowDefinitions.Count - 1);
                Grid.SetColumn(v, 1);
                Props.Children.Add(l);
                Props.Children.Add(v);
            }
        }

        // ------------------------------------------------------------ actions

        private async void SetDefault_Click(object sender, RoutedEventArgs e) => await SetDefault();

        private async void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Selected != null && !Selected.IsDefault) await SetDefault();
        }

        private async void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Selected != null) await SetDefault();
            else if (e.Key == Key.Delete && Selected != null) Remove_Click(sender, e);
        }

        private async Task SetDefault()
        {
            var p = Selected;
            if (p == null) return;
            await Shell.Busy(async () =>
            {
                try
                {
                    await Task.Run(() => PrinterService.SetDefaultPrinter(p.Name));
                    Logger.Ok($"Default printer set to \"{p.Name}\"");
                    Shell.Notify(Tone.Success, $"\"{p.Name}\" is now your default printer.");
                }
                catch (Exception ex)
                {
                    Logger.Error(ex.Message);
                    bool is709 = ex is Win32Exception w && w.NativeErrorCode == 0x709;
                    Shell.Notify(Tone.Error, is709 ? "Windows refused to change the default (0x00000709)." : ex.Message,
                        is709 ? "Fix it" : null, is709 ? (Action)(() => Shell.Navigate("fixes", "client-default-0x709")) : null);
                }
                Reload();
            });
        }

        private async void ManagedToggle_Click(object sender, RoutedEventArgs e)
        {
            bool on = ManagedToggle.IsChecked == true;
            try
            {
                PrinterService.SetWindowsManagesDefault(on);
                Logger.Ok("\"Let Windows manage my default printer\" " + (on ? "on" : "off"));
                Shell.Notify(Tone.Success, on ? "Windows will now manage your default printer." : "Your default printer will stay as you set it.");
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                Shell.Notify(Tone.Error, ex.Message);
            }
            await Task.CompletedTask;
        }

        private void Open(Action<string> action)
        {
            var p = Selected;
            if (p == null) return;
            try { action(p.Name); }
            catch (Exception ex) { Logger.Error(ex.Message); Shell.Notify(Tone.Error, ex.Message); }
        }

        private void TestPage_Click(object sender, RoutedEventArgs e)
        {
            Open(PrinterService.PrintTestPage);
            Shell.Notify(Tone.Success, "Test page sent.");
        }

        private void Queue_Click(object sender, RoutedEventArgs e) => Open(PrinterService.OpenQueue);
        private void Prefs_Click(object sender, RoutedEventArgs e) => Open(PrinterService.OpenPreferences);
        private void Props_Click(object sender, RoutedEventArgs e) => Open(PrinterService.OpenProperties);

        private void Share_Click(object sender, RoutedEventArgs e)
        {
            if (Selected != null) Shell.Navigate("share", Selected.Name);
        }

        private async void Purge_Click(object sender, RoutedEventArgs e)
        {
            var p = Selected;
            if (p == null) return;
            if (!await Shell.Confirm("Cancel all print jobs?", $"Every document waiting in the queue of \"{p.Name}\" will be cancelled.", "Cancel all jobs", danger: true))
                return;

            await Shell.Busy(async () =>
            {
                try
                {
                    await Task.Run(() => PrinterService.PurgeQueue(p.Name));
                    Logger.Ok($"All jobs cancelled on \"{p.Name}\"");
                    Shell.Notify(Tone.Success, "All jobs cancelled.");
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 5 && !SystemTools.IsAdmin && !p.Info.IsNetwork)
                {
                    await Shell.RunAdmin("All jobs cancelled.", "purge:" + p.Name);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex.Message);
                    Shell.Notify(Tone.Error, p.Info.IsNetwork
                        ? "Only users with 'Manage documents' rights on the print server can clear a network queue."
                        : ex.Message);
                }
                Reload();
            });
        }

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            var p = Selected;
            if (p == null) return;
            if (!await Shell.Confirm("Remove printer?", $"\"{p.Name}\" will be removed from this PC" +
                    (p.Info.IsNetwork ? " for your account." : " for all users (requires admin)."), "Remove", danger: true))
                return;

            await Shell.Busy(async () =>
            {
                if (p.Info.IsNetwork)
                {
                    try
                    {
                        await Task.Run(() => PrinterService.RemoveConnection(p.Name));
                        Logger.Ok($"Removed connection {p.Name}");
                        Shell.Notify(Tone.Success, $"\"{p.Name}\" removed.");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn(ex.Message);
                        if (await Shell.Confirm("Remove for all users?",
                                "This looks like a per-machine printer (installed for all users). Remove it for everyone as administrator?", "Remove for all users", danger: true))
                            await Shell.RunAdmin($"\"{p.Name}\" removed for all users.", "machine-remove:" + p.Name);
                    }
                }
                else
                {
                    await Shell.RunAdmin($"\"{p.Name}\" removed.", "delete-local:" + p.Name);
                }
                Reload();
            });
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            Reload();
            Shell.Notify(Tone.Success, "Printer list refreshed.");
        }

        private async void Add_Click(object sender, RoutedEventArgs e) => await AddPrinter(null);

        internal static async Task AddPrinter(string server)
        {
            var panel = new AddPrinterPanel(server);
            if (!await Shell.ShowDialog("Add a network printer", panel, "Add printer", "Cancel", panel.Validate)) return;

            string unc = panel.UncPath;
            bool makeDefault = panel.SetAsDefault;
            bool allUsers = panel.AllUsers;
            var (credUser, credPassword) = panel.TakeCredentials();

            await Connect(unc, makeDefault, allUsers, credUser, credPassword);
        }

        /// <summary>Connects the printer; on a credential error offers to save credentials and retries.</summary>
        private static async Task Connect(string unc, bool makeDefault, bool allUsers, string credUser = null, string credPassword = null)
        {
            bool retryWithCredentials = false;
            await Shell.Busy(async () =>
            {
                bool ok;
                if (credUser != null)
                {
                    // saved for the logged-on user before the spooler connects, so the connection uses it
                    if (!await Task.Run(() => Credentials.Save(unc, credUser, credPassword)))
                    {
                        Shell.NotifyFailure($"{Credentials.HostOf(unc)} didn't accept those credentials.");
                        return;
                    }
                }

                if (allUsers)
                {
                    ok = await Elevation.RunAdminAsync(new[] { "machine-add:" + unc });
                }
                else
                {
                    Logger.Info($"Connecting {unc} (the driver is downloaded from the server – this can take a minute)...");
                    try
                    {
                        await Task.Run(() => PrinterService.AddConnection(unc));
                        Logger.Ok($"Connected: {unc}");
                        ok = true;
                    }
                    catch (Win32Exception ex)
                    {
                        Logger.Error(ex.Message);
                        ok = false;
                        int c = ex.NativeErrorCode;
                        if (c == 5 || c == 0xbcb)
                            Shell.Notify(Tone.Error, "Driver install was blocked. Try again with 'Install for all users', or see the Point and Print fix.",
                                "Open fixes", () => Shell.Navigate("fixes", "client-pointandprint"));
                        else if (Credentials.IsCredentialError(c))
                            retryWithCredentials = true;
                        else if (c == 0x11b || c == 0x709 || c == 0x7c)
                            Shell.Notify(Tone.Error, $"Connection failed (0x{c:X8}). This is usually fixed by the RPC named-pipes fix.",
                                "Fix it", () => Shell.Navigate("fixes", "client-rpc-namedpipes"));
                        else
                            Shell.NotifyFailure("Could not connect the printer.");
                    }
                }

                if (ok)
                {
                    if (makeDefault)
                    {
                        try
                        {
                            await Task.Run(() => PrinterService.SetDefaultPrinter(unc));
                            Logger.Ok($"Default printer set to \"{unc}\"");
                        }
                        catch (Exception ex) { Logger.Warn("Could not set default yet: " + ex.Message); }
                    }
                    Shell.Notify(Tone.Success, allUsers ? $"{unc} installed for all users." : $"{unc} added" + (makeDefault ? " and set as default." : "."));
                }
                else if (allUsers) Shell.NotifyFailure("Could not install the printer.");

                Shell.Navigate("printers");
            });

            // outside Busy: the credential dialog runs its own busy section
            if (retryWithCredentials &&
                await CredentialDialog.ShowAsync(unc, $"{Credentials.HostOf(unc)} didn't accept your Windows account for {unc}."))
                await Connect(unc, makeDefault, allUsers);
        }
    }
}
