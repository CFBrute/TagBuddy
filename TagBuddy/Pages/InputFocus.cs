namespace TagBuddy.Pages;

internal static class InputFocus
{
    /// <summary>Puts the blinking cursor in a text field as a hint of where to type, without opening the
    /// keyboard. Android normally opens the keyboard whenever a field gets focus, so that is switched off for
    /// this one focus and switched back on a moment later: a tap on the field then opens the keyboard as usual.</summary>
    public static void ShowCaret(InputView view)
    {
#if ANDROID
        if (view.Handler?.PlatformView is not Android.Widget.EditText edit || edit.HasFocus) return;
        edit.ShowSoftInputOnFocus = false;
        edit.RequestFocus();
        edit.PostDelayed(() => edit.ShowSoftInputOnFocus = true, 500);
#endif
    }
}
