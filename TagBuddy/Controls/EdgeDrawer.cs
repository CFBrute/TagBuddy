using Microsoft.Maui.Controls.Shapes;
using TagBuddy.Pages;

namespace TagBuddy.Controls;

/// <summary>A floating side-sheet that docks on the right or left edge of the page and slides off it to a small tab.
/// Put it last in the page's root Grid so it floats over the content; it is only as tall as what is inside it.
///
/// <para>Drag anywhere on it to slide it open or shut, or up and down to move it; a tap on the tab toggles it, and a
/// tap anywhere on it while shut opens it. The drag follows the finger exactly (see <see cref="ScreenTouch"/>) along
/// one main direction, which only changes once the finger has gone <see cref="AxisSwitch"/> the other way. Pulled
/// <see cref="SideSwitch"/> past fully open, it flips to dock on the other side.</para>
///
/// <para>Set <see cref="PreferenceKey"/> to have it remember its height and side between runs.</para></summary>
[ContentProperty(nameof(Body))]
public sealed class EdgeDrawer : ContentView
{
    public static readonly BindableProperty BodyProperty = BindableProperty.Create(
        nameof(Body), typeof(View), typeof(EdgeDrawer), null,
        propertyChanged: (b, _, n) => ((EdgeDrawer)b)._sheet.Content = (View?)n);

    public static readonly BindableProperty TabTextProperty = BindableProperty.Create(
        nameof(TabText), typeof(string), typeof(EdgeDrawer), "",
        propertyChanged: (b, _, n) => ((EdgeDrawer)b)._tabLabel.Text = (string?)n);

    public static readonly BindableProperty IsOpenProperty = BindableProperty.Create(
        nameof(IsOpen), typeof(bool), typeof(EdgeDrawer), false, BindingMode.TwoWay,
        propertyChanged: (b, _, _) => ((EdgeDrawer)b).Animate());

    public static readonly BindableProperty DockLeftProperty = BindableProperty.Create(
        nameof(DockLeft), typeof(bool), typeof(EdgeDrawer), false,
        propertyChanged: (b, _, _) => ((EdgeDrawer)b).ApplyDockSide());

    /// <summary>What the drawer holds.</summary>
    public View? Body { get => (View?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    /// <summary>The short label on the grab tab.</summary>
    public string? TabText { get => (string?)GetValue(TabTextProperty); set => SetValue(TabTextProperty, value); }
    /// <summary>Open (on screen) or shut (only the tab showing). Setting it slides the drawer there.</summary>
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    /// <summary>Docked on the left edge rather than the right.</summary>
    public bool DockLeft { get => (bool)GetValue(DockLeftProperty); set => SetValue(DockLeftProperty, value); }

    /// <summary>When set, the drawer's height and side are kept in Preferences under "{key}_y" and "{key}_left".</summary>
    public string? PreferenceKey
    {
        get => _prefKey;
        set { _prefKey = value; if (value is not null) DockLeft = Preferences.Get(value + "_left", DockLeft); }
    }

    private const double AxisPick = 8, AxisSwitch = 40, SideSwitch = 60, Commit = 20;
    private const double TabWidth = 30, Gutter = 24, SheetInset = 15;

    private readonly Border _sheet;
    private readonly Border _tab;
    private readonly Label _tabLabel;
    private readonly Grid _scrim;
    private string? _prefKey;

    // Slid off its edge until only the tab is left: negative when docked left.
    private double _hiddenX = 300;
    private bool _yLoaded;
    // A drag follows one main direction (1 = horizontal open/shut, 2 = vertical move); the anchors are the finger's
    // travel when the current direction took over, so a switch needs AxisSwitch the other way from there.
    private int _axis;
    private bool _flipped;
    private double _startX, _startY, _origX, _anchorX, _anchorY;

    public EdgeDrawer()
    {
        HorizontalOptions = LayoutOptions.Fill;
        VerticalOptions = LayoutOptions.Center;

        _sheet = new Border { StrokeThickness = 0 };
        _sheet.SetAppThemeColor(BackgroundColorProperty, Color.FromArgb("#F3F4F6"), Color.FromArgb("#1F2937"));

        _tabLabel = new Label { FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Palette.AccentText,
                                HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        _tab = new Border { WidthRequest = TabWidth, HeightRequest = 64, VerticalOptions = LayoutOptions.Center,
                            StrokeThickness = 0, BackgroundColor = Palette.Accent, Content = _tabLabel };
        var tabTap = new TapGestureRecognizer();
        tabTap.Tapped += (_, _) => IsOpen = !IsOpen;
        _tab.GestureRecognizers.Add(tabTap);

        // Catcher plane: while shut it sits on top and takes the touch first, so a tap anywhere opens the drawer and
        // a drag still slides it. Hidden while open, so what is inside works normally.
        _scrim = new Grid { BackgroundColor = Colors.Transparent };
        var scrimPan = new PanGestureRecognizer();
        scrimPan.PanUpdated += OnPan;
        var scrimTap = new TapGestureRecognizer();
        scrimTap.Tapped += (_, _) => IsOpen = true;
        _scrim.GestureRecognizers.Add(scrimPan);
        _scrim.GestureRecognizers.Add(scrimTap);

        var pan = new PanGestureRecognizer();
        pan.PanUpdated += OnPan;
        var root = new Grid { Children = { _sheet, _tab, _scrim } };
        root.GestureRecognizers.Add(pan);
        Content = root;

        SizeChanged += OnSizeChanged;
        ApplyDockSide();
    }

    /// <summary>Lays the drawer out for its side: the right-edge layout, or its mirror.</summary>
    private void ApplyDockSide()
    {
        bool left = DockLeft;
        Margin = left ? new Thickness(0, 8, Gutter, 8) : new Thickness(Gutter, 8, 0, 8);
        _sheet.Margin = left ? new Thickness(0, 0, SheetInset, 0) : new Thickness(SheetInset, 0, 0, 0);
        _sheet.Padding = left ? new Thickness(12, 12, 22, 12) : new Thickness(22, 12, 12, 12);
        _sheet.StrokeShape = new RoundRectangle { CornerRadius = left ? new CornerRadius(0, 14, 14, 0) : new CornerRadius(14, 0, 0, 14) };
        _tab.HorizontalOptions = left ? LayoutOptions.End : LayoutOptions.Start;
        _tab.StrokeShape = new RoundRectangle { CornerRadius = left ? new CornerRadius(0, 16, 16, 0) : new CornerRadius(16, 0, 0, 16) };
        UpdateHiddenX();
    }

    /// <summary>Shut = slid off its edge until only the tab is left, its outer side touching the screen edge.</summary>
    private void UpdateHiddenX()
    {
        double travel = Math.Max(40, Width - TabWidth);
        _hiddenX = DockLeft ? -travel : travel;
    }

    /// <summary>How far up or down it can go: about to the top and bottom of the page it floats over.</summary>
    private double MaxY()
    {
        double container = (Parent as VisualElement)?.Height ?? 0;
        return container > 0 && Height > 0 ? Math.Max(0, (container - Height) / 2 - 2) : 0;
    }

    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (Width <= 0) return;
        UpdateHiddenX();
        TranslationX = IsOpen ? 0 : _hiddenX;
        _scrim.IsVisible = !IsOpen;

        // Restore the remembered height (once), then keep it within the space now available.
        double maxY = MaxY();
        if (!_yLoaded && maxY > 0)
        {
            TranslationY = Math.Clamp(_prefKey is null ? TranslationY : Preferences.Get(_prefKey + "_y", 0.0), -maxY, maxY);
            _yLoaded = true;
        }
        else TranslationY = Math.Clamp(TranslationY, -maxY, maxY);
    }

    private void OnPan(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _axis = 0;
                _flipped = false;
                _startX = _origX = TranslationX;
                _startY = TranslationY;
                break;
            case GestureStatus.Running:
                if (_flipped) break;   // just switched sides: the rest of this drag is spent
                // Where it was, plus the finger's exact screen travel — the grabbed spot stays under the finger.
                var (dx, dy) = ScreenTouch.Travel();
                if (_axis == 0)   // not picked yet: take the direction the drag clearly favours
                {
                    if (Math.Abs(dx) < AxisPick && Math.Abs(dy) < AxisPick) break;
                    _axis = Math.Abs(dx) >= Math.Abs(dy) ? 1 : 2;
                    (_anchorX, _anchorY) = (dx, dy);
                }
                else if (_axis == 2 && Math.Abs(dx - _anchorX) > AxisSwitch)   // pushed clearly sideways
                {
                    _axis = 1;
                    _startX = TranslationX - dx;   // carry on from here, no jump
                    (_anchorX, _anchorY) = (dx, dy);
                }
                else if (_axis == 1 && Math.Abs(dy - _anchorY) > AxisSwitch)   // pushed clearly up/down
                {
                    _axis = 2;
                    _startY = TranslationY - dy;
                    (_anchorX, _anchorY) = (dx, dy);
                }
                if (_axis == 1)
                {
                    // Between shut and open, plus up to SideSwitch past open; pulled that far, it flips sides.
                    double side = Math.Sign(_hiddenX), x = _startX + dx;
                    if (-x * side >= SideSwitch) { FlipSide(); break; }
                    TranslationX = Math.Clamp(x, Math.Min(-side * SideSwitch, _hiddenX), Math.Max(-side * SideSwitch, _hiddenX));
                }
                else
                    TranslationY = Math.Clamp(_startY + dy, -MaxY(), MaxY());
                break;
            default:   // Completed / Canceled
                if (_prefKey is not null) Preferences.Set(_prefKey + "_y", TranslationY);
                if (_flipped) break;
                // open or shut by the sideways travel over the whole drag, with a small commit threshold
                double moved = (TranslationX - _origX) * Math.Sign(_hiddenX);   // + = toward shut
                if (moved > Commit) IsOpen = false;
                else if (moved < -Commit) IsOpen = true;
                Animate();   // settle there, also when it stays as it was
                break;
        }
    }

    /// <summary>Pulled past fully open: it jumps to dock on the other side, open, with the tab moved over.</summary>
    private void FlipSide()
    {
        _flipped = true;
        DockLeft = !DockLeft;
        if (_prefKey is not null) Preferences.Set(_prefKey + "_left", DockLeft);
        IsOpen = true;
        this.AbortAnimation("TranslateTo");
        TranslationX = 0;
        _scrim.IsVisible = false;
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { /* no haptics */ }
    }

    private void Animate()
    {
        _scrim.IsVisible = !IsOpen;   // catcher plane only while shut, so what is inside works when open
        // X only; keep the height so opening/shutting doesn't yank it back to the middle.
        _ = this.TranslateTo(IsOpen ? 0 : _hiddenX, TranslationY, 150, Easing.CubicOut);
    }
}
