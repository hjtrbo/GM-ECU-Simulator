using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GmEcuSimulator.Views;

// Modeless setup workspace hosting the PID parameters + waveform editor
// that used to live in MainWindow. The ECU list is replicated here with
// an independent selection (MainViewModel.SetupSelectedEcu) so picking a
// different ECU in this window doesn't disturb the main window's Selected
// ECU inspector.
public partial class SetupWindow : Window
{
    public SetupWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizeIcon();
    }

    private void OnMinimizeClicked(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximizeClicked(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindowClicked(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeIcon()
    {
        if (MaxIcon == null) return;
        var key = WindowState == WindowState.Maximized ? "Icon.Restore" : "Icon.Maximize";
        if (TryFindResource(key) is Geometry geo)
            MaxIcon.Data = geo;
    }
}
