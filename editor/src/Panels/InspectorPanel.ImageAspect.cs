using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Controls;
using SEEDEditor.Panels.Inspector;

namespace SEEDEditor.Panels;

/// <summary>
/// インスペクタの「画像比率に設定」機能（<see cref="InspectorPanel"/> の部分クラス）。
///
/// <para>
/// 画像（テクスチャ）を参照し、かつサイズ（幅・高さ）を持つコンポーネントで、
/// 元画像のピクセル寸法から縦横比を取り、片方の辺を保ったままもう片方を合わせる。
/// 値の反映は <b>既存のサイズ変更と同じ経路</b>（呼び出し側が渡す commit デリゲート
/// ＝ SET_SPRITE_SIZE 等）を通すので、IPC も Undo も二重化しない。
/// </para>
///
/// <para><b>対象コンポーネントの選定根拠</b>:
/// <c>ComponentCatalog</c> と各スロット UI を突き合わせると、
/// 「画像参照 ＋ 自前の幅・高さ」を両方持つのは <c>SpriteComponent</c> だけである。
/// ・3D スプライトも、キャンバス（2D アクター）上の画像も、どちらも同じ
///   <c>SpriteComponent</c>（texture_path ＋ width/height）で表現される。
///   <c>CanvasTransform</c> 自体はサイズを持たずスケール（倍率）しか持たないため、
///   「キャンバスの画像のサイズ」＝そのアクターに載っている Sprite の幅・高さになる。
/// ・<c>SkinnedSpriteComponent</c> は寸法をメッシュ（.sprite_mesh）側が持つので対象外。
/// ・<c>SkyboxComponent</c> / <c>ParticleEmitterComponent</c> は画像を参照するが
///   「元画像の縦横比で決めるべき幅・高さ」を持たない。
/// ・<c>CanvasComponent</c> は幅・高さを持つが画像を参照しない。
/// 対象が増えたときは <see cref="BuildImageAspectRow"/> を呼ぶ行を足すだけでよい。</para>
///
/// <para><b><c>auto_scale</c> との関係</b>:
/// 「Sprite の auto_scale」は実在しない。
/// <c>runtime/src/engine/components/sprite_component.rs</c> の <c>SpriteComponent</c> は
/// texture_path / color / width / height / layer / postfx_path / raycast_target だけを持ち、
/// auto_scale は無い。<c>auto_scale</c> は <c>CanvasComponent</c>
/// （ルートキャンバスをビューポート解像度へ自動追従させるフラグ）のもので、
/// スプライトの幅・高さには関与しない。よって「auto_scale が ON ならボタンを隠す」
/// といった出し分けは不要で、本機能と競合する設定は存在しない。
/// 近い設定である <c>CanvasTransform</c> の scale_size / keep_aspect_ratio は
/// 「親キャンバスの拡縮にどう追従するか」であり、ここで書き換える
/// 基準サイズ（width/height）とは層が違うので、こちらも競合しない。</para>
/// </summary>
public partial class InspectorPanel
{
    // ── 見た目の定数（マジックナンバー禁止）────────────────────────────

    /// <summary>「画像比率に設定」ボタン内アイコンの一辺サイズ（px）。</summary>
    private const double ImageAspectIconSize = 12.0;

    /// <summary>ボタン見出しのフォントサイズ（px）。他の行内ボタンと揃える。</summary>
    private const double ImageAspectButtonFontSize = 10.0;

    /// <summary>ボタンの内側余白。</summary>
    private static readonly Thickness ImageAspectButtonPadding = new(6, 1, 6, 1);

    /// <summary>ボタン行の外側余白（幅・高さ欄のすぐ下に寄せる）。</summary>
    private static readonly Thickness ImageAspectRowMargin = new(0, 0, 0, 4);

    /// <summary>見出しアイコンと文字の間隔。</summary>
    private static readonly Thickness ImageAspectLabelMargin = new(3, 0, 2, 0);

    /// <summary>ボタンの文字色。</summary>
    private static readonly Color ImageAspectForeground = Color.FromRgb(0xCC, 0xCC, 0xCC);

    /// <summary>ボタンの背景色。</summary>
    private static readonly Color ImageAspectBackground = Color.FromRgb(0x2A, 0x2A, 0x2A);

    /// <summary>ボタンの枠線色。</summary>
    private static readonly Color ImageAspectBorder = Color.FromRgb(0x55, 0x55, 0x55);

    /// <summary>ボタンの枠線太さ。</summary>
    private const double ImageAspectBorderThickness = 1.0;

    // ── アイコンキー（editor/gen_icons.py の CATALOG と対応）───────────

    /// <summary>ボタン見出しのアイコン（縦横比）。</summary>
    private const string ImageAspectIconKey = "Icon.AspectRatio";

    /// <summary>ドロップダウンであることを示すアイコン（下向き山形）。</summary>
    private const string ImageAspectDropDownIconKey = "Icon.MoveDown";

    // ── 表示文言 ───────────────────────────────────────────────────────

    /// <summary>ボタンの見出し。</summary>
    private const string ImageAspectButtonLabel = "画像比率に設定";

    /// <summary>ボタンのツールチップ（元画像の寸法が取れたときは寸法を添える）。</summary>
    private const string ImageAspectToolTipBase =
        "元画像の縦横比に合わせて、幅または高さを計算し直します。";

    /// <summary>元画像の寸法が読めなかったときのツールチップ。</summary>
    private const string ImageAspectUnreadableToolTip =
        "元画像の寸法を読み取れませんでした（未対応形式・ファイルが見つからない等）。";

    /// <summary>
    /// メニュー項目の定義（データドリブン）。
    /// 追加・文言変更はこの表だけを直せばよい。
    /// </summary>
    private static readonly IReadOnlyList<(string Label, ImageAspectAxis Axis)> ImageAspectMenuItems =
    [
        ("縦基準（高さを保ち幅を合わせる）", ImageAspectAxis.KeepHeight),
        ("横基準（幅を保ち高さを合わせる）", ImageAspectAxis.KeepWidth),
    ];

    // ── UI 生成 ────────────────────────────────────────────────────────

    /// <summary>
    /// 「画像比率に設定」ボタンの行を作る。
    ///
    /// 画像が設定されていないときは <c>null</c> を返す（呼び出し側は行を追加しない）。
    /// これは「選択に応じて無関係なパラメータは出さない」方針
    /// （.claude/rules/editor-panels.md）に合わせたもので、
    /// 比率の取りようがない状態でボタンだけ並べない。
    /// </summary>
    /// <param name="texturePath">画像の参照パス（仮想パス <c>assets://</c> でも絶対パスでも可）。</param>
    /// <param name="widthBox">幅フィールド。</param>
    /// <param name="heightBox">高さフィールド。</param>
    /// <param name="commitSize">
    /// サイズ確定処理。既存のサイズ変更と同じ経路（SET_SPRITE_SIZE 等）を渡すこと。
    /// </param>
    /// <returns>ボタン行。画像未設定なら null。</returns>
    private UIElement? BuildImageAspectRow(
        string  texturePath,
        TextBox widthBox,
        TextBox heightBox,
        Action  commitSize)
    {
        if (string.IsNullOrWhiteSpace(texturePath)) return null;

        // ボタン中身: [比率アイコン] 画像比率に設定 [下向き山形アイコン]
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(AppIcon.Create(ImageAspectIconKey, ImageAspectIconSize));
        content.Children.Add(new TextBlock
        {
            Text              = ImageAspectButtonLabel,
            FontSize          = ImageAspectButtonFontSize,
            Margin            = ImageAspectLabelMargin,
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(AppIcon.Create(ImageAspectDropDownIconKey, ImageAspectIconSize));

        var button = new Button
        {
            Content             = content,
            Foreground          = new SolidColorBrush(ImageAspectForeground),
            Background          = new SolidColorBrush(ImageAspectBackground),
            BorderBrush         = new SolidColorBrush(ImageAspectBorder),
            BorderThickness     = new Thickness(ImageAspectBorderThickness),
            Padding             = ImageAspectButtonPadding,
            Margin              = ImageAspectRowMargin,
            HorizontalAlignment = HorizontalAlignment.Left,
            Cursor              = Cursors.Hand,
            ToolTip             = BuildImageAspectToolTip(texturePath),
        };

        // クリックでメニューを開く（項目は上の表から生成する）
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = button };
            foreach (var (label, axis) in ImageAspectMenuItems)
            {
                var item         = new MenuItem { Header = label };
                var capturedAxis = axis;
                item.Click += (_, _) =>
                    ApplyImageAspect(texturePath, widthBox, heightBox, commitSize, capturedAxis);
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };

        return button;
    }

    /// <summary>
    /// ボタンのツールチップを組み立てる。元画像の寸法が読めれば併記する
    /// （読めない形式かどうかを、押す前に気づけるようにするため）。
    /// </summary>
    private string BuildImageAspectToolTip(string texturePath)
    {
        var absolutePath = VirtualPath.ToAbsolute(texturePath, _assetsPath);
        return ImageSizeCache.TryGetPixelSize(absolutePath, out var pw, out var ph)
            ? $"{ImageAspectToolTipBase}\n元画像: {pw} × {ph} px"
            : $"{ImageAspectToolTipBase}\n{ImageAspectUnreadableToolTip}";
    }

    // ── 適用 ───────────────────────────────────────────────────────────

    /// <summary>
    /// 元画像の縦横比を幅・高さフィールドへ適用し、既存のサイズ確定処理を呼ぶ。
    ///
    /// 書き換えるのは片方のフィールドだけで、確定は 1 回しか呼ばない。
    /// これにより IPC も 1 通、Undo も 1 操作になる（既存のサイズ変更と同じ粒度）。
    /// </summary>
    private void ApplyImageAspect(
        string          texturePath,
        TextBox         widthBox,
        TextBox         heightBox,
        Action          commitSize,
        ImageAspectAxis axis)
    {
        var absolutePath = VirtualPath.ToAbsolute(texturePath, _assetsPath);
        if (!ImageSizeCache.TryGetPixelSize(absolutePath, out var pixelW, out var pixelH))
        {
            EditorLog.Write($"[Inspector] 画像比率に設定: 元画像の寸法を取得できません: {absolutePath}");
            return;
        }

        // 現在値。数値として読めないフィールドがある状態では計算しない
        // （ParseOrRestore と違い、ここでは勝手な書き戻しをしない）。
        if (!TryReadFieldValue(widthBox,  out var currentW) ||
            !TryReadFieldValue(heightBox, out var currentH))
        {
            EditorLog.Write("[Inspector] 画像比率に設定: 幅・高さの値を読み取れません");
            return;
        }

        if (!ImageAspectCalculator.TryApply(
                pixelW, pixelH, new SizePair(currentW, currentH), axis, out var next))
        {
            EditorLog.Write(
                $"[Inspector] 画像比率に設定: 比率を適用できません（画像 {pixelW}×{pixelH} / 現在値 {currentW}×{currentH}）");
            return;
        }

        // 書き換えるのは変化した側だけ。表示桁数は元のフィールドの書式に合わせる
        // （幅・高さ行の書式は行の生成側の都合なので、ここで固定値を持たず
        //   現在のテキストから桁数を読み取って揃える）。
        if (MathF.Abs(next.Width - currentW) > ScaleLinkCalculator.ChangeEpsilon)
            widthBox.Text = ScaleLinkCalculator.Format(
                next.Width, ScaleLinkCalculator.DecimalPlacesOf(widthBox.Text));

        if (MathF.Abs(next.Height - currentH) > ScaleLinkCalculator.ChangeEpsilon)
            heightBox.Text = ScaleLinkCalculator.Format(
                next.Height, ScaleLinkCalculator.DecimalPlacesOf(heightBox.Text));

        commitSize();
    }

    /// <summary>
    /// フィールドのテキストを float として読む（書き戻しは行わない）。
    /// </summary>
    private static bool TryReadFieldValue(TextBox box, out float value)
        => float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
