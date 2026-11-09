using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace PrinterUtility
{
    internal interface IPage
    {
        void OnNavigatedTo(object arg);
    }

    /// <summary>App shell: navigation, busy indicator, toasts, in-window dialogs and the activity log.</summary>
    public partial class MainWindow : Window
    {
        internal static MainWindow Shell { get; private set; }

        public ObservableCollection<LogEntry> Log { get; } = new ObservableCollection<LogEntry>();

        private readonly Dictionary<RadioButton, FrameworkElement> _pages = new Dictionary<RadioButton, FrameworkElement>();
        private readonly DispatcherTimer _toastTimer = new DispatcherTimer();
        private object _navArg;
        private int _busy;
        private int _unseenErrors;
        private Action _toastAction;
        private TaskCompletionSource<bool> _dialog;
        private Func<bool> _dialogValidate;

        public MainWindow()
        {
            Shell = this;
            InitializeComponent();

            UserText.Text = $"{Session.UserDomain}\\{Session.UserName}";
            MachineText.Text = Session.MachineName + (Session.DomainName.Length > 0 ? "." + Session.DomainName : "");
            bool admin = SystemTools.IsAdmin;
            ElevationText.Text = admin ? "Running as administrator" : "Standard user";
            ElevationPill.Tag = admin ? Tone.Warning : Tone.Neutral;
            ElevationPill.ToolTip = admin
                ? "Running elevated: the default printer is set for THIS (admin) account."
                : "Admin actions will show a UAC prompt. Per-user settings apply to " + Session.UserName + ".";

            _pages[NavPrinters] = new PrintersPage();
            _pages[NavShare] = new SharePage();
            _pages[NavFixes] = new FixesPage();
            _pages[NavTools] = new ToolsPage();
            _pages[NavActivity] = new ActivityPage();

            Logger.Line += OnLogLine;
            Closed += (s, e) => Logger.Line -= OnLogLine;
            SourceInitialized += (s, e) => Theme.StyleTitleBar(this);
            Theme.Changed += () => Theme.StyleTitleBar(this);

            _toastTimer.Tick += (s, e) => HideToast();
            PreviewKeyDown += OnPreviewKeyDown;

            Loaded += (s, e) =>
            {
                NavPrinters.IsChecked = true;
                Logger.Info("Ready. Per-user actions run as " + Session.UserName + "; admin actions ask for elevation.");
            };
        }

        // ============================================================ navigation

        internal void Navigate(string page, object arg = null)
        {
            _navArg = arg;
            var target = page == "share" ? NavShare : page == "fixes" ? NavFixes : page == "tools" ? NavTools
                       : page == "activity" ? NavActivity : NavPrinters;
            if (target.IsChecked == true) ShowPage(target);
            else target.IsChecked = true;
        }

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && _pages.ContainsKey(rb)) ShowPage(rb);
        }

        private void ShowPage(RadioButton rb)
        {
            var page = _pages[rb];
            PageHost.Content = page;
            (page as IPage)?.OnNavigatedTo(_navArg);
            _navArg = null;

            if (rb == NavActivity)
            {
                _unseenErrors = 0;
                ErrorBadge.Visibility = Visibility.Collapsed;
            }

            // subtle entrance animation
            var tt = new TranslateTransform(0, 10);
            page.RenderTransform = tt;
            page.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        }

        // ============================================================ busy

        internal async Task Busy(Func<Task> work)
        {
            if (_busy > 0)
            {
                Notify(Tone.Warning, "Please wait – another operation is still running.");
                return;
            }
            _busy++;
            Progress.Visibility = Visibility.Visible;
            PageHost.IsHitTestVisible = false;
            Mouse.OverrideCursor = Cursors.AppStarting;
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message);
                Notify(Tone.Error, ex.Message);
            }
            finally
            {
                _busy--;
                Progress.Visibility = Visibility.Collapsed;
                PageHost.IsHitTestVisible = true;
                Mouse.OverrideCursor = null;
            }
        }

        /// <summary>Runs admin actions (UAC if needed) and shows a toast with the outcome.</summary>
        internal async Task<bool> RunAdmin(string successMessage, params string[] actions)
        {
            bool ok = await Elevation.RunAdminAsync(actions);
            if (ok) Notify(Tone.Success, successMessage);
            else NotifyFailure();
            return ok;
        }

        internal void NotifyFailure(string message = "Something went wrong.") =>
            Notify(Tone.Error, message + " See Activity for details.", "View activity", () => Navigate("activity"));

        // ============================================================ toast

        internal void Notify(Tone tone, string message, string actionText = null, Action action = null)
        {
            ToastIcon.Tag = tone;
            ToastIcon.Text = tone == Tone.Success ? "" : tone == Tone.Warning ? "" : tone == Tone.Error ? "" : "";
            ToastText.Text = message;
            Toast.SetResourceReference(Border.BackgroundProperty, "Dialog");
            _toastAction = action;
            ToastAction.Content = actionText;
            ToastAction.Visibility = actionText != null ? Visibility.Visible : Visibility.Collapsed;

            Toast.Visibility = Visibility.Visible;
            Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(tone == Tone.Error ? 12 : 6);
            _toastTimer.Start();
        }

        private void HideToast()
        {
            _toastTimer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (s, e) => Toast.Visibility = Visibility.Collapsed;
            Toast.BeginAnimation(OpacityProperty, fade);
        }

        private void ToastClose_Click(object sender, RoutedEventArgs e) => HideToast();

        private void ToastAction_Click(object sender, RoutedEventArgs e)
        {
            HideToast();
            _toastAction?.Invoke();
        }

        // ============================================================ dialogs

        internal Task<bool> ShowDialog(string title, object content, string primary, string secondary = "Cancel",
                                       Func<bool> validate = null, bool danger = false)
        {
            _dialog?.TrySetResult(false);
            DialogTitle.Text = title;
            DialogContent.Content = content;
            DialogPrimary.Content = primary;
            DialogPrimary.Style = (Style)FindResource(danger ? "DangerButton" : "AccentButton");
            DialogSecondary.Content = secondary;
            DialogSecondary.Visibility = secondary == null ? Visibility.Collapsed : Visibility.Visible;
            DialogButtons.Columns = secondary == null ? 1 : 2;
            _dialogValidate = validate;
            _dialog = new TaskCompletionSource<bool>();

            DialogLayer.Visibility = Visibility.Visible;
            DialogLayer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            var scale = new ScaleTransform(0.96, 0.96);
            DialogCard.RenderTransformOrigin = new Point(0.5, 0.5);
            DialogCard.RenderTransform = scale;
            var grow = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

            Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() =>
            {
                var box = FindChild<TextBox>(DialogContent);
                if (box != null) { box.Focus(); box.SelectAll(); }
                else DialogPrimary.Focus();
            }));
            return _dialog.Task;
        }

        internal Task<bool> Confirm(string title, string message, string primary, bool danger = false)
        {
            var text = new TextBlock { Text = message, Style = (Style)FindResource("Body"), MaxWidth = 620 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            return ShowDialog(title, text, primary, "Cancel", null, danger);
        }

        internal async Task<string> Prompt(string title, string message, string placeholder, string initial = "")
        {
            var panel = new StackPanel { MaxWidth = 620 };
            var text = new TextBlock { Text = message, Style = (Style)FindResource("Body"), Margin = new Thickness(0, 0, 0, 12) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextSub");
            var box = new TextBox { Text = initial, Tag = placeholder, MinWidth = 480 };
            panel.Children.Add(text);
            panel.Children.Add(box);
            return await ShowDialog(title, panel, "OK", "Cancel", () => box.Text.Trim().Length > 0) ? box.Text.Trim() : null;
        }

        private void CloseDialog(bool result)
        {
            DialogLayer.Visibility = Visibility.Collapsed;
            DialogContent.Content = null;
            var d = _dialog;
            _dialog = null;
            d?.TrySetResult(result);
        }

        private void DialogPrimary_Click(object sender, RoutedEventArgs e)
        {
            if (_dialogValidate != null && !_dialogValidate()) return;
            CloseDialog(true);
        }

        private void DialogSecondary_Click(object sender, RoutedEventArgs e) => CloseDialog(false);

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (DialogLayer.Visibility != Visibility.Visible) return;
            if (e.Key == Key.Escape) { CloseDialog(false); e.Handled = true; }
            else if (e.Key == Key.Enter && !(Keyboard.FocusedElement is Button) &&
                     !(Keyboard.FocusedElement is FrameworkElement fe && fe.Uid == "NoSubmit"))
            {
                DialogPrimary_Click(this, null);
                e.Handled = true;
            }
        }

        // ============================================================ log

        private void OnLogLine(string line)
        {
            Dispatcher.BeginInvoke((Action)(() =>
            {
                var entry = LogEntry.Parse(line);
                Log.Add(entry);
                while (Log.Count > 3000) Log.RemoveAt(0);
                if (entry.Tone == Tone.Error && NavActivity.IsChecked != true)
                {
                    _unseenErrors++;
                    ErrorBadgeText.Text = _unseenErrors.ToString();
                    ErrorBadge.Visibility = Visibility.Visible;
                }
            }));
        }

        // ============================================================ helpers

        internal static T FindChild<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = VisualTreeHelper.GetChild(root, i);
                if (c is T t) return t;
                var r = FindChild<T>(c);
                if (r != null) return r;
            }
            return null;
        }

        internal static void CopyText(string text, string what)
        {
            try
            {
                Clipboard.SetText(text);
                Shell.Notify(Tone.Success, what + " copied to the clipboard.");
            }
            catch (Exception ex) { Shell.Notify(Tone.Error, "Clipboard is busy: " + ex.Message); }
        }
    }
}
