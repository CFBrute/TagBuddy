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

#if ANDROID
        // The top tabs swipe between pages, and that gesture otherwise wins over a slider drag: dragging a
        // knob sideways flips to the next tab. While a finger is on a slider, keep the parents from
        // intercepting the touch.
        Microsoft.Maui.Handlers.SliderHandler.Mapper.AppendToMapping("KeepTouchWhileDragging", (handler, _) =>
        {
            handler.PlatformView.Touch += (sender, e) =>
            {
                if (sender is Android.Views.View view && e.Event is not null)
                {
                    bool dragging = e.Event.Action is Android.Views.MotionEventActions.Down or Android.Views.MotionEventActions.Move;
                    view.Parent?.RequestDisallowInterceptTouchEvent(dragging);

                    // The tab pager does not honour that request reliably, so switch its swiping off outright
                    // for the duration of the drag.
                    for (var p = view.Parent; p is not null; p = p.Parent)
                    {
                        if (p is AndroidX.ViewPager2.Widget.ViewPager2 pager)
                        {
                            pager.UserInputEnabled = !dragging;
                            break;
                        }
                    }
                }
                e.Handled = false;   // the slider still handles the touch itself
            };
        });
#endif

        builder.Services.AddSingleton<FavoritesService>();
        builder.Services.AddSingleton<NfcDumpStore>();
        builder.Services.AddTransient<GeneratePage>();
        builder.Services.AddTransient<QrPage>();
        builder.Services.AddTransient<FavoritesPage>();
        builder.Services.AddTransient<ScanPage>();
        builder.Services.AddTransient<NfcPage>();
        builder.Services.AddTransient<ShowBarcodePage>();
        builder.Services.AddTransient<WebPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
