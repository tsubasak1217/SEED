using System;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Runtime;

namespace SEEDEditor.Panels;

public partial class InspectorPanel : UserControl
{
    // ── Runtime connection ────────────────────────────────────
    private RuntimeManager? _runtime;
    private int             _currentId = -1;

    // ── Transform fields (cached for SET_TRANSFORM) ──────────
    private TextBox? _tbPx, _tbPy, _tbPz;
    private TextBox? _tbEx, _tbEy, _tbEz;
    private TextBox? _tbSx, _tbSy, _tbSz;

    public InspectorPanel()
    {
        InitializeComponent();
    }

    // ── Runtime binding ──────────────────────────────────────

    public void SetRuntime(RuntimeManager runtime)
    {
        if (_runtime is not null)
        {
            _runtime.SelectionChanged  -= OnSelectionChanged;
            _runtime.ActorDataReceived -= OnActorDataReceived;
        }
        _runtime = runtime;
        _runtime.SelectionChanged  += OnSelectionChanged;
        _runtime.ActorDataReceived += OnActorDataReceived;
    }

    // ── Runtime events ───────────────────────────────────────

    private void OnSelectionChanged(int id)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _currentId = id;
            if (id < 0)
            {
                ShowNoSelection();
            }
            else
            {
                ActorNameBlock.Text = $"Actor #{id}";
                ActorModelBlock.Visibility = Visibility.Collapsed;
                ComponentStack.Children.Clear();
                ComponentScroll.Visibility  = Visibility.Visible;
                NoSelectionBlock.Visibility = Visibility.Collapsed;
                _runtime?.SendToRuntime($"GET_ACTOR:{id}");
            }
        });
    }

    private void OnActorDataReceived(string json)
    {
        Dispatcher.InvokeAsync(() =>
        {
            try { BuildInspector(json); }
            catch (Exception ex) { EditorLog.Write($"InspectorPanel: JSON parse error: {ex.Message}"); }
        });
    }

    // ── UI building ──────────────────────────────────────────

    private void ShowNoSelection()
    {
        ActorNameBlock.Text         = "選択なし";
        ActorModelBlock.Visibility  = Visibility.Collapsed;
        ComponentScroll.Visibility  = Visibility.Collapsed;
        NoSelectionBlock.Visibility = Visibility.Visible;
        ComponentStack.Children.Clear();
        _tbPx = _tbPy = _tbPz = null;
        _tbEx = _tbEy = _tbEz = null;
        _tbSx = _tbSy = _tbSz = null;
    }

    private void BuildInspector(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name      = root.TryGetProperty("name",       out var np) ? np.GetString() ?? "" : "";
        var modelPath = root.TryGetProperty("model_path", out var mp) ? mp.GetString() ?? "" : "";

        ActorNameBlock.Text = string.IsNullOrEmpty(name) ? $"Actor #{_currentId}" : name;
        if (!string.IsNullOrEmpty(modelPath))
        {
            ActorModelBlock.Text       = System.IO.Path.GetFileName(modelPath);
            ActorModelBlock.Visibility = Visibility.Visible;
        }

        ComponentStack.Children.Clear();

        // ── Transform section ──
        if (root.TryGetProperty("transform", out var tf))
        {
            float px = Fp(tf, "px"), py = Fp(tf, "py"), pz = Fp(tf, "pz");
            float ex = Fp(tf, "ex"), ey = Fp(tf, "ey"), ez = Fp(tf, "ez");
            float sx = Fp(tf, "sx"), sy = Fp(tf, "sy"), sz = Fp(tf, "sz");

            var section = BuildSection("Transform");
            var grid = BuildXYZGrid();
            grid.Tag = "transform";

            (_tbPx, _tbPy, _tbPz) = AddXYZRow(grid, 0, "位置",  px, py, pz, "#E06C75", "#98C379", "#61AFEF");
            (_tbEx, _tbEy, _tbEz) = AddXYZRow(grid, 1, "回転",  ex, ey, ez, "#E06C75", "#98C379", "#61AFEF");
            (_tbSx, _tbSy, _tbSz) = AddXYZRow(grid, 2, "スケール", sx, sy, sz, "#E06C75", "#98C379", "#61AFEF");

            ((StackPanel)section.Child).Children.Add(grid);
            ComponentStack.Children.Add(section);
        }

        // ── Model section ──
        if (!string.IsNullOrEmpty(modelPath))
        {
            var section = BuildSection("Model");
            var sp = (StackPanel)section.Child;
            sp.Children.Add(BuildPropertyRow("パス", System.IO.Path.GetFileName(modelPath)));
            ComponentStack.Children.Add(section);
        }

        ComponentScroll.Visibility  = Visibility.Visible;
        NoSelectionBlock.Visibility = Visibility.Collapsed;
    }

    // ── Section / grid helpers ────────────────────────────────

    private static Border BuildSection(string title)
    {
        var sp = new StackPanel();
        var header = new TextBlock
        {
            Text       = title,
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize   = 11,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, 6),
        };
        sp.Children.Add(header);

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(8, 6, 8, 6),
            Margin          = new Thickness(4, 0, 4, 4),
            Child           = sp,
        };
    }

    private static Grid BuildXYZGrid()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    private (TextBox x, TextBox y, TextBox z) AddXYZRow(
        Grid grid, int row,
        string label,
        float vx, float vy, float vz,
        string colorX, string colorY, string colorZ)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var tbX = MakeAxisField(vx, colorX); Grid.SetRow(tbX, row); Grid.SetColumn(tbX, 2); grid.Children.Add(tbX);
        var tbY = MakeAxisField(vy, colorY); Grid.SetRow(tbY, row); Grid.SetColumn(tbY, 4); grid.Children.Add(tbY);
        var tbZ = MakeAxisField(vz, colorZ); Grid.SetRow(tbZ, row); Grid.SetColumn(tbZ, 6); grid.Children.Add(tbZ);

        var lblX = MakeAxisLabel("X", colorX); Grid.SetRow(lblX, row); Grid.SetColumn(lblX, 1); grid.Children.Add(lblX);
        var lblY = MakeAxisLabel("Y", colorY); Grid.SetRow(lblY, row); Grid.SetColumn(lblY, 3); grid.Children.Add(lblY);
        var lblZ = MakeAxisLabel("Z", colorZ); Grid.SetRow(lblZ, row); Grid.SetColumn(lblZ, 5); grid.Children.Add(lblZ);

        tbX.KeyDown += OnFieldKeyDown;
        tbY.KeyDown += OnFieldKeyDown;
        tbZ.KeyDown += OnFieldKeyDown;
        tbX.LostFocus += OnFieldLostFocus;
        tbY.LostFocus += OnFieldLostFocus;
        tbZ.LostFocus += OnFieldLostFocus;

        return (tbX, tbY, tbZ);
    }

    private static TextBlock MakeAxisLabel(string text, string colorHex)
    {
        return new TextBlock
        {
            Text              = text,
            Foreground        = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
            FontSize          = 10,
            FontWeight        = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    private static TextBox MakeAxisField(float value, string colorHex)
    {
        return new TextBox
        {
            Text              = Fmt(value),
            Background        = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Foreground        = new SolidColorBrush(Colors.White),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness   = new Thickness(1),
            FontSize          = 11,
            Padding           = new Thickness(3, 1, 3, 1),
            Margin            = new Thickness(1, 1, 2, 1),
            VerticalAlignment = VerticalAlignment.Center,
            SelectionBrush    = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)) { Opacity = 0.4 },
        };
    }

    private static UIElement BuildPropertyRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var val = new TextBlock
        {
            Text              = value,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
        Grid.SetColumn(val, 1); grid.Children.Add(val);
        return grid;
    }

    // ── Field commit ─────────────────────────────────────────

    private void OnFieldKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            CommitTransform();
            (sender as TextBox)?.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Refresh from runtime to discard edit
            if (_currentId >= 0) _runtime?.SendToRuntime($"GET_ACTOR:{_currentId}");
            e.Handled = true;
        }
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e)
    {
        CommitTransform();
    }

    private void CommitTransform()
    {
        if (_currentId < 0) return;
        if (_tbPx is null) return;

        if (!TryParseAll(out float px, out float py, out float pz,
                         out float ex, out float ey, out float ez,
                         out float sx, out float sy, out float sz)) return;

        var msg = FormattableString.Invariant(
            $"SET_TRANSFORM:{_currentId},{px},{py},{pz},{ex},{ey},{ez},{sx},{sy},{sz}");
        _runtime?.SendToRuntime(msg);
    }

    private bool TryParseAll(
        out float px, out float py, out float pz,
        out float ex, out float ey, out float ez,
        out float sx, out float sy, out float sz)
    {
        px = py = pz = ex = ey = ez = sx = sy = sz = 0f;
        return TryParse(_tbPx, out px) && TryParse(_tbPy, out py) && TryParse(_tbPz, out pz)
            && TryParse(_tbEx, out ex) && TryParse(_tbEy, out ey) && TryParse(_tbEz, out ez)
            && TryParse(_tbSx, out sx) && TryParse(_tbSy, out sy) && TryParse(_tbSz, out sz);
    }

    private static bool TryParse(TextBox? tb, out float value)
    {
        value = 0f;
        return tb is not null &&
               float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ── Helpers ──────────────────────────────────────────────

    private static float Fp(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) ? v.GetSingle() : 0f;

    private static string Fmt(float v) =>
        v.ToString("F3", CultureInfo.InvariantCulture);
}
