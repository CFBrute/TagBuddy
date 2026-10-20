namespace BarcodeBuddy.Models;

public class BarcodeItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Format { get; set; } = "Code128";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Value : Name;
    public string FormatLabel => BarcodeTypes.LabelFor(Format);
}
