namespace TagBuddy.Controls;

/// <summary>The finger's position on the physical screen (px) for the current touch, and where it went down. Fed by
/// <see cref="MainActivity.DispatchTouchEvent"/>, which every touch passes through before any view, so it is already
/// current when a gesture handler runs.
///
/// <para>A pan's TotalX/Y is measured in the dragged view's own frame, which moves as the view is dragged — that
/// feedback makes a dragged view lag the finger and jitter. Screen coordinates don't move, so they track the finger
/// exactly.</para></summary>
public static class ScreenTouch
{
    private static float _rawX, _rawY, _downX, _downY;

    public static void Track(Android.Views.MotionEvent e)
    {
        _rawX = e.RawX;
        _rawY = e.RawY;
        if (e.ActionMasked == Android.Views.MotionEventActions.Down) { _downX = e.RawX; _downY = e.RawY; }
    }

    /// <summary>How far the finger has moved on the screen since it went down, in page units.</summary>
    public static (double X, double Y) Travel()
    {
        double density = DeviceDisplay.MainDisplayInfo.Density;
        if (density <= 0) density = 1;
        return ((_rawX - _downX) / density, (_rawY - _downY) / density);
    }
}
