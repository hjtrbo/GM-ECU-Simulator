using System.Windows;

namespace GmEcuSimulator.Views;

// Scoped DBC-import picker. DataContext is a DbcImportViewModel; Import closes with DialogResult=true
// so the caller (MainViewModel.ImportDbc) reads the picked messages and apply-mode off the VM.
public partial class DbcImportWindow : Window
{
    public DbcImportWindow()
    {
        InitializeComponent();
    }

    private void OnImportClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
