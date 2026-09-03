using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace SEEDEditor;

/// <summary>
/// エディタ全体（全ウィンドウ・全パネル共通）のタッチパッドスクロール挙動。
///
/// - 縦: 精密タッチパッド（delta が 120 の倍数でない入力）を検出し、
///   横方向と同一の感度基準（48px/ノッチ × 環境設定係数）の手動スクロールへ置き換える。
///   物理マウスホイール（±120 の倍数）は WPF 既定処理のまま影響しない。
/// - 横: WPF が UI イベント化しない WM_MOUSEHWHEEL（タッチパッド横スワイプ等）を
///   スレッドメッセージフィルタで受け取り、水平スクロールとして配送する。
///
/// どちらもマウスカーソル直下の要素から祖先を遡って
/// スクロール対象（AvalonEdit / ScrollViewer）を探すため、メインウィンドウの
/// ドックパネルだけでなくフローティングパネルや設定ウィンドウでも機能する。
/// 感度係数は <see cref="EditorPreferences.TouchpadScrollScale"/>（環境設定）で調整できる。
/// </summary>
public static class EditorScrollBehavior
{
    // ── 定数 ─────────────────────────────────────────────────

    /// <summary>横ホイール（タッチパッドの横スワイプ等）の Windows メッセージ。</summary>
    private const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>ホイール 1 ノッチの delta 値（Windows 標準）。</summary>
    private const int WheelDeltaPerNotch = 120;

    /// <summary>
    /// 手動スクロール時の 1 ノッチあたりの移動量（px）。縦・横で共通の基準とし、
    /// 実際の感度はこれに環境設定の係数を掛けて決まる。
    /// </summary>
    private const double ScrollPixelsPerNotch = 48.0;

    /// <summary>環境設定の係数を適用した 1 ノッチあたりの移動量（px）。</summary>
    private static double ScaledPixelsPerNotch
        => ScrollPixelsPerNotch * EditorPreferences.Instance.TouchpadScrollScale;

    /// <summary>
    /// 祖先探索の最大段数。ビジュアル＋論理の混在ツリーを遡るため、
    /// 万一循環参照（親が子を指すような不正なツリー）に入っても
    /// 無限ループでハングしないよう上限を設ける。実際の WPF ツリー深度は
    /// 深いテンプレートでも 100 段に届かない。
    /// </summary>
    private const int MaxAncestorDepth = 512;

    private static bool _installed;

    // ── 導入 ─────────────────────────────────────────────────

    /// <summary>
    /// スクロール挙動をアプリ全体へ導入する（起動時に一度呼ぶ）。
    /// 全 Window のクラスハンドラと UI スレッドのメッセージフィルタを登録するため、
    /// 後から生成されるウィンドウ（フローティングパネル等）にも自動で適用される。
    /// </summary>
    public static void Install()
    {
        if (_installed) return;
        _installed = true;

        // 縦: すべての Window の PreviewMouseWheel をクラスハンドラで受ける
        EventManager.RegisterClassHandler(
            typeof(Window), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));

        // 横: UI スレッドの全メッセージから WM_MOUSEHWHEEL を拾う
        //（HwndSource 個別フックと違い、全ウィンドウを 1 箇所でカバーできる）
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
    }

    // ── 縦スクロール（タッチパッド減衰・感度統一）────────────────

    /// <summary>
    /// 全ウィンドウ共通の PreviewMouseWheel: タッチパッドの精密スクロールだけを
    /// 手動スクロール（横方向と同じ感度・環境設定の係数適用）へ置き換える。
    /// </summary>
    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 120 の倍数 = 物理ホイール（または非精密ドライバ）→ 既定処理に任せる
        if (e.Delta % WheelDeltaPerNotch == 0) return;

        // クラスハンドラ内で例外を投げると WPF の未処理例外となりエディタが即死する。
        // スクロールは「できなければ既定処理へ委ねる」で足りる副次機能なので、
        // ここで確実に握り潰し、原因追跡のためログだけ残す。
        try
        {
            if (ApplyVerticalScroll(e.Delta)) e.Handled = true;
        }
        catch (Exception ex)
        {
            EditorLog.Write($"EditorScrollBehavior.OnPreviewMouseWheel — 例外を無視: {ex}");
        }
    }

    /// <summary>
    /// マウス直下の要素から祖先（ビジュアル／論理の混在ツリー）を遡り、最初に見つかったスクロール可能要素へ
    /// delta（生値）による縦スクロールを適用する。処理できた場合 true。
    /// </summary>
    private static bool ApplyVerticalScroll(double delta)
    {
        var el = Mouse.DirectlyOver as DependencyObject;
        for (int depth = 0; el is not null && depth < MaxAncestorDepth; depth++)
        {
            // AvalonEdit のエディタ: ピクセル単位の縦オフセット API を使う
            if (el is ICSharpCode.AvalonEdit.TextEditor editor)
            {
                double px = delta / WheelDeltaPerNotch * ScaledPixelsPerNotch;
                editor.ScrollToVerticalOffset(Math.Max(0, editor.VerticalOffset - px));
                return true;
            }
            if (el is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                // オフセットの単位を判定する:
                //  - AvalonEdit 内部の ScrollViewer は CanContentScroll=true だが
                //    IScrollInfo（TextView）のオフセット単位は「ピクセル」なので px 換算を使う
                //  - それ以外の論理スクロール（ListBox 等のアイテム単位）は行数換算を使う
                bool pixelBased = !sv.CanContentScroll
                    || sv.Content is ICSharpCode.AvalonEdit.Editing.TextArea
                    || sv.Content is ICSharpCode.AvalonEdit.Rendering.TextView;
                double amount = pixelBased
                    ? delta / WheelDeltaPerNotch * ScaledPixelsPerNotch
                    : delta / WheelDeltaPerNotch * SystemParameters.WheelScrollLines
                          * EditorPreferences.Instance.TouchpadScrollScale;
                sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset - amount, 0, sv.ScrollableHeight));
                return true;
            }
            el = GetAncestor(el);
        }
        return false;
    }

    // ── 祖先探索（ビジュアル／論理の混在ツリー対応）──────────────

    /// <summary>
    /// 祖先を 1 段たどる。ビジュアル要素はビジュアルツリー、
    /// コンテンツ要素（Run / Hyperlink / Paragraph 等）は論理ツリーを使う。
    ///
    /// 【なぜ VisualTreeHelper.GetParent だけでは駄目か】
    /// <see cref="Mouse.DirectlyOver"/> は最内の <c>IInputElement</c> を返すが、
    /// これは UIElement とは限らない。TextBlock の Inlines（インスペクタの
    /// コンポーネント見出しなど）や RichTextBox の本文の上にカーソルがあると
    /// <see cref="Run"/> のような <see cref="ContentElement"/> が返る。
    /// ContentElement は Visual ではないため <see cref="VisualTreeHelper.GetParent"/> は
    /// InvalidOperationException を投げ、クラスハンドラ内の未処理例外として
    /// エディタを落としていた（タッチパッド経路だけがこの探索を通るため
    /// 物理マウスでは再現しなかった）。
    /// </summary>
    /// <returns>1 段上の要素。これ以上遡れない場合は null。</returns>
    private static DependencyObject? GetAncestor(DependencyObject el)
    {
        // ビジュアル要素: ビジュアルツリー（テンプレート内部まで遡れる）
        if (el is Visual or Visual3D) return VisualTreeHelper.GetParent(el);

        // コンテンツ要素: 論理親をたどる。Run → TextBlock、
        // Run → Paragraph → FlowDocument → RichTextBox のように
        // いずれ Visual へ合流するので、そこから先はビジュアルツリーに戻る。
        if (el is FrameworkContentElement fce) return fce.Parent;
        if (el is ContentElement ce) return ContentOperations.GetParent(ce);

        // Visual でも ContentElement でもない DependencyObject（Freezable 等）は
        // 入力ツリーには現れないが、念のため論理親を試す。
        return LogicalTreeHelper.GetParent(el);
    }

    // ── 横スクロール（WM_MOUSEHWHEEL）────────────────────────────

    /// <summary>
    /// UI スレッドのメッセージフィルタ: WM_MOUSEHWHEEL（横ホイール）を検出し、
    /// マウス直下のスクロール可能要素へ水平スクロールを配送する。
    /// </summary>
    private static void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message != WM_MOUSEHWHEEL || handled) return;

        // wParam の上位 16bit が符号付き delta（正 = 右方向。1 ノッチ = 120）
        short delta = unchecked((short)(((long)msg.wParam >> 16) & 0xFFFF));
        if (delta == 0) return;

        // メッセージフィルタはディスパッチャループ直下で呼ばれるため、
        // ここで例外を投げるとそのままアプリが落ちる。縦と同様に必ず握り潰す。
        try
        {
            ApplyHorizontalScroll((double)delta / WheelDeltaPerNotch * ScaledPixelsPerNotch,
                                  ref handled);
        }
        catch (Exception ex)
        {
            EditorLog.Write($"EditorScrollBehavior.OnThreadPreprocessMessage — 例外を無視: {ex}");
        }
    }

    /// <summary>
    /// マウス直下の要素から祖先をたどり、最初に見つかったスクロール可能要素へ
    /// 水平スクロールを適用する。適用できたら <paramref name="handled"/> を立てる。
    /// </summary>
    private static void ApplyHorizontalScroll(double amount, ref bool handled)
    {
        var el = Mouse.DirectlyOver as DependencyObject;
        for (int depth = 0; el is not null && depth < MaxAncestorDepth; depth++)
        {
            // AvalonEdit のエディタ: 専用の水平オフセット API を使う
            if (el is ICSharpCode.AvalonEdit.TextEditor editor)
            {
                editor.ScrollToHorizontalOffset(Math.Max(0, editor.HorizontalOffset + amount));
                handled = true;
                return;
            }
            // 一般の ScrollViewer（出力パネル・ヒエラルキー等）
            if (el is ScrollViewer sv && sv.ScrollableWidth > 0)
            {
                sv.ScrollToHorizontalOffset(Math.Clamp(sv.HorizontalOffset + amount, 0, sv.ScrollableWidth));
                handled = true;
                return;
            }
            el = GetAncestor(el);
        }
    }
}
