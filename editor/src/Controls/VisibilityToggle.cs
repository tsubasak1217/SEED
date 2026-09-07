using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Controls;

/// <summary>
/// アクターの「表示 / 非表示」を切り替える目アイコンのトグル UI を組み立てる共有ファクトリ。
///
/// ヒエラルキーの各行と、インスペクタのアクタ名の横の 2 か所から使う。
/// 見た目・当たり判定・IPC 文字列の組み立てをここ 1 か所に集約することで、
/// 「片方だけ挙動が違う」というズレを構造的に防ぐ。
///
/// このクラスは UI の見た目だけを担い、状態は持たない（押されたら
/// <c>onToggle(新しい値)</c> を呼ぶだけ。実際の状態はランタイム側が正典）。
/// </summary>
public static class VisibilityToggle
{
    // ── アイコンリソースキー（editor/gen_icons.py の CATALOG と対応）─────────

    /// <summary>表示中に出すアイコン（開いた目）のリソースキー。</summary>
    public const string IconKeyVisible = "Icon.Node.Visible";

    /// <summary>非表示中に出すアイコン（閉じた目）のリソースキー。</summary>
    public const string IconKeyHidden = "Icon.Node.Hidden";

    // ── 見た目の定数（マジックナンバー禁止のため名前を付ける）──────────────

    /// <summary>アイコンの既定の一辺サイズ（px）。ヒエラルキー行の文字高に合わせた値。</summary>
    public const double DefaultIconSize = 12.0;

    /// <summary>表示中のアイコン不透明度。主張しすぎない程度に落とす。</summary>
    private const double VisibleIconOpacity = 0.70;

    /// <summary>非表示中のアイコン不透明度。「薄いアイコン」で状態を伝える。</summary>
    private const double HiddenIconOpacity = 0.30;

    /// <summary>アイコンの左右に空ける余白（px）。</summary>
    private const double IconMarginRight = 4.0;

    /// <summary>クリック当たり判定を広げるための内側余白（px）。</summary>
    private const double HitPadding = 1.0;

    /// <summary>非表示の行・要素を淡色表示するときの不透明度（呼び出し側が行全体へ適用する）。</summary>
    public const double DimmedRowOpacity = 0.45;

    // ── IPC ────────────────────────────────────────────────────────────

    /// <summary>表示切替 IPC コマンドの接頭辞。ランタイム側 <c>ipc.rs</c> のパーサと一致必須。</summary>
    private const string SetVisibleCommandPrefix = "SET_VISIBLE";

    /// <summary>
    /// 表示切替コマンド文字列を組み立てる（<c>SET_VISIBLE:{dfsId},{0|1}</c>）。
    /// </summary>
    /// <param name="actorDfsId">対象アクターの DFS 順 ID。</param>
    /// <param name="visible">設定したい表示フラグ。</param>
    public static string BuildCommand(int actorDfsId, bool visible)
        => $"{SetVisibleCommandPrefix}:{actorDfsId},{(visible ? 1 : 0)}";

    // ── UI 生成 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 目アイコンのトグル要素を作る。
    ///
    /// クリック（PreviewMouseLeftButtonDown）は必ずここで握りつぶす。
    /// そうしないとヒエラルキーの行選択やインスペクタのフォーカス移動が同時に走ってしまう。
    /// </summary>
    /// <param name="visible">現在の表示状態（true = 表示中）。アイコンと不透明度を決める。</param>
    /// <param name="onToggle">クリック時に呼ばれるコールバック。引数は「切り替え後の値」（= !visible）。</param>
    /// <param name="size">アイコンの一辺サイズ（px）。既定は <see cref="DefaultIconSize"/>。</param>
    /// <param name="brush">アイコンの塗り色。null なら親の Foreground を継承する。</param>
    /// <returns>クリック可能な UI 要素（Border で包んで当たり判定を確保している）。</returns>
    public static FrameworkElement Create(
        bool          visible,
        Action<bool>  onToggle,
        double        size  = DefaultIconSize,
        Brush?        brush = null)
    {
        var icon = AppIcon.Create(visible ? IconKeyVisible : IconKeyHidden, size);
        if (brush is not null) icon.SetBrush(brush);

        // Border で包む理由は 2 つ:
        //  1. Background を明示しないと透明部分がヒットテストを通さない（= 押しにくい）
        //  2. Padding でクリック領域を少し広げられる
        var host = new Border
        {
            Child               = icon,
            Background          = Brushes.Transparent,
            Padding             = new Thickness(HitPadding),
            Margin              = new Thickness(0, 0, IconMarginRight, 0),
            Opacity             = visible ? VisibleIconOpacity : HiddenIconOpacity,
            Cursor              = Cursors.Hand,
            VerticalAlignment   = VerticalAlignment.Center,
            ToolTip             = visible ? "表示中（クリックで非表示にする）"
                                          : "非表示（クリックで表示する）",
        };

        host.PreviewMouseLeftButtonDown += (_, e) =>
        {
            // 行選択・ドラッグ開始・リネームタイマなど、下位のハンドラへは流さない。
            e.Handled = true;
            onToggle(!visible);
        };

        return host;
    }
}
