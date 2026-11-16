using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace PrinterUtility
{
    /// <summary>
    /// "Save credentials for a print server" dialog: asks for server / user / password, saves them in the
    /// current user's Credential Manager (Windows Credentials), closes the stale session and tests the logon.
    /// </summary>
    internal static class CredentialDialog
    {
        private static MainWindow Shell => MainWindow.Shell;

        /// <summary>Returns true when the credential was saved and the server accepted it.</summary>
        public static async Task<bool> ShowAsync(string server, string reason = null)
        {
            TextBlock Label(string text) =>
                new TextBlock { Text = text, Style = (Style)Shell.FindResource("Caption"), Margin = new Thickness(0, 12, 0, 4) };

            var panel = new StackPanel { MaxWidth = 560 };
            var intro = new TextBlock
            {
                Style = (Style)Shell.FindResource("Body"), MaxWidth = 540,
                Text = (reason != null ? reason + "\n\n" : "") +
                       "Enter an account that exists on the PC or server sharing the printer, with a password. " +
                       "It's saved in Credential Manager for " + Session.UserName + " and used every time this PC connects to it. " +
                       "A plain user name is tried as SERVER\\user (an account of that PC) and then as DOMAIN\\user; " +
                       "the one the server accepts is saved. You can also type DOMAIN\\user or an e-mail address (Microsoft account). " +
                       "The Print Spooler is restarted afterwards so it uses the new credential (admin prompt).",
            };
            intro.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            var serverBox = new TextBox { Text = Credentials.HostOf(server), Tag = "Server name or IP (as you connect to it)" };
            var userBox = new TextBox { Tag = "user, SERVER\\user or user@domain" };
            var passwordBox = new PasswordBox();
            panel.Children.Add(intro);
            panel.Children.Add(Label("Server"));
            panel.Children.Add(serverBox);
            panel.Children.Add(Label("User name"));
            panel.Children.Add(userBox);
            panel.Children.Add(Label("Password"));
            panel.Children.Add(passwordBox);

            // focus the first empty field (the dialog focuses the first TextBox by default)
            if (serverBox.Text.Length > 0)
                panel.Loaded += (s, e) => panel.Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Input, (System.Action)(() => userBox.Focus()));

            bool Valid()
            {
                if (Credentials.HostOf(serverBox.Text).Length == 0) { serverBox.Focus(); return false; }
                if (userBox.Text.Trim().Length == 0) { userBox.Focus(); return false; }
                if (passwordBox.Password.Length == 0) { passwordBox.Focus(); return false; }
                return true;
            }

            if (!await Shell.ShowDialog("Save credentials for a print server", panel, "Save and test", "Cancel", Valid)) return false;

            string host = serverBox.Text, user = userBox.Text, password = passwordBox.Password;
            passwordBox.Clear();
            bool ok = false;
            await Shell.Busy(async () =>   // callers must not already be inside Shell.Busy
            {
                ok = await SaveAndApplyAsync(host, user, password);
                if (ok) Shell.Notify(Tone.Success, $"Credentials saved for {Credentials.HostOf(host)}.");
                else Shell.NotifyFailure($"{Credentials.HostOf(host)} didn't accept those credentials.");
            });
            return ok;
        }

        /// <summary>
        /// Saves the credential and makes the Print Spooler use it. The spooler keeps its connection to a server
        /// open under the account it first used, so until it's restarted jobs to that server stay stuck and a
        /// new sign-in can fail with 1219. It's restarted (one UAC prompt) on 1219 and after a successful save.
        /// Returns true when the server accepted the saved credential.
        /// </summary>
        internal static async Task<bool> SaveAndApplyAsync(string server, string user, string password)
        {
            var host = Credentials.HostOf(server);
            var result = await Task.Run(() => Credentials.Save(server, user, password));
            bool restarted = false;
            if (result == CredentialResult.SessionConflict)
            {
                Logger.Info($"Restarting the Print Spooler to close its old connection to \\\\{host}...");
                restarted = await Elevation.RunAdminAsync(new[] { "spooler:restart" });
                if (restarted) result = await Task.Run(() => Credentials.Save(server, user, password));
                if (result == CredentialResult.SessionConflict)
                    Logger.Error($"A connection to \\\\{host} under another account is still open (1219). Close mapped drives " +
                                 "and Explorer windows to that PC, or sign out and back in, then try again.");
            }
            if (result != CredentialResult.Accepted) return false;

            if (!restarted)
            {
                Logger.Info($"Restarting the Print Spooler so it reconnects to \\\\{host} with the saved credential...");
                if (!await Elevation.RunAdminAsync(new[] { "spooler:restart" }))
                    Logger.Warn($"The Print Spooler wasn't restarted. If jobs to \\\\{host} stay in the queue, " +
                                "restart it in Tools → Restart spooler.");
            }
            return true;
        }
    }
}
