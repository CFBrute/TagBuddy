using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The two ways a result offers to hand its content to another app: the open icon (with the action as its
/// long-press label) and, for anything but a web link, a button that says it in words ("Sign in with passkey",
/// "Join Wi-Fi network"), since the icon alone does not tell what will happen.</summary>
internal static class HandoffButtons
{
    public static void Show(string? value, Button icon, Button words)
    {
        if (!Handoff.TryDetect(value, out var handoff))
        {
            icon.IsVisible = false;
            words.IsVisible = false;
            return;
        }
        var label = HandoffLauncher.Label(handoff);
        icon.IsVisible = true;
        IconButtons.SetLabel(icon, label);
        words.IsVisible = !handoff.IsWeb;
        words.Text = label;
    }
}
