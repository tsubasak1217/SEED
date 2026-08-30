using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SEEDEditor.Runtime;

namespace SEEDEditor;

/// <summary>
/// アクターにアタッチする ECS コンポーネントをカテゴリ一覧から選択・検索して追加するダイアログ。
/// 静的カテゴリリストとロード済みプラグイン一覧から動的に「プラグイン」カテゴリを生成し、
/// 選択したコンポーネント種別・名前でランタイムへ ADD_COMPONENT コマンドを送信する。
/// </summary>
public partial class ComponentSelectorWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly RuntimeManager  _runtime;
    private readonly int             _actorDfsId;
    private readonly bool            _isActor2D;       // 2D アクターかどうか
    /// <summary>追加上限に達しているため選択不可のコンポーネント種別 ID セット。</summary>
    private readonly HashSet<string> _disabledTypes;
    /// <summary>ロード済みプラグイン名リスト（"プラグイン" カテゴリのエントリに使用）。</summary>
    private readonly IReadOnlyList<string> _pluginNames;

    private string? _selectedType;
    private Border? _selectedBorder;

    /// <summary>
    /// 現在表示中の「選択できる」アイテム行を上から順に保持したリスト（↑↓ キー移動用）。
    /// BuildCategoryList のたびに作り直す。追加上限に達した行（disabled）は
    /// 選択させても Enter で追加できないため含めない。
    /// </summary>
    private readonly List<(Border Row, ComponentEntry Entry)> _navigableRows = new();

    private readonly HashSet<string> _collapsedCategories = new();

    /// <summary>コンポーネントが対応するアクター種別。</summary>
    private enum ActorTarget
    {
        /// <summary>2D/3D 両方に追加可能。</summary>
        Common,
        /// <summary>3D アクター専用。</summary>
        Actor3D,
        /// <summary>2D アクター専用。</summary>
        Actor2D,
    }

    /// <summary>コンポーネント選択リストの1エントリ（型ID・表示名・説明・対応アクター種別）。</summary>
    private record ComponentEntry(string TypeId, string Label, string Description, ActorTarget Target = ActorTarget.Common);

    private static readonly List<(string Category, List<ComponentEntry> Items)> Categories = new()
    {
        ("レンダリング", new()
        {
            new("ModelComponent", "Model", "3D モデルをアクタにアタッチ", ActorTarget.Actor3D),
            new("SkyboxComponent", "Skybox", "equirectangular（正距円筒）画像1枚を天球として描画。CameraLocked/WorldAnchored", ActorTarget.Actor3D),
        }),
        ("環境", new()
        {
            new("WaterVolumeComponent", "Water Volume", "海・池などの水領域。水面描画と水中判定を提供", ActorTarget.Actor3D),
            new("WaterLinkComponent", "Water Link", "2つの水域をつなぐ開口（扉・窓・穴・バルブ）。水位グラフで水が行き来する", ActorTarget.Actor3D),
            new("InteractionSourceComponent", "Interaction Source", "動く物に付ける。移動速度を共有フィールドへ焼き、草を押し倒す（将来は水の波紋・雪泥の轍も）", ActorTarget.Actor3D),
            new("CoverEmitterComponent", "Cover Emitter", "地表へ雪・落ち葉・濡れを積もらせる。範囲は全域/直方体/マスク画像から選ぶ", ActorTarget.Actor3D),
        }),
        ("UI", new()
        {
            new("CanvasComponent", "Canvas", "UI 矩形領域をアクタにアタッチ（幅・高さ指定）。3D アクタにアタッチするとワールド空間に配置", ActorTarget.Common),
            new("SpriteComponent", "Sprite", "2D スプライト画像をキャンバスに表示",          ActorTarget.Common),
            new("SkinnedSpriteComponent", "Skinned Sprite", ".sprite_mesh のメッシュを子アクター（ボーン）で変形して表示する 2D スプライト", ActorTarget.Common),
        }),
        ("ライト", new()
        {
            new("LightComponent", "Light", "光源（directional / point / spot / rect）をアクターにアタッチ。向き・位置は Transform から", ActorTarget.Actor3D),
            new("JointAttachComponent", "ジョイントアタッチ", "モデルのジョイント（ボーン）へ追従するソケット", ActorTarget.Actor3D),
        }),
        ("エフェクト", new()
        {
            new("ParticleEmitterComponent", "Particle Emitter", "GPUパーティクルエミッタ。放出レート・寿命・色・サイズ補間などをデータドリブンに設定", ActorTarget.Actor3D),
        }),
        // 「ツール」カテゴリ: シーン編集の道具として使う汎用コンポーネント。
        // ControlPoint は川・巡回ルート・カメラフライスルーなど用途に依存しない
        // 「順序付き点列」そのものを提供する土台なので、特定用途の「環境」ではなく
        // 用途中立の「ツール」に置く（将来の同種コンポーネントもここへ集める）。
        ("ツール", new()
        {
            new("ControlPointComponent", "Control Point", "シーン上に順序付きの点列を置く汎用パス。川・巡回ルート・カメラパスなどの共通土台", ActorTarget.Actor3D),
        }),
        ("カメラ", new()
        {
            new("CameraComponent", "Camera", "Play モードで使用するゲームカメラ", ActorTarget.Actor3D),
        }),
        ("物理", new()
        {
            new("ColliderComponent",   "Collider",    "衝突判定形状・リジッドボディをアクターにアタッチ（Box・Sphere・Capsule、重力有無は内部で設定）", ActorTarget.Actor3D),
            new("Collider2dComponent", "Collider 2D", "2D コライダー・リジッドボディをアクターにアタッチ（Box・Circle・Capsule、ピクセル単位）",         ActorTarget.Common),
        }),
        ("サウンド", new()
        {
            new("AudioComponent", "Audio Source", "BGM/SE の再生。3D 距離減衰・パン対応", ActorTarget.Common),
        }),
        ("アニメーション", new()
        {
            new("AnimatorComponent", "Animator", "キーフレームアニメーションクリップ（.anim）の再生", ActorTarget.Common),
        }),
        ("入力", new()
        {
            new("InputMapComponent", "InputMap", ".inputmap アセットをアクタにアタッチ", ActorTarget.Common),
        }),
        ("スクリプト", new()
        {
            new("ScriptComponent", "Script", "スクリプトをアクタにアタッチ", ActorTarget.Common),
        }),
    };

    /// <summary>
    /// ロード済みプラグインリストから動的にカテゴリエントリを生成して返す。
    /// プラグインがない場合は空のカテゴリを返す（「今後追加」として表示される）。
    /// </summary>
    private List<ComponentEntry> BuildPluginEntries() =>
        _pluginNames
            .Select(name => new ComponentEntry(
                $"Plugin:{name}",
                name,
                $"{name} プラグインをアクターにアタッチ",
                ActorTarget.Common))
            .ToList();

    /// <summary>コンポーネント種別アイコンの一辺サイズ（px）。</summary>
    private const double ItemIconSize = 14.0;

    /// <summary>アイコンとラベルの間隔（px）。</summary>
    private const double ItemIconGap = 6.0;

    /// <summary>通常行のラベル／アイコン色。</summary>
    private static readonly SolidColorBrush BrushLabelNormal   = new(Color.FromRgb(0xCC, 0xCC, 0xCC));

    /// <summary>追加済みで選べない行のラベル／アイコン色。</summary>
    private static readonly SolidColorBrush BrushLabelDisabled = new(Color.FromRgb(0x44, 0x44, 0x44));

    private static readonly SolidColorBrush BrushSelected  = new(Color.FromRgb(0x1A, 0x2A, 0x3A));
    private static readonly SolidColorBrush BrushHover     = new(Color.FromRgb(0x28, 0x28, 0x28));
    private static readonly SolidColorBrush BrushTransp    = Brushes.Transparent;
    private static readonly SolidColorBrush BrushAccent    = new(Color.FromRgb(0x33, 0x99, 0xFF));

    public ComponentSelectorWindow(
        RuntimeManager runtime, int actorDfsId,
        bool isActor2D = false, HashSet<string>? disabledTypes = null,
        IReadOnlyList<string>? pluginNames = null)
    {
        InitializeComponent();
        _runtime      = runtime;
        _actorDfsId   = actorDfsId;
        _isActor2D    = isActor2D;
        _disabledTypes = disabledTypes ?? new HashSet<string>();
        _pluginNames  = pluginNames ?? Array.Empty<string>();
        BuildCategoryList(filter: "");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        int dark = 1;
        DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        TxtSearch.Focus();
    }

    // ── リスト構築 ───────────────────────────────────────────

    /// <summary>
    /// エントリが現在のアクター種別に対応しているか判定する。
    /// Common はどちらにも表示、Actor2D/Actor3D は対応する種別のみ表示。
    /// </summary>
    private bool EntryMatchesActor(ComponentEntry entry) =>
        entry.Target == ActorTarget.Common ||
        (_isActor2D ? entry.Target == ActorTarget.Actor2D : entry.Target == ActorTarget.Actor3D);

    private void BuildCategoryList(string filter)
    {
        CategoryList.Children.Clear();
        _selectedBorder = null;
        _navigableRows.Clear();
        var prevType = _selectedType;

        // 検索中はフラットリスト表示（カテゴリヘッダーなし）
        // プラグインカテゴリを動的追加して全カテゴリを対象に検索する
        if (!string.IsNullOrEmpty(filter))
        {
            var matches = Categories
                .SelectMany(c => c.Items)
                .Concat(BuildPluginEntries())
                .Where(i => EntryMatchesActor(i)
                         && (i.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)
                          || i.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (matches.Count == 0)
            {
                CategoryList.Children.Add(new TextBlock
                {
                    Text       = "  一致する項目がありません",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                    FontSize   = 11,
                    Margin     = new Thickness(12, 8, 8, 4),
                });
            }
            else
            {
                foreach (var entry in matches)
                {
                    var row = BuildItemRow(entry);
                    CategoryList.Children.Add(row);
                    if (entry.TypeId == prevType) SelectRow(row, entry);
                }
            }
            SelectFirstRowIfNoneSelected();
            return;
        }

        // 通常表示: カテゴリヘッダー + 開閉
        // 静的カテゴリリストにプラグインカテゴリを動的追加して結合する
        var allCategories = Categories
            .Append(("プラグイン", BuildPluginEntries()))
            .ToList();
        foreach (var (catName, items) in allCategories)
        {
            bool collapsed = _collapsedCategories.Contains(catName);

            var header = new TextBlock { Style = (Style)Resources["CategoryHeader"] };
            header.Inlines.Add(new Run(collapsed ? "▶ " : "▼ ")
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
                FontSize   = 9,
            });
            header.Inlines.Add(new Run(catName));
            header.MouseLeftButtonDown += (_, _) =>
            {
                if (_collapsedCategories.Contains(catName))
                    _collapsedCategories.Remove(catName);
                else
                    _collapsedCategories.Add(catName);
                BuildCategoryList(TxtSearch.Text.Trim());
            };
            CategoryList.Children.Add(header);

            if (collapsed) continue;

            // 現在のアクター種別でフィルタリング
            var visibleItems = items.Where(EntryMatchesActor).ToList();

            if (visibleItems.Count == 0)
            {
                CategoryList.Children.Add(new TextBlock
                {
                    Text       = "  （今後追加）",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                    FontSize   = 11,
                    Margin     = new Thickness(28, 2, 8, 4),
                });
                continue;
            }

            foreach (var entry in visibleItems)
            {
                var row = BuildItemRow(entry);
                CategoryList.Children.Add(row);
                if (entry.TypeId == prevType) SelectRow(row, entry);
            }
        }

        SelectFirstRowIfNoneSelected();
    }

    /// <summary>
    /// どの行も選択されていなければ先頭の選択可能行を選ぶ。
    /// 「開いた直後から Enter で決定できる」「検索を絞ったら先頭候補が選ばれる」を担保する
    /// （前回選択していた種別が絞り込みで消えた場合もここで拾い直す）。
    /// </summary>
    private void SelectFirstRowIfNoneSelected()
    {
        if (_selectedBorder is not null || _navigableRows.Count == 0) return;
        var (row, entry) = _navigableRows[0];
        SelectRow(row, entry);
    }

    private Border BuildItemRow(ComponentEntry entry)
    {
        var disabled = _disabledTypes.Contains(entry.TypeId);

        var label = new TextBlock
        {
            Text       = entry.Label,
            Foreground = disabled ? BrushLabelDisabled : BrushLabelNormal,
            FontSize   = 12,
        };

        // disabled の場合は「（既に追加済み）」を付記する
        var descText = disabled ? entry.Description + "  ※既に追加済み（1 つまで）" : entry.Description;
        var desc = new TextBlock
        {
            Text       = descText,
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            FontSize   = 10,
            Margin     = new Thickness(0, 1, 0, 0),
        };
        var texts = new StackPanel();
        texts.Children.Add(label);
        if (!string.IsNullOrEmpty(entry.Description)) texts.Children.Add(desc);

        // コンポーネント種別アイコン（対応表は Controls/ComponentIcons.cs が唯一の正典）。
        // 無効行（追加済み）はラベルと同じトーンまで落として一体で減光する。
        var icon = SEEDEditor.Controls.AppIcon.Create(
            SEEDEditor.Controls.ComponentIcons.GetIconKey(entry.TypeId), ItemIconSize);
        icon.SetBrush(disabled ? BrushLabelDisabled : BrushLabelNormal);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin            = new Thickness(0, 0, ItemIconGap, 0);

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(icon);
        sp.Children.Add(texts);

        var border = new Border
        {
            // アイコンぶんだけ左パディングを詰め、行全体の見た目の開始位置は従来どおりに保つ。
            Padding    = new Thickness(28 - ItemIconSize - ItemIconGap, 5, 8, 5),
            Cursor     = disabled ? Cursors.No : Cursors.Hand,
            Background = Brushes.Transparent,
            Child      = sp,
            Tag        = entry,
        };

        if (!disabled)
        {
            border.MouseEnter += (_, _) =>
            {
                if (border != _selectedBorder) border.Background = BrushHover;
            };
            border.MouseLeave += (_, _) =>
            {
                if (border != _selectedBorder) border.Background = Brushes.Transparent;
            };
            border.MouseLeftButtonDown += (_, _) => SelectRow(border, entry);
            // ↑↓ キーでの移動対象に登録する（表示順＝リストの並び）。
            _navigableRows.Add((border, entry));
        }

        return border;
    }

    private void SelectRow(Border row, ComponentEntry entry)
    {
        if (_selectedBorder != null) _selectedBorder.Background = Brushes.Transparent;
        _selectedBorder  = row;
        row.Background   = BrushSelected;

        // 前の型のデフォルト名を保存してから型を更新する（自動リネーム判定に使用）
        var prevType  = _selectedType;
        _selectedType = entry.TypeId;

        // テキストが空、または前回選択した型のデフォルト名のままであれば新しい型のデフォルト名に更新する
        TbName.Text      = string.IsNullOrEmpty(TbName.Text) || TbName.Text == GetDefaultName(prevType ?? "")
            ? entry.Label
            : TbName.Text;

        BtnConfirm.IsEnabled = true;
    }

    private static string GetDefaultName(string typeId) => typeId switch
    {
        "ModelComponent"     => "Model",
        "ScriptComponent"    => "Script",
        "CanvasComponent"    => "Canvas",
        "SpriteComponent"    => "Sprite",
        "SkinnedSpriteComponent" => "SkinnedSprite",
        "InputMapComponent"  => "InputMap",
        "CameraComponent"    => "Camera",
        "ColliderComponent"   => "Collider",
        "Collider2dComponent" => "Collider2D",
        "AudioComponent"      => "Audio",
        "AnimatorComponent"   => "Animator",
        "LightComponent"      => "Light",
        "SkyboxComponent"     => "Skybox",
        "ParticleEmitterComponent" => "ParticleEmitter",
        "WaterVolumeComponent"     => "Water",
        "WaterLinkComponent"       => "WaterLink",
        "InteractionSourceComponent" => "Interaction",
        "CoverEmitterComponent"       => "CoverEmitter",
        "ControlPointComponent"      => "ControlPoint",
        // Plugin:{name} → プラグイン名をデフォルト名とする
        _ when typeId.StartsWith("Plugin:", StringComparison.Ordinal) => typeId["Plugin:".Length..],
        _                    => typeId,
    };

    // ── イベント ─────────────────────────────────────────────

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        BuildCategoryList(TxtSearch.Text.Trim());
    }

    /// <summary>
    /// ウィンドウ全体のキー入力で一覧を操作する。
    /// ↑↓ で選択移動、Enter で追加を実行、Esc で閉じる。
    ///
    /// 検索ボックス・名前ボックスにフォーカスがあっても効くように PreviewKeyDown で受ける
    /// （TextBox に矢印キーや Enter を取られる前に処理する）。文字入力は素通しするため、
    /// 検索しながら ↑↓ で候補を選び Enter で追加する、という一連の操作がキーボードだけで完結する。
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;

            // Key.Enter は Key.Return と同一値（WPF の別名）なので 1 つだけ書く。
            case Key.Return:
                if (BtnConfirm.IsEnabled)
                {
                    OnConfirm(sender, e);
                    e.Handled = true;
                }
                break;

            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// 選択を delta 行ぶん動かす（端で止まる。循環はしない）。
    /// 未選択なら先頭（delta が負でも先頭）を選ぶ。移動先はスクロールして見える位置へ送る。
    /// </summary>
    private void MoveSelection(int delta)
    {
        if (_navigableRows.Count == 0) return;

        int current = _selectedBorder is null
            ? -1
            : _navigableRows.FindIndex(r => ReferenceEquals(r.Row, _selectedBorder));
        int next = current < 0 ? 0 : Math.Clamp(current + delta, 0, _navigableRows.Count - 1);

        var (row, entry) = _navigableRows[next];
        SelectRow(row, entry);
        row.BringIntoView();
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_selectedType is null) return;
        // 追加制限チェック（UI で弾いているが念のため再確認する）
        if (_disabledTypes.Contains(_selectedType))
        {
            MessageBox.Show(
                $"{_selectedType} は 1 アクターにつき 1 つのみ追加できます。",
                "追加不可", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var name = TbName.Text.Trim();
        if (string.IsNullOrEmpty(name)) name = GetDefaultName(_selectedType);

        // 空の状態で追加（パスなし）。インスペクター上で後から設定する。
        _runtime.SendToRuntime($"ADD_COMPONENT:{_actorDfsId},{_selectedType},{name},");
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
