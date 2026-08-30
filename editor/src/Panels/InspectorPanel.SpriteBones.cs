using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「スキンスプライトのボーン対応表」実装（Phase A2）。
///
/// SkinnedSpriteComponent のボーンは独立したスケルトンアセットではなく、
/// **スプライトルート配下の普通の 2D 子アクター**である（docs/sprite_skinning.md §1）。
/// このファイルはその対応付けをオーサリングするための UI を持つ:
///
///   ① ボーン一覧（メッシュのボーン名 → 解決先アクターの相対パス）
///      ・自動解決の結果は薄字、明示指定（bone_overrides）は太字で見分ける
///      ・未解決のボーンは行を警告色にし、警告アイコンを付ける
///   ② 各行のピッカー／ヒエラルキーからのドラッグで明示指定を設定・解除する
///   ③ 「ボーンアクターを生成」ボタンでメッシュのボーン階層を一括生成する
///
/// ランタイムとの取り決め（component_ops.rs / sprite_bone_ops.rs が正典）:
///   受信: "bones"[{name,parent,path,resolved,override}] /
///         "bone_candidates"[{path,dfs}] / "bone_unresolved"
///   送信: SET_SKINNED_SPRITE_BONE_OVERRIDES:{actor},{slot},{json}
///         CREATE_SPRITE_BONE_ACTORS:{actor},{slot}
///
/// **相対パスで持つ理由**: ボーンはアクター名ではなくスプライトルートからの
/// 相対パスで解決される（同名の子孫が複数あっても一意に定まる）。そのため
/// 汎用の参照ピッカー（アクタ名 + スロット名で持つ）ではなく、ここでは
/// ランタイムが送ってくる候補一覧（相対パス + DFS ID）から選ばせる。
/// ヒエラルキーのドラッグは DFS ID を運ぶので、その ID で候補表を引けば
/// IPC 往復なしに相対パスへ変換できる。
/// </summary>
public partial class InspectorPanel
{
    // ── 定数（マジックナンバー排除）─────────────────────────

    /// <summary>ボーン行の左右パディング・行間。</summary>
    private static readonly Thickness BoneRowMargin = new(0, 1, 0, 1);

    /// <summary>ボーン名列の幅（px）。深い階層でも名前が読めるだけの幅を確保する。</summary>
    private const double BoneNameColumnWidth = 96.0;

    /// <summary>行内アイコン（警告・解除）の一辺サイズ（px）。</summary>
    private const double BoneRowIconSize = 12.0;

    /// <summary>「ボーンアクターを生成」ボタンのアイコンサイズ（px）。</summary>
    private const double BoneButtonIconSize = 14.0;

    /// <summary>ボーン表の最大表示高さ（px）。これを超えたらスクロールさせる。</summary>
    private const double BoneListMaxHeight = 220.0;

    /// <summary>行の文字サイズ。</summary>
    private const double BoneRowFontSize = 11.0;

    /// <summary>自動解決された行のパス表示色（薄字）。</summary>
    private static readonly Brush BoneAutoBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>明示指定された行のパス表示色（通常字）。</summary>
    private static readonly Brush BoneOverrideBrush = new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xDC));

    /// <summary>未解決の行の色（警告）。</summary>
    private static readonly Brush BoneUnresolvedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0x5A));

    /// <summary>ボーン名列の文字色。</summary>
    private static readonly Brush BoneNameBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));

    /// <summary>ドラッグホバー中の行のハイライト背景。</summary>
    private static readonly Brush BoneDropHoverBrush = new SolidColorBrush(Color.FromArgb(0x44, 0x4C, 0x9E, 0xFF));

    // ── ランタイムから受け取るデータ ────────────────────────

    /// <summary>ボーン 1 本ぶんの解決状況。</summary>
    /// <param name="Name">メッシュ内のボーン名。</param>
    /// <param name="Parent">親ボーン名（空 = ルートボーン）。表示のインデントに使う。</param>
    /// <param name="Path">実際に解決されたアクターの相対パス（未解決なら空）。</param>
    /// <param name="Resolved">解決できたか。false なら無変形で描画されている。</param>
    /// <param name="IsOverride">bone_overrides の明示エントリで解決したか。</param>
    public sealed record SkinBoneRow(
        string Name, string Parent, string Path, bool Resolved, bool IsOverride);

    /// <summary>明示指定の候補（スプライトルート配下のアクター）。</summary>
    /// <param name="Path">スプライトルート基準の相対パス。</param>
    /// <param name="Dfs">アクターの DFS ID（ヒエラルキーのドラッグと突き合わせる）。</param>
    public sealed record SkinBoneCandidate(string Path, int Dfs);

    /// <summary>
    /// スロットのボーン一覧（null を空リストへ正規化する）。
    /// SlotInfo は位置指定レコードで既定値に null しか置けないため、参照側はこれを通す。
    /// </summary>
    private static IReadOnlyList<SkinBoneRow> BonesOf(SlotInfo info)
        => info.SkinBonesRaw ?? Array.Empty<SkinBoneRow>();

    /// <summary>スロットの明示指定候補一覧（null を空リストへ正規化する）。</summary>
    private static IReadOnlyList<SkinBoneCandidate> BoneCandidatesOf(SlotInfo info)
        => info.SkinBoneCandidatesRaw ?? Array.Empty<SkinBoneCandidate>();

    /// <summary>ACTOR_COMPONENTS の "bones" 配列を読む。要素が無ければ空リスト。</summary>
    private static List<SkinBoneRow> ParseSkinBones(JsonElement comp)
    {
        var list = new List<SkinBoneRow>();
        if (!comp.TryGetProperty("bones", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in arr.EnumerateArray())
        {
            list.Add(new SkinBoneRow(
                e.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                e.TryGetProperty("parent", out var p) ? p.GetString() ?? "" : "",
                e.TryGetProperty("path", out var q) ? q.GetString() ?? "" : "",
                e.TryGetProperty("resolved", out var r) && r.GetInt32() != 0,
                e.TryGetProperty("override", out var o) && o.GetInt32() != 0));
        }
        return list;
    }

    /// <summary>ACTOR_COMPONENTS の "bone_candidates" 配列を読む。要素が無ければ空リスト。</summary>
    private static List<SkinBoneCandidate> ParseSkinBoneCandidates(JsonElement comp)
    {
        var list = new List<SkinBoneCandidate>();
        if (!comp.TryGetProperty("bone_candidates", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var e in arr.EnumerateArray())
        {
            list.Add(new SkinBoneCandidate(
                e.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                e.TryGetProperty("dfs", out var d) ? d.GetInt32() : -1));
        }
        return list;
    }

    // ── 送信 ────────────────────────────────────────────────

    /// <summary>
    /// ボーン対応表（明示指定ぶんだけ）をランタイムへ一括送信する。
    ///
    /// 「1 行だけ変えるのに全体を送る」のは、bone_overrides が
    /// **1 フィールドの丸ごと差し替え**として Undo に載るためである
    /// （field_edit.rs が "bone_overrides" キーで 1 件にまとめる）。
    /// 行ごとの差分 IPC にすると Undo が行数ぶんに割れて操作感が悪くなる。
    /// </summary>
    private void SendSkinBoneOverrides(int slotIdx, IEnumerable<SkinBoneRow> rows)
    {
        if (_currentActorId < 0) return;
        var map = new Dictionary<string, string>();
        foreach (var r in rows)
        {
            // 明示指定のみ送る。自動解決の行は送らない（＝ ランタイム側で自動解決に戻る）
            if (r.IsOverride && !string.IsNullOrEmpty(r.Path))
                map[r.Name] = r.Path;
        }
        var json = JsonSerializer.Serialize(map);
        _runtime?.SendToRuntime(
            $"SET_SKINNED_SPRITE_BONE_OVERRIDES:{_currentActorId},{slotIdx},{json}");
    }

    /// <summary>1 行の明示指定を差し替えて送信する（path が null なら自動解決へ戻す）。</summary>
    private void ApplySkinBoneOverride(
        SlotInfo info, IReadOnlyList<SkinBoneRow> rows, string boneName, string? path)
    {
        var updated = rows.Select(r => r.Name == boneName
            ? r with { Path = path ?? "", IsOverride = path is not null }
            : r).ToList();
        SendSkinBoneOverrides(info.SlotIdx, updated);
    }

    // ── UI 構築 ─────────────────────────────────────────────

    /// <summary>
    /// ボーン対応表 UI を構築する。
    ///
    /// メッシュ未設定・読み込み失敗のときはランタイムが "bones" を送ってこないので、
    /// 表の代わりに案内文だけを出す（存在しない表を空で見せない）。
    /// </summary>
    private UIElement BuildSkinnedSpriteBoneTable(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };

        // ── 見出し行（件数サマリ + 生成ボタン）──
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };

        var genButton = new Button
        {
            Padding = new Thickness(6, 2, 6, 2),
            ToolTip = "メッシュのボーン宣言から、スプライト配下へ同名の 2D 子アクター階層を作ります。\n"
                    + "バインドポーズが設定されるので、生成直後は無変形のまま表示されます。\n"
                    + "既に同名のアクターがある場所は作り直しません（Ctrl+Z で戻せます）。",
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    AppIcon.Create("Icon.Bone", size: BoneButtonIconSize),
                    new TextBlock
                    {
                        Text = "ボーンアクターを生成",
                        Margin = new Thickness(4, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        FontSize = BoneRowFontSize,
                    },
                },
            },
        };
        genButton.Click += (_, _) =>
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime($"CREATE_SPRITE_BONE_ACTORS:{_currentActorId},{info.SlotIdx}");
        };
        DockPanel.SetDock(genButton, Dock.Right);
        header.Children.Add(genButton);

        var summary = new TextBlock
        {
            FontSize = BoneRowFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        header.Children.Add(summary);
        sp.Children.Add(header);

        var bones = BonesOf(info);
        if (bones.Count == 0)
        {
            summary.Text = string.IsNullOrEmpty(info.SkinMeshPath)
                ? "ボーン対応: メッシュ（.sprite_mesh）を指定するとボーン一覧が表示されます"
                : "ボーン対応: メッシュを読み込めません（形式・パスを確認してください）";
            summary.Foreground = BoneAutoBrush;
            genButton.IsEnabled = false;
            return sp;
        }

        summary.Text = info.SkinBoneUnresolved > 0
            ? $"ボーン {bones.Count} 本中 {info.SkinBoneUnresolved} 本が未解決"
            : $"ボーン {bones.Count} 本すべて解決済み";
        summary.Foreground = info.SkinBoneUnresolved > 0 ? BoneUnresolvedBrush : BoneAutoBrush;

        // ── ボーン行 ──
        var list = new StackPanel();
        foreach (var bone in bones)
            list.Children.Add(BuildSkinBoneRow(info, bones, bone));

        sp.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = BoneListMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        sp.Children.Add(new TextBlock
        {
            Text = "薄字 = 自動解決（同名の子孫アクター）／通常字 = 明示指定。\n"
                 + "ヒエラルキーからアクターをドロップするか、パス欄をクリックして指定します。",
            Foreground = BoneAutoBrush,
            FontSize = BoneRowFontSize - 1,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        return sp;
    }

    /// <summary>ボーン 1 行（名前 / 解決先パス / 解除ボタン）を構築する。</summary>
    private UIElement BuildSkinBoneRow(
        SlotInfo info, IReadOnlyList<SkinBoneRow> allRows, SkinBoneRow bone)
    {
        var row = new DockPanel
        {
            LastChildFill = true,
            Margin = BoneRowMargin,
            Background = Brushes.Transparent, // Transparent でないとドロップイベントが来ない
            AllowDrop = true,
        };

        // 未解決の警告アイコン（解決済みの行では場所だけ確保しない＝出さない）
        if (!bone.Resolved)
        {
            var warn = AppIcon.Create("Icon.Warning", size: BoneRowIconSize);
            warn.Margin = new Thickness(0, 0, 3, 0);
            warn.VerticalAlignment = VerticalAlignment.Center;
            warn.SetBrush(BoneUnresolvedBrush);
            warn.ToolTip = "対応するアクターが見つかりません。このボーンはバインドポーズ（無変形）で描画されます。";
            DockPanel.SetDock(warn, Dock.Left);
            row.Children.Add(warn);
        }

        // ボーン名（ルートでなければ 1 段インデントして親子関係を示す）
        var nameBlock = new TextBlock
        {
            Text = bone.Name,
            Width = BoneNameColumnWidth,
            FontSize = BoneRowFontSize,
            Foreground = bone.Resolved ? BoneNameBrush : BoneUnresolvedBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(string.IsNullOrEmpty(bone.Parent) ? 0 : 10, 0, 4, 0),
            ToolTip = string.IsNullOrEmpty(bone.Parent)
                ? $"{bone.Name}（ルートボーン）"
                : $"{bone.Name}（親: {bone.Parent}）",
        };
        DockPanel.SetDock(nameBlock, Dock.Left);
        row.Children.Add(nameBlock);

        // 明示指定の解除ボタン（明示指定の行だけ出す）
        if (bone.IsOverride)
        {
            var clear = new Button
            {
                Padding = new Thickness(2),
                Margin = new Thickness(3, 0, 0, 0),
                ToolTip = "明示指定を解除して自動解決に戻す",
                Content = AppIcon.Create("Icon.Close", size: BoneRowIconSize),
            };
            clear.Click += (_, _) => ApplySkinBoneOverride(info, allRows, bone.Name, null);
            DockPanel.SetDock(clear, Dock.Right);
            row.Children.Add(clear);
        }

        // 解決先パス（クリックで候補選択、ドロップでヒエラルキーから設定）
        var pathBlock = new TextBlock
        {
            Text = bone.Resolved ? bone.Path : "(未解決)",
            FontSize = BoneRowFontSize,
            Foreground = !bone.Resolved ? BoneUnresolvedBrush
                       : bone.IsOverride ? BoneOverrideBrush
                       : BoneAutoBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Cursor = Cursors.Hand,
            ToolTip = "クリックで解決先アクターを選択（ヒエラルキーからのドロップでも設定できます）",
        };
        pathBlock.MouseLeftButtonUp += (_, _) => PickSkinBoneTarget(info, allRows, bone);
        row.Children.Add(pathBlock);

        // ── ヒエラルキー／シーンビューからのドラッグ受け入れ ──
        row.DragOver += (_, e) =>
        {
            var ok = ResolveDroppedBonePath(info, e.Data) is not null;
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            row.Background = ok ? BoneDropHoverBrush : Brushes.Transparent;
            e.Handled = true;
        };
        row.DragLeave += (_, _) => row.Background = Brushes.Transparent;
        row.Drop += (_, e) =>
        {
            row.Background = Brushes.Transparent;
            e.Handled = true;
            var path = ResolveDroppedBonePath(info, e.Data);
            if (path is null)
            {
                MessageBox.Show(
                    "このアクターはスプライト配下にないため、ボーンとして指定できません。\n"
                    + "ボーンはスキンスプライトを持つアクターの子孫でなければなりません。",
                    "ボーン指定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ApplySkinBoneOverride(info, allRows, bone.Name, path);
        };

        return row;
    }

    /// <summary>
    /// ドラッグデータ（ヒエラルキー／シーンビューのアクター DFS ID）を
    /// スプライトルート基準の相対パスへ変換する。
    ///
    /// ランタイムが送ってきた候補一覧に DFS ID が入っているので、IPC 往復なしで
    /// 引ける。候補に無い＝スプライト配下のアクターではないので `null` を返す。
    /// </summary>
    private static string? ResolveDroppedBonePath(SlotInfo info, IDataObject data)
    {
        var dfs = ReferenceDragData.TryGetActorDfsId(data);
        if (dfs is null) return null;
        foreach (var c in BoneCandidatesOf(info))
            if (c.Dfs == dfs.Value)
                return c.Path;
        return null;
    }

    /// <summary>
    /// 解決先アクターを候補一覧から選ばせる（キャンセル時は何もしない）。
    ///
    /// 先頭に「自動解決に戻す」を置き、それが選ばれたら明示指定を消す。
    /// </summary>
    private void PickSkinBoneTarget(
        SlotInfo info, IReadOnlyList<SkinBoneRow> allRows, SkinBoneRow bone)
    {
        const string AutoResolveItem = "（自動解決に戻す）";

        var candidates = BoneCandidatesOf(info);
        if (candidates.Count == 0)
        {
            MessageBox.Show(
                "スプライト配下に 2D 子アクターがありません。\n"
                + "「ボーンアクターを生成」でメッシュのボーン階層を作ってください。",
                "ボーン指定", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var items = new List<string> { AutoResolveItem };
        items.AddRange(candidates.Select(c => c.Path));

        var selected = ReferenceSelectorWindow.Show(
            Window.GetWindow(this),
            new ReferenceSelectorPage(
                $"ボーン「{bone.Name}」の解決先",
                "このボーンとして使うアクターを選択してください。\n"
                + "パスはスキンスプライトを持つアクターからの相対パスです。",
                items));
        if (selected is not { Count: > 0 }) return;

        var choice = selected[0];
        ApplySkinBoneOverride(info, allRows, bone.Name,
            choice == AutoResolveItem ? null : choice);
    }
}
