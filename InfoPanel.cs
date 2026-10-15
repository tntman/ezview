using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EzView;

// Right side panel: everything known about what's on screen, in titled sections.
class InfoPanel
{
    public readonly Border View;
    readonly StackPanel _body = new() { Margin = new(0, 0, 0, 20) };
    string _shown = "";

    public InfoPanel() => View = Ui.SidePanel(left: false, new ScrollViewer { Content = _body });

    public void Show(List<(string Title, List<(string Key, string Value)> Rows)> sections)
    {
        // Rebuild only on change, so periodic refreshes don't reset the scroll position.
        var key = string.Join("|", sections.SelectMany(s => s.Rows.Select(r => r.Key + r.Value).Prepend(s.Title)));
        if (key == _shown) return;
        _shown = key;
        _body.Children.Clear();
        foreach (var (title, rows) in sections.Where(s => s.Rows.Count > 0))
        {
            _body.Children.Add(Ui.Header(title));
            foreach (var (k, v) in rows)
            {
                var grid = new Grid { ColumnDefinitions = new("110,*"), Margin = new(20, 3, 20, 3) };
                var label = new TextBlock { Text = k, Foreground = Ui.Muted, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
                var value = new SelectableTextBlock { Text = v, Foreground = Ui.Text, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
                _body.Children.Add(grid);
            }
        }
    }
}
