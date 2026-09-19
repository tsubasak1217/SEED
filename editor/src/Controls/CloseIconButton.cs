// ============================================================
//  CloseIconButton.cs — 「×（閉じる・解除・削除）」ボタンを作る共通の窓口
//
//  【役割】
//  エディタ中の「× で消す」ボタンを 1 か所で組み立てる。
//  見た目（アイコンの大きさ）は呼び出し側が指定した値のまま変えず、
//  押せる範囲（当たり判定）だけを SeedButtonMetrics の規定まで広げる。
//
//  【なぜ必要か】
//  以前は 10〜12px の AppIcon へ直接マウスハンドラを付けていたため、
//  当たり判定がアイコンの絵の大きさしかなく「小さすぎて押しづらい」
//  という指摘を受けた。各所で個別に広げると寸法がまた散らばるので、
//  生成をここへ 1 本化する。
//
//  【実体を Button にしている理由】
//  ・ホバー／押下の見た目、キーボード操作、UI Automation の Invoke が
//    共通書式（Seed.Button.Icon）でそのまま揃う。
//  ・ButtonBase は MouseLeftButtonDown を自分で処理して e.Handled を立てるため、
//    ドラッグ元や開閉トグルを兼ねた行・見出しの中に置いても、
//    × を押しただけでドラッグや開閉が始まらない（親のハンドラへ伝播しない）。
//    このため、以前 MouseDown で閉じていた箇所も Click（離した瞬間）へ揃う。
//
//  【色について】
//  ボタン自身の色は共通書式（Theme/SeedButtonStyles.xaml）が決める。
//  ここで Background / Foreground / BorderBrush を書かないこと。
//  アイコンの塗り色だけは、既存箇所の強調色（赤系の削除アイコンなど）を
//  保つために任意指定できる。指定しなければ共通書式の色を継ぐ。
// ============================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Theme;

namespace SEEDEditor.Controls;

/// <summary>
/// 「×」ボタンの共通ファクトリ。エディタ中の <c>Icon.Close</c> ボタンはすべてここを通す。
/// </summary>
public static class CloseIconButton
{
    /// <summary>閉じる・解除・削除に使うベクターアイコンのリソースキー（Icons.xaml）。</summary>
    public const string IconKey = "Icon.Close";

    /// <summary>内側余白の既定値。当たり判定を正方形で確保するため 0 にして中央へ置く。</summary>
    private static readonly Thickness NoPadding = new(0);

    /// <summary>
    /// 「×」ボタンを 1 個作る。
    /// </summary>
    /// <param name="iconSize">
    /// アイコン（見た目）の一辺 [px]。**この値は当たり判定の計算にだけ使い、
    /// アイコン自体の大きさは変えない**。呼び出し元の既存サイズをそのまま渡すこと。
    /// </param>
    /// <param name="tooltip">ツールチップ文言（null なら付けない）。</param>
    /// <param name="onClick">押されたときの処理（null なら呼び出し側で Click を足す）。</param>
    /// <param name="iconBrush">
    /// アイコンの塗り色。null なら共通書式の色を継ぐ（ホバーで明るくなる）。
    /// 既存箇所の強調色を保つときだけ指定する。
    /// </param>
    /// <param name="hoverIconBrush">
    /// ホバー中のアイコン色。<paramref name="iconBrush"/> を指定した場合、
    /// 塗り色を固定したぶん共通書式のホバー色が効かなくなるため、
    /// 色でホバーを示していた箇所はここへ元の色を渡して挙動を保つ。
    /// </param>
    /// <param name="tag">ボタンへ持たせる任意データ（Click ハンドラ側で対象を引くために使う）。</param>
    /// <param name="margin">外側余白（null なら 0）。</param>
    /// <param name="padding">
    /// 内側余白（null なら 0）。既に余白込みで当たり判定が足りている箇所が
    /// 横幅を縮めないために指定する。
    /// </param>
    /// <param name="verticalBleed">
    /// 上下の余白へはみ出させる量 [px]。行やタブの高さを変えずに当たり判定を確保したいとき、
    /// この分だけ負のマージンを入れて周囲の余白を食う（描画と当たり判定は
    /// はみ出した分も有効で、レイアウト上の要求高さだけが小さくなる）。
    /// </param>
    /// <param name="styleKey">
    /// 共通書式のスタイルキー（<see cref="SeedButtonStyle"/> の定数）。
    /// 既定はアイコン専用（背景は透明でホバー時だけ薄く光る）。
    /// 枠線つきの小ボタンが並ぶ行（参照ピッカーなど）で見た目を揃えたいときだけ
    /// <see cref="SeedButtonStyle.OUTLINED"/> を渡す。ここに色は書かない。
    /// </param>
    /// <returns>共通書式が当たった「×」ボタン。</returns>
    public static Button Create(
        double     iconSize,
        string?    tooltip        = null,
        Action?    onClick        = null,
        Brush?     iconBrush      = null,
        Brush?     hoverIconBrush = null,
        object?    tag            = null,
        Thickness? margin         = null,
        Thickness? padding        = null,
        double     verticalBleed  = 0,
        string     styleKey       = SeedButtonStyle.ICON)
    {
        // 当たり判定の一辺。アイコンの見た目は iconSize のまま変えない。
        var hit = SeedButtonMetrics.IconHitAreaSize(iconSize);

        var icon = AppIcon.Create(IconKey, iconSize);
        if (iconBrush != null) icon.SetBrush(iconBrush);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment   = VerticalAlignment.Center;

        var button = new Button
        {
            Content = icon,
            // Width/Height ではなく Min* にしておくと、呼び出し側が
            // もっと大きい寸法を持っていた場合にそれを縮めてしまわない。
            MinWidth  = hit,
            MinHeight = hit,
            Padding   = padding ?? NoPadding,
            Margin    = WithVerticalBleed(margin ?? default, verticalBleed),
            VerticalAlignment          = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment   = VerticalAlignment.Center,
            Cursor    = Cursors.Hand,
            ToolTip   = tooltip,
            Tag       = tag,
        };

        // 色・ホバー・押下・無効の見た目は共通書式に任せる（自前で色を決めない）。
        // Application が無い文脈（オフスクリーン検証など）では何も起きず、
        // 暗黙スタイルのままになる＝画面は壊れない。
        SeedButtonStyle.Apply(button, styleKey);

        // アイコン色を固定した箇所だけ、ホバーの色替えを自前で持つ。
        if (iconBrush != null && hoverIconBrush != null)
        {
            button.MouseEnter += (_, _) => icon.SetBrush(hoverIconBrush);
            button.MouseLeave += (_, _) => icon.SetBrush(iconBrush);
        }

        if (onClick != null)
            button.Click += (_, _) => onClick();

        return button;
    }

    /// <summary>
    /// 外側余白の上下から <paramref name="bleed"/> だけ引いた余白を返す。
    ///
    /// 負のマージンは「描画・当たり判定はそのままで、レイアウト上の要求サイズだけを縮める」
    /// 働きをするので、行の高さを保ったまま当たり判定を上下へ広げられる。
    /// </summary>
    /// <param name="margin">元の外側余白。</param>
    /// <param name="bleed">上下それぞれへはみ出す量 [px]（0 なら何もしない）。</param>
    private static Thickness WithVerticalBleed(Thickness margin, double bleed)
        => bleed <= 0
            ? margin
            : new Thickness(margin.Left, margin.Top - bleed, margin.Right, margin.Bottom - bleed);
}
