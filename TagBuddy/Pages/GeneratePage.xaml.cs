using TagBuddy.Controls;
using TagBuddy.Models;
using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>The Barcode tab: a linear format, its text, and the code rendered live.</summary>
[QueryProperty(nameof(IncomingValue), AppNavigation.Value)]
[QueryProperty(nameof(IncomingFormat), AppNavigation.Format)]
public partial class GeneratePage : CodeGeneratorPage
{
    public string? IncomingValue { get; set; }
    public string? IncomingFormat { get; set; }

    public GeneratePage(SavedService saved) : base(saved)
    {
        InitializeComponent();
        FormatPicker.ItemsSource = BarcodeTypes.Linear.Select(f => f.Label).ToList();
        FormatPicker.SelectedIndex = 0;
    }

    protected override InputView ValueInput => ValueEntry;
    protected override Label ErrorText => ErrorLabel;
    protected override View CodeCard => Card;
    protected override PreviewView CodePreview => PreviewImage;
    protected override CaptionSection Caption => CaptionArea;
    protected override View ActionRow => Actions;
    protected override Button OpenLink => OpenLinkButton;
    protected override ScrollView PageScroller => Scroller;
    protected override View BottomPad => BottomSpacer;
    protected override string AlreadySavedMessage => "This barcode is already saved.";

    protected override void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (IncomingValue is null) return;

        var format = BarcodeTypes.Get(IncomingFormat ?? BarcodeTypes.Linear[0].Key);
        var index = BarcodeTypes.Linear.ToList().IndexOf(format);
        FormatPicker.SelectedIndex = index < 0 ? 0 : index;
        ValueEntry.Text = IncomingValue;
        IncomingValue = null;
        IncomingFormat = null;
    }

    private FormatInfo CurrentFormat =>
        BarcodeTypes.Linear[Math.Max(0, FormatPicker.SelectedIndex)];

    protected override SavedItem CurrentCode() => new() { Value = Value, Format = CurrentFormat.Key };

    private void OnFormatChanged(object? sender, EventArgs e)
    {
        HintLabel.Text = CurrentFormat.Hint;
        Refresh();
    }

    private void OnValueChanged(object? sender, TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var format = CurrentFormat;
        var error = BarcodeTypes.Validate(format.Key, Value);
        var matrix = error is null ? BarcodeRenderer.TryEncode(format, Value, null, out error) : null;
        ShowCode(matrix is not null, error);
    }
}
