using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClickUpTimer;

internal sealed class PreferredListPicker : StackPanel
{
    private const int BrowseLimit = 50;
    private const int SearchLimit = 200;
    private readonly TextBox search = new() { Height = 32, Padding = new Thickness(7), MaxLength = 300 };
    private readonly TextBlock selected = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 7, 0, 5) };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 5) };
    private readonly ListBox results = new() { DisplayMemberPath = "Name", Height = 150 };
    private List<Choice> choices = [];
    private bool updating;

    internal Choice? SelectedChoice { get; private set; }
    internal int AvailableCount => choices.Count;
    internal int DisplayedCount => results.Items.Count;
    internal TextBox SearchBox => search;

    internal PreferredListPicker()
    {
        Children.Add(new TextBlock { Text = "Search by Space, Folder, List name, or list ID", FontSize = 12, Margin = new Thickness(0, 0, 0, 5) });
        Children.Add(search); Children.Add(selected); Children.Add(summary); Children.Add(results);
        VirtualizingPanel.SetIsVirtualizing(results, true);
        VirtualizingPanel.SetVirtualizationMode(results, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(results, true);
        search.TextChanged += (_, _) => RefreshResults();
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && search.Text.Length > 0)
            {
                search.Clear();
                e.Handled = true;
            }
            else if (e.Key == Key.Down && results.Items.Count > 0)
            {
                results.SelectedIndex = Math.Max(0, results.SelectedIndex);
                results.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && results.Items.Count > 0)
            {
                results.SelectedIndex = Math.Max(0, results.SelectedIndex);
                e.Handled = true;
            }
        };
        results.SelectionChanged += (_, _) =>
        {
            if (updating || results.SelectedItem is not Choice choice) return;
            SelectedChoice = choice; UpdateSelected();
        };
    }

    internal void SetChoices(IEnumerable<Choice> available, Choice? preferred = null, bool resetSearch = false, bool loading = false)
    {
        if (resetSearch) { search.Clear(); SelectedChoice = preferred; }
        else SelectedChoice ??= preferred;
        choices = available.GroupBy(c => c.Id).Select(g => g.First()).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        SelectedChoice ??= choices.FirstOrDefault();
        if (SelectedChoice is not null)
        {
            var refreshed = choices.FirstOrDefault(c => c.Id == SelectedChoice.Id);
            if (refreshed is not null) SelectedChoice = refreshed;
            else choices.Add(SelectedChoice);
        }
        UpdateSelected(); RefreshResults(loading);
    }

    internal void Select(string? id)
    {
        SelectedChoice = choices.FirstOrDefault(c => c.Id == id);
        UpdateSelected(); RefreshResults();
    }

    private void UpdateSelected() => selected.Text = SelectedChoice is null ? "Selected: none" : "Selected: " + SelectedChoice.Name;

    private void RefreshResults(bool loading = false)
    {
        var terms = search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matches = choices.Where(c => terms.All(term => c.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
            || c.Id.Contains(term, StringComparison.OrdinalIgnoreCase))).ToList();
        var limit = terms.Length == 0 ? BrowseLimit : SearchLimit;
        var shown = matches.Take(limit).ToList();
        updating = true;
        try
        {
            results.ItemsSource = shown;
            results.SelectedItem = shown.FirstOrDefault(c => c.Id == SelectedChoice?.Id);
        }
        finally { updating = false; }
        var action = terms.Length == 0 && choices.Count > BrowseLimit ? " Type above to search all lists." : "";
        var capped = matches.Count > shown.Count ? $" Showing the first {shown.Count} of {matches.Count} matches; keep typing to narrow." : $" {matches.Count} match{(matches.Count == 1 ? "" : "es")}.";
        summary.Text = $"{choices.Count} list{(choices.Count == 1 ? "" : "s")} available.{capped}{action}" + (loading ? " Still loading…" : "");
    }
}
