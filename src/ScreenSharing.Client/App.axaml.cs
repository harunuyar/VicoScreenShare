using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ScreenSharing.Client.Services;
using ScreenSharing.Client.ViewModels;

namespace ScreenSharing.Client;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var identity = new IdentityStore();
            var signaling = new SignalingClient();
            var navigation = new NavigationService();
            var settings = new ClientSettings();

            AppServices.Initialize(identity, signaling, navigation, settings);

            var home = new HomeViewModel(identity, signaling, navigation, settings);
            navigation.NavigateTo(home);

            desktop.MainWindow = new MainWindow
            {
                DataContext = navigation,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
