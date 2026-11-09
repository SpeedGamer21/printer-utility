using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PrinterUtility
{
    public partial class SharePage : UserControl, IPage
    {
        private List<ShareVm> _local = new List<ShareVm>();
        private static MainWindow Shell => MainWindow.Shell;

        public SharePage()
        {
            InitializeComponent();
        }

        public void OnNavigatedTo(object arg)
        {
            Reload(arg as string);
            _ = LoadNetworkAsync();
        }

        private ShareVm Selected => LocalList.SelectedItem as ShareVm;

        private void Reload(string select = null)
        {
            select = select ?? Selected?.Name;
            try
            {
                // physical local printers only – virtual ones (PDF, OneNote, XPS, Fax) can't be usefully shared
                _local = PrinterService.GetPrinters()
                    .Where(p => !p.IsNetwork && !new PrinterVm(p).IsVirtual && !string.Equals(p.Driver, "Microsoft Shared Fax Driver", StringComparison.OrdinalIgnoreCase))
                    .Select(p => new ShareVm(p)).ToList();
            }
            catch (Exception ex)
            {
                _local = new List<ShareVm>();
                Logger.Error("Could not list printers: " + ex.Message);
            }

            LocalList.ItemsSource = _local;
            NoLocal.Visibility = _local.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LocalList.SelectedItem = _local.FirstOrDefault(p => p.Name == select);

            var shared = _local.Where(p => p.IsShared).ToList();
            SharedList.ItemsSource = shared;
            NoneShared.Visibility = shared.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            bool domain = Session.DomainName.Length > 0;
            Publish.IsEnabled = domain;
            PublishHint.Text = domain
                ? $"Users in {Session.DomainName} can find it with Add printer → search the directory."
                : "Not available – this PC is not joined to a domain.";
            UpdateForm();
        }

        private async Task LoadNetworkAsync()
        {
            NetBanner.Tag = Tone.Neutral;
            NetIcon.Tag = Tone.Neutral;
            NetTitle.Text = "Checking network…";
            NetText.Text = "";
            NetFix.Visibility = Visibility.Collapsed;
            try
            {
                var nets = await Task.Run(SystemTools.GetConnectedNetworks);
                if (nets.Count == 0)
                {
                    SetBanner(Tone.Warning, "Not connected to a network", "Other computers can't reach this PC until it is on the network.", false);
                    return;
                }
                var pub = nets.Where(n => n.Category == 0).ToList();
                var names = string.Join(", ", nets.Select(n => $"{n.Name} ({n.CategoryText})"));
                if (pub.Count > 0)
                    SetBanner(Tone.Warning,
                        (pub.Count == 1 ? $"\"{pub[0].Name}\" is a Public network" : $"{pub.Count} networks are Public"),
                        "Windows blocks printer sharing on Public networks – switch it to Private so other computers can connect. Connected: " + names, true);
                else
                    SetBanner(Tone.Success, "Network: " + names,
                        nets.Any(n => n.Category == 2) ? "Domain network – sharing rules from Group Policy may also apply."
                                                       : "Private network – printer sharing is allowed.", false);
            }
            catch (Exception ex)
            {
                SetBanner(Tone.Neutral, "Network profile unknown", ex.Message, false);
            }
        }

        private void SetBanner(Tone tone, string title, string text, bool showFix)
        {
            NetBanner.Tag = tone;
            NetIcon.Tag = tone;
            NetIcon.Text = tone == Tone.Warning ? "\uE7BA" : tone == Tone.Success ? "\uE968" : "\uE946";
            NetTitle.Text = title;
            NetText.Text = text;
            NetFix.Visibility = showFix ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void NetFix_Click(object sender, RoutedEventArgs e)
        {
            if (!await Shell.Confirm("Switch to a Private network?",
                    "Only do this on a trusted network (office / home). Public networks such as hotels or cafés should stay Public.",
                    "Switch to Private"))
                return;
            await Shell.Busy(async () =>
            {
                await Shell.RunAdmin("Network switched to Private.", "netprofile:private");
                await LoadNetworkAsync();
            });
        }

        // ------------------------------------------------------------ form

        private void LocalList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var p = Selected;
            if (p != null)
            {
                ShareName.Text = p.IsShared ? p.Info.ShareName : PrinterService.SuggestShareName(p.Name);
                Publish.IsChecked = p.Info.IsPublished;
            }
            UpdateForm();
        }

        private void ShareName_TextChanged(object sender, TextChangedEventArgs e)
        {
            ShareError.Visibility = Visibility.Collapsed;
            UpdateForm();
        }

        private void UpdateForm()
        {
            var p = Selected;
            SettingsCard.IsEnabled = p != null;
            SettingsCard.Opacity = p != null ? 1 : 0.55;
            var name = ShareName.Text.Trim();
            UncPreview.Text = $@"\\{Session.MachineName}\{(name.Length > 0 ? name : "…")}";
            ShareBtn.Content = p?.IsShared == true ? "Update sharing" : "Share printer";
            StopBtn.Visibility = p?.IsShared == true ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CopyUnc_Click(object sender, RoutedEventArgs e) => MainWindow.CopyText(UncPreview.Text, "Printer path");

        private void CopyRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ShareVm vm) MainWindow.CopyText(vm.Unc, "Printer path");
        }

        private void EditRow_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ShareVm vm)
            {
                LocalList.SelectedItem = _local.FirstOrDefault(p => p.Name == vm.Name);
                LocalList.ScrollIntoView(LocalList.SelectedItem);
                ShareName.Focus();
            }
        }

        private async void Share_Click(object sender, RoutedEventArgs e)
        {
            var p = Selected;
            if (p == null) return;
            var share = ShareName.Text.Trim();
            var error = PrinterService.ValidateShareName(share, p.Name);
            if (error != null)
            {
                ShareError.Text = error;
                ShareError.Visibility = Visibility.Visible;
                ShareName.Focus();
                return;
            }

            var actions = new List<string>();
            if (Prepare.IsChecked == true) actions.AddRange(PrintServerPrep.Actions);
            actions.Add($"share:{p.Name}\\{share}\\{(Publish.IsChecked == true && Publish.IsEnabled ? 1 : 0)}");

            string unc = $@"\\{Session.MachineName}\{share}";
            await Shell.Busy(async () =>
            {
                bool ok = await Elevation.RunAdminAsync(actions);
                Reload(p.Name);
                if (ok)
                    Shell.Notify(Tone.Success, $"\"{p.Name}\" is shared as {unc}", "Copy path", () => MainWindow.CopyText(unc, "Printer path"));
                else
                    Shell.NotifyFailure("Sharing did not complete.");
            });
        }

        private async void Stop_Click(object sender, RoutedEventArgs e)
        {
            var p = Selected;
            if (p == null) return;
            if (!await Shell.Confirm("Stop sharing?",
                    $"Computers connected to {p.Unc} will no longer be able to print to \"{p.Name}\".", "Stop sharing", danger: true))
                return;
            await Shell.Busy(async () =>
            {
                await Shell.RunAdmin($"\"{p.Name}\" is no longer shared.", "unshare:" + p.Name);
                Reload(p.Name);
            });
        }
    }
}
