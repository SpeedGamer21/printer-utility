using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace PrinterUtility
{
    public partial class ActivityPage : UserControl, IPage
    {
        public ActivityPage()
        {
            InitializeComponent();
            var log = MainWindow.Shell.Log;
            List.ItemsSource = log;
            log.CollectionChanged += (s, e) =>
            {
                if (e.Action == NotifyCollectionChangedAction.Add && IsVisible && log.Count > 0)
                    List.ScrollIntoView(log[log.Count - 1]);
            };
        }

        public void OnNavigatedTo(object arg)
        {
            var log = MainWindow.Shell.Log;
            if (log.Count > 0)
                Dispatcher.BeginInvoke((System.Action)(() => List.ScrollIntoView(log[log.Count - 1])));
        }

        private void Copy_Click(object sender, RoutedEventArgs e) =>
            MainWindow.CopyText(string.Join("\r\n", MainWindow.Shell.Log.Select(l => l.Raw)), "Activity log");

        private void Clear_Click(object sender, RoutedEventArgs e) => MainWindow.Shell.Log.Clear();
    }
}
