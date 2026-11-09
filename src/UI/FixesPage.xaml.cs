using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PrinterUtility
{
    public partial class FixesPage : UserControl, IPage
    {
        private readonly List<FixVm> _fixes = FixCatalog.All.Select(f => new FixVm(f)).ToList();
        private static MainWindow Shell => MainWindow.Shell;

        public FixesPage()
        {
            InitializeComponent();
            ClientList.ItemsSource = _fixes.Where(f => f.Fix.Scope == FixScope.Client).ToList();
            BothList.ItemsSource = _fixes.Where(f => f.Fix.Scope == FixScope.Both).ToList();
            ServerList.ItemsSource = _fixes.Where(f => f.Fix.Scope == FixScope.Server).ToList();
        }

        public void OnNavigatedTo(object arg)
        {
            _ = RecheckAsync();
            if (arg is string id)
            {
                var vm = _fixes.FirstOrDefault(f => f.Id == id);
                if (vm == null) return;
                foreach (var f in _fixes) f.IsSelected = false;
                vm.IsSelected = true;
                vm.IsExpanded = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                {
                    foreach (var list in new[] { ClientList, BothList, ServerList })
                        if (list.ItemContainerGenerator.ContainerFromItem(vm) is FrameworkElement el)
                            el.BringIntoView();
                }));
            }
        }

        private async Task RecheckAsync()
        {
            var states = await Task.Run(() => _fixes.Select(f => f.Fix.SafeState()).ToList());
            for (int i = 0; i < _fixes.Count; i++) _fixes[i].Update(states[i]);
        }

        private void Select(Func<Fix, bool> predicate)
        {
            foreach (var f in _fixes) f.IsSelected = predicate(f.Fix);
        }

        private void SelectClient_Click(object sender, RoutedEventArgs e) =>
            Select(f => f.Recommended && (f.Scope == FixScope.Client || f.Scope == FixScope.Both));

        private void SelectServer_Click(object sender, RoutedEventArgs e) =>
            Select(f => f.Recommended && (f.Scope == FixScope.Server || f.Scope == FixScope.Both));

        private void Clear_Click(object sender, RoutedEventArgs e) => Select(_ => false);

        private async void Recheck_Click(object sender, RoutedEventArgs e)
        {
            await RecheckAsync();
            Shell.Notify(Tone.Success, "Status re-checked.");
        }

        private async void Apply_Click(object sender, RoutedEventArgs e) => await Run(revert: false);
        private async void Revert_Click(object sender, RoutedEventArgs e) => await Run(revert: true);

        private async Task Run(bool revert)
        {
            var selected = _fixes.Where(f => f.IsSelected).Select(f => f.Fix).ToList();
            if (selected.Count == 0)
            {
                Shell.Notify(Tone.Warning, "Select at least one fix first.");
                return;
            }

            if (revert)
            {
                foreach (var f in selected.Where(f => !f.CanRevert)) Logger.Warn($"[{f.Id}] cannot be reverted automatically – skipped.");
                selected = selected.Where(f => f.CanRevert).ToList();
                if (selected.Count == 0) { Shell.Notify(Tone.Warning, "None of the selected fixes can be reverted automatically."); return; }
                if (!await Shell.Confirm("Revert fixes?", $"{selected.Count} fix(es) will be undone on this PC.", "Revert")) return;
            }
            else
            {
                var risky = selected.Where(f => f.SecurityNote != null).ToList();
                if (risky.Count > 0 && !await Shell.Confirm("This lowers security",
                        string.Join("\n\n", risky.Select(f => "• " + f.Title + "\n" + f.SecurityNote)) + "\n\nContinue anyway?",
                        "Apply anyway", danger: true))
                    return;
            }

            var userActions = new List<string>();
            var adminActions = new List<string>();
            foreach (var f in selected)
            {
                string action;
                if (revert) action = "revert:" + f.Id;
                else if (f.ParameterPrompt != null)
                {
                    var value = await Shell.Prompt(f.Title, f.ParameterPrompt, "server1;server1.contoso.local");
                    if (string.IsNullOrWhiteSpace(value)) { Logger.Warn($"[{f.Id}] skipped – no value entered."); continue; }
                    action = $"fix:{f.Id}={value}";
                }
                else action = "fix:" + f.Id;
                (f.RequiresAdmin ? adminActions : userActions).Add(action);
            }
            if (userActions.Count + adminActions.Count == 0) return;

            await Shell.Busy(async () =>
            {
                bool ok = true;
                if (userActions.Count > 0) ok &= await Task.Run(() => ActionRunner.Run(userActions)) == 0;
                if (adminActions.Count > 0) ok &= await Elevation.RunAdminAsync(adminActions);
                await RecheckAsync();
                if (ok) Shell.Notify(Tone.Success, revert ? "Fixes reverted." : "Fixes applied.");
                else Shell.NotifyFailure("Some fixes did not complete.");
            });
        }
    }
}
