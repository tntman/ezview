using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EzView;

// Left side panel: the 3D scene hierarchy. Chevrons expand/collapse, the eye hides a subtree, S solos it.
class OutlinerPanel
{
    public readonly Border View;
    readonly StackPanel _rows = new();
    readonly TextBlock _header = Ui.Header("Outliner");
    readonly Func<ModelView?> _model;

    public OutlinerPanel(Func<ModelView?> model)
    {
        _model = model;
        DockPanel.SetDock(_header, Dock.Top);
        View = Ui.SidePanel(left: true, new DockPanel
        {
            Children = { _header, new ScrollViewer { Content = _rows, Margin = new(0, 0, 0, 12) } },
        });
    }

    // ponytail: rows are rebuilt on every toggle and not virtualized; fine for thousands of visible rows
    public void Refresh()
    {
        _rows.Children.Clear();
        var root = _model()?.Outline;
        if (root == null)
        {
            _header.Text = "OUTLINER";
            _rows.Children.Add(new TextBlock { Text = "Open a 3D file to see its scene.", Foreground = Ui.Muted, FontSize = 13, Margin = new(20, 4) });
            return;
        }
        _header.Text = $"OUTLINER  ·  {root.Descendants().Count()}";
        // First look at a scene: open the top two levels.
        if (root.Children.Count > 0 && !root.Children.Any(c => c.Expanded) && root.Children.Count <= 8)
            foreach (var c in root.Children) c.Expanded = true;
        foreach (var c in root.Children) AddRows(c, 0, true);
    }

    static string Glyph(OutlineNode n) => n.Type switch
    {
        "Mesh" or "PolyMesh" or "SubD" => "",
        "PointInstancer" => "",
        "Camera" => "",
        "Material" or "Shader" => "",
        "Light" or "SphereLight" or "DistantLight" or "DomeLight" or "RectLight" => "",
        _ => n.Children.Count > 0 ? "" : "",
    };

    void AddRows(OutlineNode n, int depth, bool parentShown)
    {
        var model = _model();
        bool shown = parentShown && n.Visible;
        bool solo = model?.Solo == n;

        var chevron = Ui.Icon(n.Children.Count == 0 ? "" : n.Expanded ? "" : "", 9);
        chevron.Width = 16;
        chevron.Foreground = Ui.Muted;
        chevron.Margin = new(depth * 14, 0, 2, 0);
        chevron.Cursor = new Cursor(StandardCursorType.Hand);
        void Toggle(bool branch)
        {
            if (n.Children.Count == 0) return;
            n.Expanded = !n.Expanded;
            if (branch) foreach (var d in n.Descendants()) d.Expanded = n.Expanded;
            Refresh();
        }
        // Shift-click expands or collapses the whole branch.
        chevron.PointerPressed += (_, e) => { Toggle(e.KeyModifiers.HasFlag(KeyModifiers.Shift)); e.Handled = true; };

        var icon = Ui.Icon(Glyph(n), 13);
        icon.Foreground = solo ? Ui.Accent : Ui.Muted;
        icon.Margin = new(0, 0, 10, 0);

        var name = new TextBlock
        {
            Text = n.Name, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = solo ? Ui.Accent : shown ? Ui.Text : Ui.Muted, Opacity = shown ? 1 : 0.6,
        };
        ToolTip.SetTip(name, n.Type.Length > 0 ? $"{n.Name}  ({n.Type})" : n.Name);

        var soloButton = new TextBlock
        {
            Text = "S", FontSize = 11, FontWeight = FontWeight.Bold, Width = 22, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Foreground = solo ? Ui.Accent : Ui.Muted, Opacity = solo ? 1 : 0,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(soloButton, "Solo");
        soloButton.PointerPressed += (_, e) =>
        {
            if (model == null) return;
            model.Solo = solo ? null : n;
            model.UpdateVisibility();
            Refresh();
            e.Handled = true;
        };

        var eye = Ui.Icon(n.Visible ? "" : "", 13);
        eye.Width = 24;
        eye.Foreground = n.Visible ? Ui.Muted : Ui.Accent;
        eye.Opacity = n.Visible ? 0 : 1;
        eye.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(eye, n.Visible ? "Hide" : "Show");
        eye.PointerPressed += (_, e) =>
        {
            n.Visible = !n.Visible;
            model?.UpdateVisibility();
            Refresh();
            e.Handled = true;
        };

        var grid = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto,Auto"), Margin = new(16, 0, 10, 0) };
        foreach (var (c, i) in new Control[] { chevron, icon, name, soloButton, eye }.Select((c, i) => (c, i)))
        {
            Grid.SetColumn(c, i);
            grid.Children.Add(c);
        }
        var row = new Border
        {
            Height = 30, Background = Brushes.Transparent, Child = grid,
            BorderBrush = solo ? Ui.Accent : Brushes.Transparent, BorderThickness = new(3, 0, 0, 0),
        };
        // Toggles fade in on hover (always visible while active), like the queue's remove button.
        row.PointerEntered += (_, _) => { row.Background = Ui.Hover; soloButton.Opacity = 1; eye.Opacity = 1; };
        row.PointerExited += (_, _) => { row.Background = Brushes.Transparent; soloButton.Opacity = solo ? 1 : 0; eye.Opacity = n.Visible ? 0 : 1; };
        row.DoubleTapped += (_, e) => { Toggle(false); e.Handled = true; };
        _rows.Children.Add(row);

        if (n.Expanded)
            foreach (var c in n.Children) AddRows(c, depth + 1, shown);
    }
}
