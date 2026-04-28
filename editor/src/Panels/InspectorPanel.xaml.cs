using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SEEDEditor.Runtime;
using SEEDEditor.Scripting;

namespace SEEDEditor.Panels;

public partial class InspectorPanel : UserControl
{
    // ── Runtime connection ────────────────────────────────────
    private RuntimeManager? _runtime;
    private int             _currentActorId = -1;

    // ── Mode ─────────────────────────────────────────────────
    private bool     _isActorEditMode       = false;
    /// <summary>
    /// 仮想アクターノード（DFS ID）での選択かどうか。
    /// シーンモードでもアクターが仮想選択された場合は SET_ACTOR_TRANSFORM を使う。
    /// </summary>
    private bool     _isVirtualActorSelected = false;
    private int      _selectedSlotIdx    = -1;   // 選択中のコンポーネントスロット
    private int      _componentSlotCount = 0;
    private SlotInfo? _copiedSlot        = null;  // Ctrl+C でコピーしたスロット

    // ── Script state ─────────────────────────────────────────
    // Key: "{actorId}:{slotIdx}", Value: {fieldName → valueString}
    private readonly Dictionary<string, Dictionary<string, string>> _scriptFieldValues = new();
    private readonly Dictionary<string, Type> _scriptTypeCache = new();
    private string _lastComponentsJson = "";

    // ── Transform fields ─────────────────────────────────────
    private TextBox? _tbPx, _tbPy, _tbPz;
    private TextBox? _tbEx, _tbEy, _tbEz;
    private TextBox? _tbSx, _tbSy, _tbSz;
    private bool     _isDraggingTransform = false;
    /// <summary>現在選択中のアクターが 2D Actor（CanvasTransform 持ち）かどうか。</summary>
    private bool     _isActor2D = false;

    public InspectorPanel()
    {
        InitializeComponent();
    }

    // ── Events ───────────────────────────────────────────────

    public event Action? TransformCommitted;

    // ── Runtime binding ──────────────────────────────────────

    public void SetRuntime(RuntimeManager runtime)
    {
        if (_runtime is not null)
        {
            _runtime.SelectionChanged       -= OnSelectionChanged;
            _runtime.ActorDataReceived      -= OnActorDataReceived;
            _runtime.ActorComponentsReceived -= OnActorComponentsReceived;
        }
        _runtime = runtime;
        _runtime.SelectionChanged       += OnSelectionChanged;
        _runtime.ActorDataReceived      += OnActorDataReceived;
        _runtime.ActorComponentsReceived += OnActorComponentsReceived;
    }

    public void SetActorEditMode(bool isActorMode)
    {
        _isActorEditMode = isActorMode;
        ShowNoSelection();
    }

    /// <summary>HierarchyPanel からアクター編集モードのアクター選択を受け取る。</summary>
    public void SelectActor(int dfsId)
    {
        // シーンモード・アクター編集モード共通: DFS id でアクタを選択してコンポーネントを表示する
        ClearTransformRefs();
        _currentActorId       = dfsId;
        _isVirtualActorSelected = true; // 仮想 DFS 選択 → SET_ACTOR_TRANSFORM を使う
        // Rust の SELECT 処理で ACTOR_COMPONENTS が即時プッシュされるため
        // OnActorComponentsReceived で正しいデータに更新される。
        ActorEditGrid.Visibility    = Visibility.Visible;
        ComponentScroll.Visibility  = Visibility.Collapsed;
        NoSelectionBlock.Visibility = Visibility.Collapsed;
    }

    // ── Runtime events ───────────────────────────────────────

    // アクター仮想ノード ID の下限（HierarchyPanel と合わせること）
    private const int VirtualActorNodeIdBase = 999_000_000;

    private void OnSelectionChanged(int id)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (id < 0)
            {
                ShowNoSelection();
                return;
            }

            // 仮想ノード ID（アクターツリー DFS 選択）: シーンモード・アクター編集モード共通
            if (id >= VirtualActorNodeIdBase)
            {
                SelectActor(id - VirtualActorNodeIdBase);
                return;
            }

            // レガシー: インスタンスインデックス直接選択（シーン編集モード後方互換）
            ClearTransformRefs();
            _currentActorId         = id;
            _isVirtualActorSelected = false; // インスタンス直接選択 → SET_TRANSFORM を使う
            ActorNameBlock.Text         = $"Actor #{id}";
            ActorModelBlock.Visibility  = Visibility.Collapsed;
            ComponentStack.Children.Clear();
            ComponentScroll.Visibility  = Visibility.Visible;
            ActorEditGrid.Visibility    = Visibility.Collapsed;
            NoSelectionBlock.Visibility = Visibility.Collapsed;
            _runtime?.SendToRuntime($"GET_ACTOR:{id}");
        });
    }

    private void OnActorDataReceived(string json)
    {
        if (_isActorEditMode) return;
        Dispatcher.InvokeAsync(() =>
        {
            try { BuildSceneInspector(json); }
            catch (Exception ex) { EditorLog.Write($"InspectorPanel: JSON parse error: {ex.Message}"); }
        });
    }

    private void OnActorComponentsReceived(string json)
    {
        // シーンモード・アクター編集モード共通: アクタが選択されていれば常に更新する
        if (_currentActorId < 0) return;
        Dispatcher.InvokeAsync(() =>
        {
            try { BuildActorComponentList(json); }
            catch (Exception ex) { EditorLog.Write($"InspectorPanel: ACTOR_COMPONENTS parse error: {ex.Message}"); }
        });
    }

    // ── No selection ─────────────────────────────────────────

    private void ShowNoSelection()
    {
        _isVirtualActorSelected     = false;
        _isActor2D                  = false;
        ActorNameBlock.Text         = "選択なし";
        ActorModelBlock.Visibility  = Visibility.Collapsed;
        ComponentScroll.Visibility  = Visibility.Collapsed;
        ActorEditGrid.Visibility    = Visibility.Collapsed;
        NoSelectionBlock.Visibility = Visibility.Visible;
        ComponentStack.Children.Clear();
        ComponentListStack.Children.Clear();
        BasicInfoStack.Children.Clear();
        ComponentPropsStack.Children.Clear();
        CompInfoScroll.Visibility         = Visibility.Collapsed;
        NoComponentSelectedBlock.Visibility = Visibility.Visible;
        ClearTransformRefs();
    }

    private void ClearTransformRefs()
    {
        _tbPx = _tbPy = _tbPz = null;
        _tbEx = _tbEy = _tbEz = null;
        _tbSx = _tbSy = _tbSz = null;
    }

    // ── Scene mode inspector ─────────────────────────────────

    private void BuildSceneInspector(string json)
    {
        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name      = root.TryGetProperty("name",       out var np) ? np.GetString() ?? "" : "";
        var modelPath = root.TryGetProperty("model_path", out var mp) ? mp.GetString() ?? "" : "";

        ActorNameBlock.Text = string.IsNullOrEmpty(name) ? $"Actor #{_currentActorId}" : name;
        if (!string.IsNullOrEmpty(modelPath))
        {
            ActorModelBlock.Text       = System.IO.Path.GetFileName(modelPath);
            ActorModelBlock.Visibility = Visibility.Visible;
        }

        ComponentStack.Children.Clear();
        ClearTransformRefs();

        if (root.TryGetProperty("canvas_transform", out var ct))
        {
            // 2D Actor: CanvasTransform
            _isActor2D = true;
            float px  = Fp(ct, "px"),  py  = Fp(ct, "py");
            float rot = Fp(ct, "rotation");
            float sx  = Fp(ct, "sx"),  sy  = Fp(ct, "sy");
            ComponentStack.Children.Add(BuildCanvas2DTransformSection(px, py, rot, sx, sy));
        }
        else if (root.TryGetProperty("transform", out var tf))
        {
            // 3D Actor: Transform
            _isActor2D = false;
            float px = Fp(tf, "px"), py = Fp(tf, "py"), pz = Fp(tf, "pz");
            float ex = Fp(tf, "ex"), ey = Fp(tf, "ey"), ez = Fp(tf, "ez");
            float sx = Fp(tf, "sx"), sy = Fp(tf, "sy"), sz = Fp(tf, "sz");

            var section = BuildSection("Transform");
            var grid = BuildXYZGrid();

            (_tbPx, _tbPy, _tbPz) = AddXYZRow(grid, 0, "位置",    px, py, pz, "#E06C75", "#98C379", "#61AFEF", 0.1);
            (_tbEx, _tbEy, _tbEz) = AddXYZRow(grid, 1, "回転",    ex, ey, ez, "#E06C75", "#98C379", "#61AFEF", 1.0);
            (_tbSx, _tbSy, _tbSz) = AddXYZRow(grid, 2, "スケール", sx, sy, sz, "#E06C75", "#98C379", "#61AFEF", 0.01);

            ((StackPanel)section.Child).Children.Add(grid);
            ComponentStack.Children.Add(section);
        }

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

    // ── Actor edit mode: component list ──────────────────────

    /// <summary>コンポーネントスロット 1 件分の情報。TypeId ごとに追加フィールドを持つ。</summary>
    private record SlotInfo(int SlotIdx, string Name, string TypeId, string ModelPath,
        float Width = 0f, float Height = 0f);
    private List<SlotInfo> _slotInfos = new();

    private void BuildActorComponentList(string json)
    {
        _lastComponentsJson = json;

        using var doc  = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var name = root.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
        ActorNameBlock.Text = string.IsNullOrEmpty(name) ? $"Actor #{_currentActorId}" : name;

        ComponentListStack.Children.Clear();
        ClearTransformRefs();
        _slotInfos.Clear();

        if (!root.TryGetProperty("components", out var comps)) return;

        _componentSlotCount = comps.GetArrayLength();
        var prevSlot = _selectedSlotIdx;
        _selectedSlotIdx = -1;

        foreach (var comp in comps.EnumerateArray())
        {
            var slotIdx   = comp.TryGetProperty("slot",       out var si) ? si.GetInt32()    : 0;
            var compName  = comp.TryGetProperty("name",       out var cn) ? cn.GetString() ?? "" : "";
            var compType  = comp.TryGetProperty("type",       out var ct) ? ct.GetString() ?? "" : "";
            var modelPath = comp.TryGetProperty("model_path", out var mp) ? mp.GetString() ?? "" : "";
            // CanvasComponent 用: 幅・高さ
            var width  = comp.TryGetProperty("width",  out var wd) ? wd.GetSingle() : 0f;
            var height = comp.TryGetProperty("height", out var ht) ? ht.GetSingle() : 0f;

            var info = new SlotInfo(slotIdx, compName, compType, modelPath, width, height);
            _slotInfos.Add(info);
            ComponentListStack.Children.Add(BuildComponentChip(info.SlotIdx, info.Name, info.TypeId));

            if (slotIdx == prevSlot) _selectedSlotIdx = slotIdx;
        }

        // 前回未選択かつコンポーネントが存在する場合は最後のスロットを自動選択
        if (_selectedSlotIdx < 0 && _slotInfos.Count > 0)
            _selectedSlotIdx = _slotInfos[^1].SlotIdx;

        RefreshChipSelection();
        RebuildPropsPane(root);
    }

    private void RebuildPropsPane(JsonElement root)
    {
        BasicInfoStack.Children.Clear();
        ComponentPropsStack.Children.Clear();
        ClearTransformRefs();

        // [基本情報] タブ: Transform セクション（3D / 2D で分岐）
        if (root.TryGetProperty("canvas_transform", out var ct))
        {
            // 2D Actor: CanvasTransform（位置XY・回転・スケールXY）
            _isActor2D = true;
            float px  = Fp(ct, "px"),  py  = Fp(ct, "py");
            float rot = Fp(ct, "rotation");
            float sx  = Fp(ct, "sx"),  sy  = Fp(ct, "sy");

            BasicInfoStack.Children.Add(BuildCanvas2DTransformSection(px, py, rot, sx, sy));
        }
        else if (root.TryGetProperty("transform", out var tf))
        {
            // 3D Actor: Transform（位置XYZ・回転XYZ・スケールXYZ）
            _isActor2D = false;
            float px = Fp(tf, "px"), py = Fp(tf, "py"), pz = Fp(tf, "pz");
            float ex = Fp(tf, "ex"), ey = Fp(tf, "ey"), ez = Fp(tf, "ez");
            float sx = Fp(tf, "sx"), sy = Fp(tf, "sy"), sz = Fp(tf, "sz");

            var section = BuildSection("Transform");
            var grid = BuildXYZGrid();
            (_tbPx, _tbPy, _tbPz) = AddXYZRow(grid, 0, "位置",    px, py, pz, "#E06C75", "#98C379", "#61AFEF", 0.1);
            (_tbEx, _tbEy, _tbEz) = AddXYZRow(grid, 1, "回転",    ex, ey, ez, "#E06C75", "#98C379", "#61AFEF", 1.0);
            (_tbSx, _tbSy, _tbSz) = AddXYZRow(grid, 2, "スケール", sx, sy, sz, "#E06C75", "#98C379", "#61AFEF", 0.01);
            ((StackPanel)section.Child).Children.Add(grid);
            BasicInfoStack.Children.Add(section);
        }

        // [コンポーネント情報] タブ: 選択中スロットのプロパティ
        if (_selectedSlotIdx >= 0)
        {
            var info = _slotInfos.FirstOrDefault(s => s.SlotIdx == _selectedSlotIdx);
            if (info != null)
            {
                CompInfoScroll.Visibility           = Visibility.Visible;
                NoComponentSelectedBlock.Visibility = Visibility.Collapsed;
                BuildSlotProps(info);
                return;
            }
        }
        CompInfoScroll.Visibility           = Visibility.Collapsed;
        NoComponentSelectedBlock.Visibility = Visibility.Visible;
    }

    private void BuildSlotProps(SlotInfo info)
    {
        if (info.TypeId == "ModelComponent")
        {
            var section = BuildSection(info.Name + " (ModelComponent)");
            var sp = (StackPanel)section.Child;
            sp.Children.Add(BuildModelPathRow(info));
            ComponentPropsStack.Children.Add(section);
        }
        else if (info.TypeId == "ScriptComponent")
        {
            BuildScriptSlotProps(info);
        }
        else if (info.TypeId == "CanvasComponent")
        {
            BuildCanvasSlotProps(info);
        }
    }

    // ── CanvasComponent inspector ─────────────────────────────

    /// <summary>CanvasComponent の幅・高さフィールドを表示して SET_CANVAS_SIZE を送信する。</summary>
    private void BuildCanvasSlotProps(SlotInfo info)
    {
        var section = BuildSection(info.Name + " (CanvasComponent)");
        var sp      = (StackPanel)section.Child;

        // 幅フィールド
        var rowW = BuildLabeledNumberRow("幅",  info.Width);
        var tbW  = rowW.textBox;
        sp.Children.Add(rowW.element);

        // 高さフィールド
        var rowH = BuildLabeledNumberRow("高さ", info.Height);
        var tbH  = rowH.textBox;
        sp.Children.Add(rowH.element);

        ComponentPropsStack.Children.Add(section);

        // 幅・高さを送信するローカル関数
        void Commit()
        {
            if (_currentActorId < 0) return;
            if (!float.TryParse(tbW.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)) return;
            if (!float.TryParse(tbH.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CANVAS_SIZE:{_currentActorId},{info.SlotIdx},{w},{h}"));
        }

        tbW.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; } };
        tbW.LostFocus += (_, _) => Commit();
        tbH.KeyDown   += (_, e) => { if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; } };
        tbH.LostFocus += (_, _) => Commit();
    }

    /// <summary>ラベル + 数値入力フィールドの行を生成する。</summary>
    private static (UIElement element, TextBox textBox) BuildLabeledNumberRow(string label, float value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        var tb = new TextBox
        {
            Text              = value.ToString("F1", CultureInfo.InvariantCulture),
            Background        = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            Foreground        = new SolidColorBrush(Colors.White),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness   = new Thickness(1),
            FontSize          = 11,
            Padding           = new Thickness(3, 1, 3, 1),
            Margin            = new Thickness(1, 1, 2, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(tb);

        return (grid, tb);
    }

    // ── ScriptComponent inspector ─────────────────────────────

    private void BuildScriptSlotProps(SlotInfo info)
    {
        // スクリプトパス選択セクション
        var pathSection = BuildSection(info.Name + " (ScriptComponent)");
        ((StackPanel)pathSection.Child).Children.Add(BuildScriptPathRow(info));
        ComponentPropsStack.Children.Add(pathSection);

        // スクリプトが設定されていなければここで終了
        if (string.IsNullOrEmpty(info.ModelPath)) return;

        var scriptType = GetOrCompileScript(info.ModelPath);
        if (scriptType is null) return;

        var fields = ScriptCompiler.GetSerializeFields(scriptType);
        if (fields.Count == 0) return;

        var storeKey = $"{_currentActorId}:{info.SlotIdx}";
        if (!_scriptFieldValues.ContainsKey(storeKey))
            _scriptFieldValues[storeKey] = new Dictionary<string, string>();
        var values = _scriptFieldValues[storeKey];

        var fieldSection = BuildSection("フィールド");
        var fieldSp = (StackPanel)fieldSection.Child;
        fieldSp.Children.Add(ScriptInspectorBuilder.Build(fields, values, (name, val) =>
        {
            values[name] = val;
        }));
        ComponentPropsStack.Children.Add(fieldSection);
    }

    private UIElement BuildScriptPathRow(SlotInfo info) =>
        FileRefBuilder.Build(
            "スクリプト", info.ModelPath,
            [".cs"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "スクリプトファイルを選択",
                    Filter = "C# スクリプト|*.cs|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path => SetScriptPath(info.SlotIdx, path));

    private void SetScriptPath(int slotIdx, string path)
    {
        if (_currentActorId < 0) return;
        // 既存のキャッシュを無効化
        var old = _slotInfos.FirstOrDefault(s => s.SlotIdx == slotIdx)?.ModelPath;
        if (old is not null) _scriptTypeCache.Remove(old);
        _runtime?.SendToRuntime($"SET_MODEL_PATH:{_currentActorId},{slotIdx},{path}");
    }

    private Type? GetOrCompileScript(string path)
    {
        if (_scriptTypeCache.TryGetValue(path, out var cached)) return cached;

        var (type, errors) = ScriptCompiler.CompileFile(path);
        if (type is null)
        {
            EditorLog.Write($"Script compile error [{Path.GetFileName(path)}]: {string.Join("; ", errors)}");
            return null;
        }
        _scriptTypeCache[path] = type;
        return type;
    }

    private UIElement BuildModelPathRow(SlotInfo info) =>
        FileRefBuilder.Build(
            "モデル", info.ModelPath,
            [".glb", ".gltf", ".obj"],
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "モデルファイルを選択",
                    Filter = "3D モデル|*.glb;*.gltf;*.obj|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path => SetModelPath(info.SlotIdx, path));

    private void SetModelPath(int slotIdx, string path)
    {
        if (_currentActorId < 0) return;
        _runtime?.SendToRuntime($"SET_MODEL_PATH:{_currentActorId},{slotIdx},{path}");
    }

    private Border BuildComponentChip(int slotIdx, string name, string typeName)
    {
        var isSelected = slotIdx == _selectedSlotIdx;

        var chip = new Border
        {
            Background      = isSelected
                                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x3A))
                                : new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            BorderBrush     = isSelected
                                ? new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF))
                                : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(3),
            Margin          = new Thickness(4, 2, 4, 2),
            Padding         = new Thickness(6, 4, 6, 4),
            Cursor          = Cursors.Hand,
            Tag             = slotIdx,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var typeIcon = new TextBlock
        {
            Text              = typeName == "ModelComponent" ? "◈" : "⬡",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x55, 0xAA, 0xFF)),
            FontSize          = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 4, 0),
        };

        var nameBlock = new TextBlock
        {
            Text              = name,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var innerStack = new StackPanel { Orientation = Orientation.Horizontal };
        innerStack.Children.Add(typeIcon);
        innerStack.Children.Add(nameBlock);
        Grid.SetColumn(innerStack, 0);
        grid.Children.Add(innerStack);

        var removeBtn = new TextBlock
        {
            Text              = "✕",
            Foreground        = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            FontSize          = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Cursor            = Cursors.Hand,
            Padding           = new Thickness(4, 2, 0, 2),
            Tag               = slotIdx,
        };
        removeBtn.MouseEnter += (_, _) =>
            removeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66));
        removeBtn.MouseLeave += (_, _) =>
            removeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        removeBtn.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_currentActorId >= 0)
                _runtime?.SendToRuntime($"REMOVE_COMPONENT:{_currentActorId},{slotIdx}");
        };
        Grid.SetColumn(removeBtn, 1);
        grid.Children.Add(removeBtn);

        chip.Child = grid;

        chip.MouseLeftButtonDown += (_, _) =>
        {
            _selectedSlotIdx = slotIdx;
            if (!string.IsNullOrEmpty(_lastComponentsJson))
                BuildActorComponentList(_lastComponentsJson);
            else
                RefreshChipSelection();
        };

        chip.MouseRightButtonDown += (_, e) =>
        {
            _selectedSlotIdx = slotIdx;
            RefreshChipSelection();
            ShowComponentChipContextMenu(chip, slotIdx, name);
            e.Handled = true;
        };

        return chip;
    }

    private void RefreshChipSelection()
    {
        foreach (var child in ComponentListStack.Children)
        {
            if (child is Border b && b.Tag is int idx)
            {
                bool sel = idx == _selectedSlotIdx;
                b.Background  = sel
                    ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x3A))
                    : new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25));
                b.BorderBrush = sel
                    ? new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF))
                    : new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
            }
        }
    }

    private void ShowComponentChipContextMenu(UIElement target, int slotIdx, string currentName)
    {
        var menu = new ContextMenu
        {
            Background  = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
        };

        MenuItem MakeItem(string header, SolidColorBrush fg, RoutedEventHandler handler)
        {
            var item = new MenuItem
            {
                Header     = header,
                Background = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
                Foreground = fg,
                FontSize   = 11,
            };
            item.Click += handler;
            return item;
        }

        menu.Items.Add(MakeItem("リネーム", new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            (_, _) => StartComponentRename(slotIdx, currentName)));
        menu.Items.Add(MakeItem("複製", new SolidColorBrush(Color.FromRgb(0xAA, 0xDD, 0xAA)),
            (_, _) =>
            {
                if (_currentActorId >= 0)
                    _runtime?.SendToRuntime($"DUPLICATE_COMPONENT:{_currentActorId},{slotIdx}");
            }));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeItem("削除", new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66)),
            (_, _) =>
            {
                if (_currentActorId >= 0)
                    _runtime?.SendToRuntime($"REMOVE_COMPONENT:{_currentActorId},{slotIdx}");
            }));

        menu.PlacementTarget = target;
        menu.Placement       = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen          = true;
    }

    private void StartComponentRename(int slotIdx, string currentName)
    {
        // 該当チップを TextBox に差し替え
        foreach (var child in ComponentListStack.Children)
        {
            if (child is not Border b || b.Tag is not int idx || idx != slotIdx) continue;

            var committed = false;
            var tb = new TextBox
            {
                Text              = currentName,
                Background        = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x2D)),
                Foreground        = Brushes.White,
                CaretBrush        = Brushes.White,
                BorderBrush       = new SolidColorBrush(Color.FromRgb(0x33, 0x99, 0xFF)),
                BorderThickness   = new Thickness(1),
                Padding           = new Thickness(4, 3, 4, 3),
                FontSize          = 12,
                Margin            = new Thickness(4, 2, 4, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };

            void Commit()
            {
                if (committed) return;
                committed = true;
                var newName = tb.Text.Trim();
                if (!string.IsNullOrEmpty(newName) && newName != currentName && _currentActorId >= 0)
                    _runtime?.SendToRuntime($"RENAME_COMPONENT_SLOT:{_currentActorId},{slotIdx},{newName}");
                // ランタイムが ACTOR_COMPONENTS を再送してくれるので UI は自動更新される
            }

            tb.PreviewKeyDown += (_, e) =>
            {
                if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape)          { committed = true; /* キャンセル */ e.Handled = true;
                    if (_currentActorId >= 0) _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{_currentActorId}"); }
            };
            tb.LostFocus += (_, _) => Commit();

            var originalChild = b.Child;
            b.Child = tb;
            Dispatcher.BeginInvoke(() => { tb.Focus(); tb.SelectAll(); },
                System.Windows.Threading.DispatcherPriority.Input);
            return;
        }
    }

    // ── Add Component (actor edit mode) ─────────────────────

    private void OnAddComponentClicked(object sender, RoutedEventArgs e)
    {
        if (_runtime is null || _currentActorId < 0) return;
        var win = new ComponentSelectorWindow(_runtime, _currentActorId)
        {
            Owner = Window.GetWindow(this),
        };
        win.ShowDialog();
    }

    // ── Component copy/paste (Ctrl+C / Ctrl+V) ───────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_isActorEditMode || _currentActorId < 0) return;

        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (_selectedSlotIdx >= 0)
            {
                _copiedSlot = _slotInfos.FirstOrDefault(s => s.SlotIdx == _selectedSlotIdx);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (_copiedSlot != null)
            {
                _runtime?.SendToRuntime($"DUPLICATE_COMPONENT:{_currentActorId},{_copiedSlot.SlotIdx}");
                e.Handled = true;
            }
        }
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
            Background   = new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x25)),
            CornerRadius = new CornerRadius(3),
            Padding      = new Thickness(8, 6, 8, 6),
            Margin       = new Thickness(4, 0, 4, 4),
            Child        = sp,
        };
    }

    private static Grid BuildXYZGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    /// <summary>
    /// CanvasTransform（2D）用の Transform セクションを生成する。
    /// 位置(X,Y)・回転(単一値)・スケール(X,Y) の5フィールド構成。
    /// _tbPx/_tbPy = 位置, _tbEz = 回転, _tbSx/_tbSy = スケール にそれぞれ格納される。
    /// </summary>
    private Border BuildCanvas2DTransformSection(float px, float py, float rot, float sx, float sy)
    {
        var section = BuildSection("CanvasTransform");
        var sp      = (StackPanel)section.Child;
        var grid    = BuildXYGrid();

        // 位置行: X, Y
        (_tbPx, _tbPy) = AddXYRow(grid, 0, "位置", px, py, "#E06C75", "#98C379", 0.1);
        // 回転行: Z（2D では Z 軸周り単一値）
        _tbEz = AddSingleValueRow(grid, 1, "回転", "Z", rot, "#61AFEF", 1.0);
        // スケール行: X, Y
        (_tbSx, _tbSy) = AddXYRow(grid, 2, "スケール", sx, sy, "#E06C75", "#98C379", 0.01);

        sp.Children.Add(grid);
        return section;
    }

    /// <summary>XY 2列グリッド（ラベル + X軸 + Y軸）を生成する。</summary>
    private static Grid BuildXYGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        return grid;
    }

    /// <summary>XY グリッドに X, Y 2フィールド行を追加する。</summary>
    private (TextBox x, TextBox y) AddXYRow(
        Grid grid, int row, string label,
        float vx, float vy,
        string colorX, string colorY,
        double dragSpeed)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);

        var tbX = MakeAxisField(vx, colorX); Grid.SetRow(tbX, row); Grid.SetColumn(tbX, 2); grid.Children.Add(tbX);
        var tbY = MakeAxisField(vy, colorY); Grid.SetRow(tbY, row); Grid.SetColumn(tbY, 4); grid.Children.Add(tbY);

        var lblX = MakeAxisLabel("X", colorX, tbX, dragSpeed); Grid.SetRow(lblX, row); Grid.SetColumn(lblX, 1); grid.Children.Add(lblX);
        var lblY = MakeAxisLabel("Y", colorY, tbY, dragSpeed); Grid.SetRow(lblY, row); Grid.SetColumn(lblY, 3); grid.Children.Add(lblY);

        tbX.KeyDown   += OnFieldKeyDown; tbY.KeyDown   += OnFieldKeyDown;
        tbX.LostFocus += OnFieldLostFocus; tbY.LostFocus += OnFieldLostFocus;

        return (tbX, tbY);
    }

    /// <summary>XY グリッドに単一値行（回転などに使用）を追加する。Y列はフィールドが全幅を占める。</summary>
    private TextBox AddSingleValueRow(
        Grid grid, int row, string label, string axisLabel,
        float value, string color, double dragSpeed)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);

        var tb = MakeAxisField(value, color);
        Grid.SetRow(tb, row); Grid.SetColumn(tb, 2);
        Grid.SetColumnSpan(tb, 3); // Y 列も含めて全幅表示
        grid.Children.Add(tb);

        var lblAxis = MakeAxisLabel(axisLabel, color, tb, dragSpeed);
        Grid.SetRow(lblAxis, row); Grid.SetColumn(lblAxis, 1);
        grid.Children.Add(lblAxis);

        tb.KeyDown   += OnFieldKeyDown;
        tb.LostFocus += OnFieldLostFocus;

        return tb;
    }

    private (TextBox x, TextBox y, TextBox z) AddXYZRow(
        Grid grid, int row, string label,
        float vx, float vy, float vz,
        string colorX, string colorY, string colorZ,
        double dragSpeed)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(24) });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);

        var tbX = MakeAxisField(vx, colorX); Grid.SetRow(tbX, row); Grid.SetColumn(tbX, 2); grid.Children.Add(tbX);
        var tbY = MakeAxisField(vy, colorY); Grid.SetRow(tbY, row); Grid.SetColumn(tbY, 4); grid.Children.Add(tbY);
        var tbZ = MakeAxisField(vz, colorZ); Grid.SetRow(tbZ, row); Grid.SetColumn(tbZ, 6); grid.Children.Add(tbZ);

        var lblX = MakeAxisLabel("X", colorX, tbX, dragSpeed); Grid.SetRow(lblX, row); Grid.SetColumn(lblX, 1); grid.Children.Add(lblX);
        var lblY = MakeAxisLabel("Y", colorY, tbY, dragSpeed); Grid.SetRow(lblY, row); Grid.SetColumn(lblY, 3); grid.Children.Add(lblY);
        var lblZ = MakeAxisLabel("Z", colorZ, tbZ, dragSpeed); Grid.SetRow(lblZ, row); Grid.SetColumn(lblZ, 5); grid.Children.Add(lblZ);

        tbX.KeyDown  += OnFieldKeyDown; tbY.KeyDown  += OnFieldKeyDown; tbZ.KeyDown  += OnFieldKeyDown;
        tbX.LostFocus += OnFieldLostFocus; tbY.LostFocus += OnFieldLostFocus; tbZ.LostFocus += OnFieldLostFocus;

        return (tbX, tbY, tbZ);
    }

    private TextBlock MakeAxisLabel(string text, string colorHex, TextBox target, double dragSpeed)
    {
        var label = new TextBlock
        {
            Text                = text,
            Foreground          = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
            FontSize            = 10,
            FontWeight          = FontWeights.Bold,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor              = Cursors.SizeWE,
        };

        double dragOriginX     = 0;
        float  dragOriginValue = 0f;

        label.MouseLeftButtonDown += (_, e) =>
        {
            if (float.TryParse(target.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                dragOriginX     = e.GetPosition(null).X;
                dragOriginValue = v;
                label.CaptureMouse();
                BeginTransformDrag();
            }
            e.Handled = true;
        };
        label.MouseMove += (_, e) =>
        {
            if (!label.IsMouseCaptured) return;
            var dx    = e.GetPosition(null).X - dragOriginX;
            var speed = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? dragSpeed * 0.1 : dragSpeed;
            target.Text = Fmt(dragOriginValue + (float)(dx * speed));
            CommitTransform();
        };
        label.MouseLeftButtonUp += (_, e) =>
        {
            if (label.IsMouseCaptured)
            {
                label.ReleaseMouseCapture();
                EndTransformDrag();
            }
            e.Handled = true;
        };
        return label;
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
        var lbl = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Text = value, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
        Grid.SetColumn(val, 1); grid.Children.Add(val);
        return grid;
    }

    // ── Transform drag (undo batching) ───────────────────────

    private void BeginTransformDrag()
    {
        if (_currentActorId < 0) return;
        _isDraggingTransform = true;
        // アクター編集モード・仮想DFS選択のどちらも type=1（アクタートランスフォームモード）
        int type = (_isActorEditMode || _isVirtualActorSelected) ? 1 : 0;
        _runtime?.SendToRuntime($"BEGIN_TRANSFORM_DRAG:{type},{_currentActorId}");
    }

    private void EndTransformDrag()
    {
        if (!_isDraggingTransform) return;
        _isDraggingTransform = false;
        _runtime?.SendToRuntime("END_TRANSFORM_DRAG");
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
            if (_isActorEditMode && _currentActorId >= 0)
                _runtime?.SendToRuntime($"GET_ACTOR_COMPONENTS:{_currentActorId}");
            else if (_currentActorId >= 0)
                _runtime?.SendToRuntime($"GET_ACTOR:{_currentActorId}");
            e.Handled = true;
        }
    }

    private void OnFieldLostFocus(object sender, RoutedEventArgs e) => CommitTransform();

    private void CommitTransform()
    {
        if (_currentActorId < 0 || _tbPx is null) return;

        // 2D Actor の場合は CanvasTransform 専用コマンドを送信する
        if (_isActor2D && (_isActorEditMode || _isVirtualActorSelected))
        {
            if (!TryParse(_tbPx, out float px) || !TryParse(_tbPy, out float py)) return;
            if (!TryParse(_tbEz, out float rot)) return;
            if (!TryParse(_tbSx, out float sx) || !TryParse(_tbSy, out float sy)) return;
            _runtime?.SendToRuntime(FormattableString.Invariant(
                $"SET_CANVAS_TRANSFORM:{_currentActorId},{px},{py},{rot},{sx},{sy}"));
            TransformCommitted?.Invoke();
            return;
        }

        // 3D Actor の場合は従来の 9 軸トランスフォームコマンドを送信する
        if (!TryParseAll(out float px3, out float py3, out float pz3,
                         out float ex3, out float ey3, out float ez3,
                         out float sx3, out float sy3, out float sz3)) return;

        string msg;
        // アクター編集モード、またはシーンモードで仮想DFS選択された場合は SET_ACTOR_TRANSFORM を使う。
        // それ以外（レガシーインスタンス直接選択）は SET_TRANSFORM を使う。
        if (_isActorEditMode || _isVirtualActorSelected)
        {
            msg = FormattableString.Invariant(
                $"SET_ACTOR_TRANSFORM:{_currentActorId},{px3},{py3},{pz3},{ex3},{ey3},{ez3},{sx3},{sy3},{sz3}");
        }
        else
        {
            msg = FormattableString.Invariant(
                $"SET_TRANSFORM:{_currentActorId},{px3},{py3},{pz3},{ex3},{ey3},{ez3},{sx3},{sy3},{sz3}");
        }
        _runtime?.SendToRuntime(msg);
        TransformCommitted?.Invoke();
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
        return tb is not null && float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // ── Helpers ──────────────────────────────────────────────

    private static float Fp(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) ? v.GetSingle() : 0f;

    private static string Fmt(float v) =>
        v.ToString("F3", CultureInfo.InvariantCulture);
}
