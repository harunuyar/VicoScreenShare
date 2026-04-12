using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ScreenSharing.Client.Views;

public partial class PreviewView : UserControl
{
    public PreviewView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
