using System.Windows;

namespace GmEcuSimulator.Views;

public partial class RegisteredDevicesWindow : Window
{
    public RegisteredDevicesWindow(string output)
    {
        InitializeComponent();
        OutputBox.Text = output;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
