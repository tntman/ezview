using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EzView;

// Left side panel: the AOVs (layers) of a multi-layer EXR. Click one to show it; ↑ ↓ step through them.
class AovPanel
{
    public readonly Border View;
    readonly StackPanel _rows = new();
    readonly TextBlock _header = Ui.Header("AOVs");
    readonly Action<string> _select;

    public AovPanel(Action<string> select)
    {
        _select = select;
        DockPanel.SetDock(_header, Dock.Top);
        View = Ui.SidePanel(left: true, new DockPanel
        {
            Children = { _header, new ScrollViewer { Content = _rows, Margin = new(0, 0, 0, 12) } },
        });
    }

    public void Refresh(List<(string Layer, string Channels)>? layers, string current)
    {
        _rows.Children.Clear();
        if (layers == null)
        {
            _header.Text = "AOVS";
            _rows.Children.Add(new TextBlock { Text = "Open a multi-layer EXR to see its AOVs.", Foreground = Ui.Muted, FontSize = 13, Margin = new(20, 4), TextWrapping = TextWrapping.Wrap });
            return;
        }
        _header.Text = $"AOVS  ·  {layers.Count}";
        foreach (var (layer, channels) in layers)
        {
            bool active = layer == current;
            var name = new TextBlock
            {
                Text = layer.Length == 0 ? "RGBA" : layer, FontSize = 14, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Foreground = active ? Ui.Accent : Ui.Text,
            };
            var chans = new TextBlock { Text = channels, FontSize = 11, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0) };
            DockPanel.SetDock(chans, Dock.Right);
            var row = new Border
            {
                Height = 36, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand),
                BorderThickness = new(3, 0, 0, 0), BorderBrush = active ? Ui.Accent : Brushes.Transparent, Padding = new(17, 0, 16, 0),
                Child = new DockPanel { Children = { chans, name } },
            };
            ToolTip.SetTip(row, name.Text);
            row.PointerEntered += (_, _) => row.Background = Ui.Hover;
            row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
            row.PointerPressed += (_, e) => { if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) _select(layer); };
            _rows.Children.Add(row);
            if (active) Avalonia.Threading.Dispatcher.UIThread.Post(() => row.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
