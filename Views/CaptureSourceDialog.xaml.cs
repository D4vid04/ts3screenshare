using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using TS3ScreenShare.Models;
using TS3ScreenShare.Services;

namespace TS3ScreenShare.Views
{
    public partial class CaptureSourceDialog : Window
    {
        public CaptureSource? SelectedSource { get; private set; }

        private readonly ObservableCollection<SourceItem> _monitors = new();
        private readonly ObservableCollection<SourceItem> _allWindows = new();
        private readonly ObservableCollection<SourceItem> _filteredWindows = new();

        private SourceItem? _selected;

        public CaptureSourceDialog()
        {
            InitializeComponent();
            MonitorList.ItemsSource = _monitors;
            WindowList.ItemsSource = _filteredWindows;

            LoadSources();
        }

        private void LoadSources()
        {
            var sources = ScreenCaptureService.GetSources();

            int monIndex = 1;
            foreach (var src in sources)
            {
                if (src.Type == CaptureSourceType.FullScreen)
                {
                    _monitors.Add(new SourceItem
                    {
                        Source = src,
                        Name = src.Name,
                        Icon = monIndex == 1 ? "🖥" : "🖥",
                    });
                    monIndex++;
                }
                else
                {
                    _allWindows.Add(new SourceItem { Source = src, Name = src.Name });
                }
            }

            foreach (var w in _allWindows)
                _filteredWindows.Add(w);
        }

        private void Select(SourceItem item)
        {
            if (_selected != null)
                _selected.IsSelected = false;

            _selected = item;
            item.IsSelected = true;

            SelectedLabel.Text = item.Name;
            BtnSelect.IsEnabled = true;
        }

        private void MonitorCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is SourceItem item)
                Select(item);
        }

        private void WindowRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.DataContext is SourceItem item)
                Select(item);
        }

        private void TxtSearch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            var query = TxtSearch.Text.Trim().ToLower();
            _filteredWindows.Clear();

            foreach (var w in _allWindows)
            {
                if (string.IsNullOrEmpty(query) || w.Name.ToLower().Contains(query))
                    _filteredWindows.Add(w);
            }
        }

        private void BtnSelect_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            SelectedSource = _selected.Source;
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Titlebar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            DragMove();
        }
    }

    internal sealed class SourceItem : System.ComponentModel.INotifyPropertyChanged
    {
        public required CaptureSource Source { get; init; }
        public required string Name { get; init; }
        public string Icon { get; init; } = "🖥";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }
}
