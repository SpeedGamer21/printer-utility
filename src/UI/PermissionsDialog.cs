using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PrinterUtility
{
    /// <summary>
    /// "Who can print" for a local printer – the printer's Security tab, which Control Panel lockdowns hide.
    /// Lists the current entries, lets the admin add a domain user or group (CONTOSO\jdoe, CONTOSO\Printer-Users,
    /// Domain Users) with Print permission or remove an entry, then applies all changes in one UAC prompt.
    /// </summary>
    internal static class PermissionsDialog
    {
        private static MainWindow Shell => MainWindow.Shell;

        public static async Task ShowAsync(string printer)
        {
            List<PermissionEntry> entries;
            try { entries = await Task.Run(() => PrinterSecurity.List(printer)); }
            catch (Exception ex) { Logger.Error(ex.Message); Shell.NotifyFailure("Could not read the printer's permissions."); return; }

            SecurityIdentifier domainUsers = null;
            try { domainUsers = PrinterSecurity.DomainUsersSid(); } catch { /* not on a domain */ }

            var pending = new Dictionary<SecurityIdentifier, bool>();   // true = give Print, false = remove entry

            var panel = new StackPanel { Width = 560 };
            var intro = new TextBlock
            {
                Style = (Style)Shell.FindResource("Body"), TextWrapping = TextWrapping.Wrap,
                Text = $"Accounts that can use \"{printer}\". Add a domain user or group to give it Print permission. " +
                       "Users print with their own sign-in, or with that account saved in Credential Manager on the client. " +
                       "Removing Everyone means only the accounts listed here can print.",
            };
            intro.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            panel.Children.Add(intro);

            var list = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var scroll = new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(scroll);

            var addLabel = new TextBlock { Text = "Add a user or group", Style = (Style)Shell.FindResource("Caption"), Margin = new Thickness(0, 16, 0, 4) };
            panel.Children.Add(addLabel);
            var addRow = new Grid();
            addRow.ColumnDefinitions.Add(new ColumnDefinition());
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            addRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameBox = new TextBox { Tag = @"DOMAIN\user or DOMAIN\group", Uid = "NoSubmit" };   // Enter adds, not Apply
            var addBtn = new Button { Content = "Add", Style = (Style)Shell.FindResource("BaseButton"), Margin = new Thickness(8, 0, 0, 0) };
            var duBtn = new Button { Content = "Domain Users", Style = (Style)Shell.FindResource("BaseButton"), Margin = new Thickness(8, 0, 0, 0),
                                     Visibility = domainUsers != null ? Visibility.Visible : Visibility.Collapsed,
                                     ToolTip = "Every user of the domain" };
            Grid.SetColumn(addBtn, 1);
            Grid.SetColumn(duBtn, 2);
            addRow.Children.Add(nameBox);
            addRow.Children.Add(addBtn);
            addRow.Children.Add(duBtn);
            panel.Children.Add(addRow);

            var message = new TextBlock { Style = (Style)Shell.FindResource("Caption"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            panel.Children.Add(message);

            void Say(string text, bool error)
            {
                message.Text = text;
                message.SetResourceReference(TextBlock.ForegroundProperty, error ? "ErrorFg" : "TextSub");
                message.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            }

            void Render()
            {
                list.Children.Clear();
                foreach (var entry in entries)
                {
                    var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    text.Children.Add(new TextBlock { Text = entry.Name, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    var rights = new TextBlock { Text = entry.Rights + (entry.Pending ? "   •   added when you click Apply" : ""), Style = (Style)Shell.FindResource("Caption") };
                    text.Children.Add(rights);
                    row.Children.Add(text);
                    if (entry.CanRemove)
                    {
                        var remove = new Button { Style = (Style)Shell.FindResource("IconButton"), Tag = "", ToolTip = "Remove " + entry.Name };
                        var captured = entry;
                        remove.Click += (s, e) =>
                        {
                            entries.Remove(captured);
                            if (captured.Pending) pending.Remove(captured.Sid);
                            else pending[captured.Sid] = false;
                            Say(captured.Pending ? null : $"{captured.Name} will be removed when you click Apply.", false);
                            Render();
                        };
                        Grid.SetColumn(remove, 1);
                        row.Children.Add(remove);
                    }
                    var card = new Border { Style = (Style)Shell.FindResource("Card"), Padding = new Thickness(14, 8, 8, 8), Margin = new Thickness(0, 0, 0, 4), Child = row };
                    list.Children.Add(card);
                }
            }

            async Task Add(string account)
            {
                account = (account ?? "").Trim();
                if (account.Length == 0) { nameBox.Focus(); return; }
                addBtn.IsEnabled = duBtn.IsEnabled = false;
                Say($"Looking up {account}…", false);
                try
                {
                    var sid = await Task.Run(() => PrinterSecurity.Resolve(account));
                    var name = await Task.Run(() => PrinterSecurity.NameOf(sid));
                    var existing = entries.FirstOrDefault(x => x.Sid == sid);
                    if (existing != null && existing.Rights.StartsWith("Print"))
                    {
                        Say($"{name} can already print.", false);
                        return;
                    }
                    if (existing == null)
                    {
                        existing = new PermissionEntry(sid, name, "Print") { Pending = true };
                        entries.Add(existing);
                    }
                    else { existing.Rights = "Print, " + existing.Rights; existing.Pending = true; }
                    pending[sid] = true;
                    nameBox.Clear();
                    Say($"{name} will get Print permission when you click Apply.", false);
                    Render();
                }
                catch (Exception ex) { Say(ex.Message, true); }
                finally { addBtn.IsEnabled = duBtn.IsEnabled = true; nameBox.Focus(); }
            }

            addBtn.Click += async (s, e) => await Add(nameBox.Text);
            duBtn.Click += async (s, e) => await Add(domainUsers?.Value);
            nameBox.KeyDown += async (s, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                await Add(nameBox.Text);
            };

            Render();
            if (!await Shell.ShowDialog($"Who can print to \"{printer}\"", panel, "Apply", "Cancel")) return;
            if (pending.Count == 0) return;

            var actions = pending.Select(p => $"print-perm:{printer},{p.Key.Value},{(p.Value ? 1 : 0)}").ToArray();
            await Shell.Busy(() => Shell.RunAdmin($"Permissions of \"{printer}\" updated.", actions));
        }
    }
}
