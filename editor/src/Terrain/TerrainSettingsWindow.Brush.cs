// ============================================================
//  TerrainSettingsWindow.Brush.cs — 地形設定ウィンドウ「ブラシ」タブ
//
//  【責務】
//    地形編集ブラシの **共通設定** を編集する。現在の項目はブラシテクスチャ
//    （形状マスク）1 つだけで、対象はレイヤペイントブラシとカバーブラシ。
//
//  【なぜツールごとではなく「共通設定」なのか】
//    半径・強度は既にツール横断で共有されている（ツールを切り替えても
//    スライダーの値は据え置き）。同じ「ブラシの当たり方」を決めるパラメータの
//    うちテクスチャだけをツールごとに分けると、
//      ・レイヤペイントで形を決めた直後にカバーへ切り替えると形が消える
//      ・どのツールのテクスチャを編集しているのか UI から読み取れない
//    という一貫性の無さが出る。設定は 1 行だけ置き、両ブラシで共有する。
//
//  【ランタイムとの関係】
//    値そのものはランタイム（Rust）の `TerrainState::brush_mask_path` が持つ。
//    本タブは `TERRAIN_BRUSH_MASK:{path}`（空文字で解除）を送るだけで、
//    画像のデコード・キャッシュ・サンプリングはすべてランタイム側が行う。
//    エディタ側は「今どれを選んでいるか」の表示のために同じ値を控える
//    （ウィンドウを開き直しても選択が消えないよう、実体は MainWindow が保持する）。
// ============================================================

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Panels;

namespace SEEDEditor.Terrain;

public partial class TerrainSettingsWindow
{
    // ── 定数（マジックナンバー禁止）────────────────────────────

    /// <summary>ブラシ形状マスクとして受け付ける画像拡張子（ランタイムの image クレートが読めるもの）。</summary>
    private static readonly string[] BrushMaskExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp" };

    /// <summary>ファイル選択ダイアログのフィルタ文字列。</summary>
    private const string BrushMaskDialogFilter = "画像ファイル|*.png;*.jpg;*.jpeg;*.tga;*.bmp";

    /// <summary>説明文の色（グレー。他タブの補足テキストと同じ）。</summary>
    private static readonly Color BrushHintColor = Color.FromRgb(0x88, 0x88, 0x88);

    /// <summary>説明文どうしの縦マージン（px）。</summary>
    private const double BrushHintMargin = 6;

    /// <summary>ブラシ形状マスクを解除する（＝未指定）ことを表す IPC 値。</summary>
    private const string BrushMaskNone = "";

    // ── 状態 ──────────────────────────────────────────────────

    /// <summary>
    /// 現在のブラシ形状マスクのパス（null／空 = 未指定 ＝ 従来どおりの円形フォールオフ）。
    /// 実体は MainWindow が持ち、生成時に受け取って表示に使う。
    /// </summary>
    private string? _brushMaskPath;

    /// <summary>
    /// ブラシ形状マスクが変わったときに呼ぶコールバック（引数は解除時 null）。
    /// MainWindow がこれを受けて値を保持し、`TERRAIN_BRUSH_MASK` をランタイムへ送る。
    /// </summary>
    private Action<string?>? _onBrushMaskChanged;

    // ── 組み立て ──────────────────────────────────────────────

    /// <summary>
    /// 「ブラシ」タブを初期化する（コンストラクタから 1 回だけ呼ぶ）。
    /// </summary>
    /// <param name="brushMaskPath">現在のブラシ形状マスク（未指定なら null／空文字）。</param>
    /// <param name="onBrushMaskChanged">変更通知（MainWindow が値の保持と IPC 送信を行う）。</param>
    private void InitBrushTab(string? brushMaskPath, Action<string?>? onBrushMaskChanged)
    {
        _brushMaskPath      = string.IsNullOrEmpty(brushMaskPath) ? null : brushMaskPath;
        _onBrushMaskChanged = onBrushMaskChanged;
        RebuildBrushPanel();
    }

    /// <summary>
    /// ブラシタブの中身を組み立て直す。
    /// FileRefBuilder が生成する行は「今のパス」を焼き込むため、
    /// 値が変わったら行ごと作り直す（レイヤタブのテクスチャ行と同じ流儀）。
    /// </summary>
    private void RebuildBrushPanel()
    {
        if (BrushTabPanel == null) return;
        BrushTabPanel.Children.Clear();

        BrushTabPanel.Children.Add(new TextBlock
        {
            Text  = "ブラシ設定（共通）",
            Style = (Style)FindResource("SectionHeaderStyle"),
        });

        // ── ブラシテクスチャ参照行（× ボタンで解除） ──
        BrushTabPanel.Children.Add(FileRefBuilder.Build(
            "テクスチャ",
            _brushMaskPath,
            BrushMaskExtensions,
            browseFn: () =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title            = "ブラシテクスチャ（グレースケール）を選択",
                    Filter           = BrushMaskDialogFilter,
                    InitialDirectory = Directory.Exists(_assetsRoot) ? _assetsRoot : Environment.CurrentDirectory,
                };
                return dlg.ShowDialog(this) == true ? dlg.FileName : null;
            },
            onPathSet: path => ApplyBrushMask(ToAssetRelativePath(path)),
            onClear:   () => ApplyBrushMask(null)));

        BrushTabPanel.Children.Add(new TextBlock
        {
            Foreground        = new SolidColorBrush(BrushHintColor),
            TextWrapping      = TextWrapping.Wrap,
            Margin            = new Thickness(0, BrushHintMargin, 0, 0),
            Text =
                "グレースケール画像をブラシの形状マスクとして使う。白 = フル強度 / 黒 = 効果なし。"
                + "画像はブラシ球の XZ バウンディング正方形（一辺 = 半径 × 2・中心 = 着弾点）へ"
                + "回転せずに貼り付けられ、画像の上端が -Z 側・左端が -X 側に対応する。",
        });

        BrushTabPanel.Children.Add(new TextBlock
        {
            Foreground        = new SolidColorBrush(BrushHintColor),
            TextWrapping      = TextWrapping.Wrap,
            Margin            = new Thickness(0, BrushHintMargin, 0, 0),
            Text =
                "対象はレイヤペイントブラシとカバーブラシ（塗り／消去）。密度ブラシ（盛る・掘る・均す・平坦化）"
                + "と散布ブラシには効かない。未指定のときは従来どおりの円形フォールオフになる。"
                + "半径・強度と同じくツール共通の設定であり、シーンには保存されない。",
        });
    }

    /// <summary>
    /// ブラシ形状マスクを設定・解除する共通処理。
    /// 値を控えて MainWindow へ通知し、表示（ファイル名・× ボタンの活性）を作り直す。
    /// </summary>
    /// <param name="path">assets 相対化済みパス。解除時は null。</param>
    private void ApplyBrushMask(string? path)
    {
        _brushMaskPath = string.IsNullOrEmpty(path) ? null : path;
        _onBrushMaskChanged?.Invoke(_brushMaskPath);
        RebuildBrushPanel();
        SetStatus(
            _brushMaskPath == null
                ? "ブラシテクスチャを解除しました（円形フォールオフに戻ります）"
                : $"ブラシテクスチャを設定しました: {_brushMaskPath}",
            ok: true);
    }

    /// <summary>
    /// 「未指定」を表す IPC 値へ正規化する（null → 空文字）。
    /// MainWindow が `TERRAIN_BRUSH_MASK` を組み立てるときに使う。
    /// </summary>
    internal static string NormalizeBrushMaskForIpc(string? path)
        => string.IsNullOrEmpty(path) ? BrushMaskNone : path;
}
