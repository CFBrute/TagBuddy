using BarcodeBuddy.Pages;
using BarcodeBuddy.Services;
using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;

namespace BarcodeBuddy;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<FavoritesService>();
        builder.Services.AddTransient<GeneratePage>();
        builder.Services.AddTransient<FavoritesPage>();
        builder.Services.AddTransient<ScanPage>();
        builder.Services.AddTransient<ShowBarcodePage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
