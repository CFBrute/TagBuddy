using BarcodeBuddy.Pages;

namespace BarcodeBuddy;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(ShowBarcodePage.Route, typeof(ShowBarcodePage));
        Routing.RegisterRoute(WebPage.Route, typeof(WebPage));
    }
}
