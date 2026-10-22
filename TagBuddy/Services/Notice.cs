namespace TagBuddy.Services;

/// <summary>A short notice that shows for a moment and goes away on its own, for confirmations such as
/// "Saved" or "Copied" that need no answer. Errors and questions still use a dialog.</summary>
public static class Notice
{
    public static void Show(string text)
    {
#if ANDROID
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                Android.Widget.Toast.MakeText(Platform.AppContext, text, Android.Widget.ToastLength.Short)?.Show();
            }
            catch
            {
                // A missing notice is no loss.
            }
        });
#endif
    }
}
