using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ScreenSharing.Client.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
