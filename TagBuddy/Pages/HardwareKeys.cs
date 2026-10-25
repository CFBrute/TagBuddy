namespace TagBuddy.Pages;

/// <summary>Keys from a hardware keyboard that an editor on screen can take: undo and redo (Ctrl+Z, Ctrl+Y or
/// Ctrl+Shift+Z) and the cursor keys.</summary>
internal enum EditorKey { Undo, Redo, Left, Right, Up, Down, Home, End }

/// <summary>Hands hardware-keyboard shortcuts from the activity to whichever editor is listening. The handler returns
/// true when it used the key, so the key goes no further; with no handler, or false, the key behaves as usual.</summary>
internal static class HardwareKeys
{
    public static Func<EditorKey, bool>? Handler { get; set; }

    public static bool Handle(EditorKey key) => Handler?.Invoke(key) == true;
}
