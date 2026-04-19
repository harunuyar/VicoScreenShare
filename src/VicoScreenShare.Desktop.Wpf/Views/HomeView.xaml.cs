namespace VicoScreenShare.Desktop.App.Views;

using System.Windows.Controls;
using System.Windows.Input;
using VicoScreenShare.Desktop.App.ViewModels;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Backdrop click → dismiss the connection picker. Mirrors the Settings
    /// overlay pattern in MainWindow: the popup uses <c>StaysOpen="True"</c>
    /// so WPF's built-in "lose focus → close" behavior is disabled, and
    /// dismissal flows only through this explicit backdrop click or through
    /// toggling the chevron button. Without this, interacting with any
    /// control outside the popup (including the popup's own embedded
    /// TextBoxes during an inline edit, in some ordering scenarios) would
    /// surprise-close the picker.
    /// </summary>
    private void OnConnectionPickerBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        if (DataContext is HomeViewModel vm)
        {
            vm.IsPickerOpen = false;
        }
    }
}
