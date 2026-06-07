using System.Windows;

namespace GmEcuSimulator.Views;

// A DataGridColumn lives outside the visual/logical tree, so a normal
// {Binding} on a column (or its Header) can't reach the DataGrid's
// DataContext. This Freezable carries the DataContext into the resource
// dictionary: declare it in DataGrid.Resources with Data="{Binding}", then
// columns bind through it with an explicit Source. Standard WPF workaround
// (see the column Header bindings in EcuSetupWindow.xaml).
public sealed class BindingProxy : Freezable
{
    protected override Freezable CreateInstanceCore() => new BindingProxy();

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy),
                                    new UIPropertyMetadata(null));
}
