using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EzView;

// Left side panel: the play queue. Current item is accent-coloured with a bar on the left.
class QueuePanel
{
    public readonly Border View;
    readonly StackPanel _rows = new();
    readonly TextBlock _header = Ui.Header("Queue");
    readonly Action<int> _select, _remove;
    readonly List<(Border Row, TextBlock Icon, TextBlock Name)> _items = [];
    IReadOnlyList<Entry>? _shown;
    int _current = -1;

    public QueuePanel(Action<int> select, Action<int> remove, Action add)
    {
        _select = select;
        _remove = remove;
        var addButton = new TextBlock
        {
            Text = "Add files  +", Foreground = Ui.Accent, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Margin = new(20, 12, 20, 20), Cursor = new Cursor(StandardCursorType.Hand),
        };
        addButton.PointerPressed += (_, e) => { add(); e.Handled = true; };
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(addButton, Dock.Bottom);
        View = Ui.SidePanel(left: true, new DockPanel
        {
            Children = { _header, addButton, new ScrollViewer { Content = _rows } },
        });
    }

    public void Refresh(IReadOnlyList<Entry> entries, int current)
    {
        // Same list, only the current item moved: restyle the two rows.
        if (ReferenceEquals(entries, _shown) && entries.Count == _items.Count)
        {
            if (current != _current)
            {
                if (_current >= 0 && _current < _items.Count) Style(_items[_current], false);
                if (current >= 0 && current < _items.Count) Style(_items[current], true);
                _current = current;
            }
            if (current >= 0 && current < _items.Count) Reveal(_items[current].Row);
            return;
        }
        (_shown, _current) = (entries, current);
        _header.Text = $"QUEUE  ·  {entries.Count}";
        _rows.Children.Clear();
        _items.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            var index = i;
            var icon = Ui.Icon(entries[i].Glyph);
            icon.Margin = new(0, 0, 14, 0);
            var remove = Ui.Icon("", 10);
            remove.Foreground = Ui.Muted;
            remove.Margin = new(8, 0, 0, 0);
            remove.Opacity = 0;
            remove.Cursor = new Cursor(StandardCursorType.Hand);
            remove.PointerPressed += (_, e) => { _remove(index); e.Handled = true; };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(remove, Dock.Right);
            var name = new TextBlock { Text = entries[i].Name, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var row = new Border
            {
                Height = 40, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new(3, 0, 0, 0), Padding = new(17, 0, 16, 0),
                Child = new DockPanel { Children = { icon, remove, name } },
            };
            var item = (row, icon, name);
            Style(item, i == current);
            _items.Add(item);
            row.PointerEntered += (_, _) => { row.Background = Ui.Hover; remove.Opacity = 1; };
            row.PointerExited += (_, _) => { row.Background = Brushes.Transparent; remove.Opacity = 0; };
            row.PointerPressed += (_, e) => { if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) _select(index); };
            _rows.Children.Add(row);
            if (i == current) Reveal(row);
        }
    }

    // Current item: accent bar, name and icon.
    static void Style((Border Row, TextBlock Icon, TextBlock Name) item, bool active)
    {
        item.Row.BorderBrush = active ? Ui.Accent : Brushes.Transparent;
        item.Name.Foreground = active ? Ui.Accent : Ui.Text;
        item.Icon.Foreground = active ? Ui.Accent : Ui.Muted;
    }

    static void Reveal(Control row) => Avalonia.Threading.Dispatcher.UIThread.Post(() => row.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
}
