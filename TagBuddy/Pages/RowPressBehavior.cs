namespace TagBuddy.Pages;

/// <summary>Tap and long press on a list row, using Android's own click and long click on the row's view. MAUI has no
/// long-press gesture, and a TapGestureRecognizer next to a native long click would swallow it; with both native,
/// Android tells them apart and cancels both when the finger scrolls. Buttons inside the row keep their own clicks.
/// The sender of both events is the row, whose BindingContext is the item.</summary>
public sealed class RowPressBehavior : Behavior<View>
{
    public event EventHandler? Pressed;
    public event EventHandler? LongPressed;

    private View? _row;
#if ANDROID
    private Android.Views.View? _native;
#endif

    protected override void OnAttachedTo(View row)
    {
        base.OnAttachedTo(row);
        _row = row;
        row.HandlerChanged += OnHandlerChanged;
        Hook();
    }

    protected override void OnDetachingFrom(View row)
    {
        row.HandlerChanged -= OnHandlerChanged;
        Unhook();
        _row = null;
        base.OnDetachingFrom(row);
    }

    private void OnHandlerChanged(object? sender, EventArgs e) => Hook();

    private void Hook()
    {
        Unhook();
#if ANDROID
        if (_row?.Handler?.PlatformView is not Android.Views.View native) return;
        _native = native;
        native.Clickable = true;
        native.LongClickable = true;
        native.Click += OnClick;
        native.LongClick += OnLongClick;
#endif
    }

    private void Unhook()
    {
#if ANDROID
        if (_native is null) return;
        _native.Click -= OnClick;
        _native.LongClick -= OnLongClick;
        _native = null;
#endif
    }

#if ANDROID
    private void OnClick(object? sender, EventArgs e) => Pressed?.Invoke(_row, EventArgs.Empty);

    private void OnLongClick(object? sender, Android.Views.View.LongClickEventArgs e)
    {
        e.Handled = true;   // no click follows the long press
        LongPressed?.Invoke(_row, EventArgs.Empty);
    }
#endif
}
