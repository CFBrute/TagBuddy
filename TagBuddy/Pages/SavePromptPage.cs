using TagBuddy.Services;

namespace TagBuddy.Pages;

/// <summary>One dialog for saving: a name and a group together, so there is no second step. The group starts at
/// "No group" and can be changed here, including making a new one. Returns the name and group, or null if cancelled.</summary>
public sealed class SavePromptPage : ContentPage
{
    private const string NoGroup = "No group", NewGroup = "New group…";

    /// <summary>Shows the dialog and returns the chosen name and group, or null when cancelled.</summary>
    public static async Task<(string Name, string Group)?> ShowAsync(Page page, SavedService saved, string title, string suggested, string placeholder = "")
    {
        await saved.LoadAsync();
        var dialog = new SavePromptPage(title, suggested, placeholder, saved.Groups);
        await page.Navigation.PushModalAsync(dialog);
        return await dialog.Result;
    }

    private readonly TaskCompletionSource<(string Name, string Group)?> _result = new();
    private readonly Entry _name;
    private readonly Picker _group;
    private readonly List<string> _groups;

    public Task<(string Name, string Group)?> Result => _result.Task;

    public SavePromptPage(string title, string suggested, string placeholder, IReadOnlyList<string> groups)
    {
        _groups = groups.ToList();
        BackgroundColor = Color.FromArgb("#80000000");   // dim the tab behind

        _name = new Entry { Text = suggested, Placeholder = placeholder, MaxLength = 60 };
        _group = new Picker { Title = "Group" };
        _group.Items.Add(NoGroup);
        foreach (var g in _groups) _group.Items.Add(g);
        _group.Items.Add(NewGroup);
        _group.SelectedIndex = 0;
        _group.SelectedIndexChanged += OnGroupChanged;

        var save = new Button { Text = "Save" };
        save.Clicked += (_, _) => Finish();
        var cancel = new Button { Text = "Cancel", BackgroundColor = Color.FromArgb("#6B7280"), TextColor = Colors.White };
        cancel.Clicked += (_, _) => Close(null);

        var card = new Border
        {
            Padding = 18,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Fill,
            Margin = 24,
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    new Label { Text = title, FontFamily = "OpenSansSemibold", FontSize = 17 },
                    new Label { Text = "Name", FontSize = 12, TextColor = Colors.Gray },
                    _name,
                    new Label { Text = "Group", FontSize = 12, TextColor = Colors.Gray },
                    _group,
                    new HorizontalStackLayout
                    {
                        Spacing = 8, HorizontalOptions = LayoutOptions.End,
                        Children = { cancel, save },
                    },
                },
            },
        };
        card.SetAppThemeColor(BackgroundColorProperty, Colors.White, Color.FromArgb("#1F2937"));
        Content = card;
    }

    private async void OnGroupChanged(object? sender, EventArgs e)
    {
        // "New group…" asks for a name; cancelling it falls back to "No group".
        if (_group.SelectedIndex != _group.Items.Count - 1) return;
        var name = await DisplayPromptAsync("New group", "A name for the group:", accept: "OK", cancel: "Cancel", maxLength: 40);
        if (!string.IsNullOrWhiteSpace(name))
        {
            var trimmed = name.Trim();
            int existing = _groups.FindIndex(g => string.Equals(g, trimmed, StringComparison.CurrentCultureIgnoreCase));
            if (existing >= 0) { _group.SelectedIndex = existing + 1; return; }
            _group.Items.Insert(_group.Items.Count - 1, trimmed);
            _groups.Add(trimmed);
            _group.SelectedIndex = _group.Items.Count - 2;
        }
        else _group.SelectedIndex = 0;
    }

    private void Finish()
    {
        var name = _name.Text?.Trim() ?? "";
        int i = _group.SelectedIndex;
        string group = i <= 0 || i >= _group.Items.Count - 1 ? "" : _group.Items[i];
        Close((name, group));
    }

    private async void Close((string, string)? result)
    {
        if (Navigation.ModalStack.Contains(this)) await Navigation.PopModalAsync();
        _result.TrySetResult(result);
    }

    /// <summary>Android back button cancels.</summary>
    protected override bool OnBackButtonPressed()
    {
        Close(null);
        return true;
    }
}
