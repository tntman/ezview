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
        _header.Text = $"QUEUE  ·  {entries.Count}";
        _rows.Children.Clear();
        for (int i = 0; i < entries.Count; i++)
        {
            var index = i;
            bool active = i == current;
            var fg = active ? Ui.Accent : Ui.Text;
            var icon = Ui.Icon(entries[i].Glyph);
            icon.Foreground = active ? Ui.Accent : Ui.Muted;
            icon.Margin = new(0, 0, 14, 0);
            var remove = Ui.Icon("", 10);
            remove.Foreground = Ui.Muted;
            remove.Margin = new(8, 0, 0, 0);
            remove.Opacity = 0;
            remove.Cursor = new Cursor(StandardCursorType.Hand);
            remove.PointerPressed += (_, e) => { _remove(index); e.Handled = true; };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(remove, Dock.Right);
            var row = new Border
            {
                Height = 40, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                BorderBrush = active ? Ui.Accent : Brushes.Transparent, BorderThickness = new(3, 0, 0, 0),
                Padding = new(17, 0, 16, 0),
                Child = new DockPanel
                {
                    Children =
                    {
                        icon, remove,
                        new TextBlock { Text = entries[i].Name, Foreground = fg, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                    },
                },
            };
            row.PointerEntered += (_, _) => { row.Background = Ui.Hover; remove.Opacity = 1; };
            row.PointerExited += (_, _) => { row.Background = Brushes.Transparent; remove.Opacity = 0; };
            row.PointerPressed += (_, e) => { if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) _select(index); };
            _rows.Children.Add(row);
            if (active) Avalonia.Threading.Dispatcher.UIThread.Post(() => row.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
