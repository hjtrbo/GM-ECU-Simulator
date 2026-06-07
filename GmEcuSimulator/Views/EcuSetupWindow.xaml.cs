using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GmEcuSimulator.ViewModels;

namespace GmEcuSimulator.Views;

// Modeless setup workspace hosting the PID parameters + waveform editor
// that used to live in MainWindow. The ECU list is replicated here with
// an independent selection (MainViewModel.SetupSelectedEcu) so picking a
// different ECU in this window doesn't disturb the main window's Selected
// ECU inspector.
public partial class EcuSetupWindow : Window
{
    public EcuSetupWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizeIcon();
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindowClicked(object sender, RoutedEventArgs e) => Close();

    // Right-clicking a PID column header opens that column's Excel-style
    // sort/filter popup. The header's DataContext is the column's
    // PidColumnFilter (bound via EcuProxy in EcuSetupWindow.xaml); opening is just
    // flipping its IsOpen, which the popup binds to.
    private void OnColumnHeaderRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PidColumnFilter filter })
        {
            filter.IsOpen = true;
            e.Handled = true;
        }
    }

    private void UpdateMaximizeIcon()
    {
        if (MaxIcon == null) return;
        var key = WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize";
        if (TryFindResource(key) is Geometry geo)
            MaxIcon.Data = geo;
    }
}
