using System.Windows;

namespace GmEcuSimulator.Views;

// Generic script-output viewer. Original (string output) ctor preserves
// the J2534-list defaults so existing callers don't break; the new ctor
// takes a title / subtitle / description so the same window can surface
// elevated-PowerShell error output, future script dumps, etc.
public partial class RegisteredDevicesWindow : Window
{
    public RegisteredDevicesWindow(string output)
        : this("Registered J2534 Devices",
               "J2534 device registry",
               "Output of Installer\\List.ps1 - every J2534 PassThru device registered on this machine, both bitnesses. Read-only diagnostic.",
               output)
    {
    }

    public RegisteredDevicesWindow(string title, string subtitle, string description, string output)
    {
        InitializeComponent();
        Title = title;
        ChromeTitle.Text = title;
        SubtitleBlock.Text = subtitle;
        DescriptionBlock.Text = description;
        OutputBox.Text = output;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
