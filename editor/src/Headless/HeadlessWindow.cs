using System.Windows;

namespace SEEDEditor.Headless;

/// <summary>
/// ヘッドレス起動時に MainWindow を「画面に出さないまま生かす」ための設定。
///
/// <para><b>なぜ Visibility.Hidden にしないのか</b></para>
/// <para>
/// ビューポートには wgpu(DX12) のランタイムウィンドウを <c>SetParent</c> で子として
/// 埋め込んでいる。WPF ウィンドウを <c>Hidden</c> にすると HWND のクライアント領域が
/// 事実上ゼロ扱いになり、埋め込み先コンテナのサイズが 0×0 になる。ランタイム側は
/// 0×0 のフレームを描けず（最小化ガードで即 return する）スクリーンショットも撮れない。
/// </para>
/// <para>
/// そこで「通常サイズのまま仮想デスクトップの外へ置く」方式にする。HWND は実在し、
/// レイアウトも通常どおり走るのでビューポートは正しいサイズになる。DXGI の Present は
/// 画面外ウィンドウでも成立し、GPU からの読み戻し（SCREENSHOT: IPC）は表示状態に
/// 一切依存しない。タスクバーにも出さず、フォーカスも奪わない。
/// </para>
///
/// <para><b>フレーム駆動について</b></para>
/// <para>
/// 画面外ウィンドウには OS が WM_PAINT を配送しないため、ランタイムの
/// RedrawRequested によるフレームループは止まる。ランタイム側は環境変数
/// <c>SEED_HEADLESS=1</c> を受け取ると、イベントループ（about_to_wait）から
/// 自前でフレームを回すため、非表示でも描画・シミュレーションが進む。
/// 環境変数は <c>RuntimeManager</c> が子プロセス起動時に引き渡す。
/// </para>
/// </summary>
internal static class HeadlessWindow
{
    /// <summary>
    /// 画面外配置の X 座標。仮想デスクトップ左端よりさらに外側に置く
    /// （Windows が最小化ウィンドウを退避させる座標と同じ値で、実績のある領域）。
    /// </summary>
    private const double OFFSCREEN_LEFT = -32000;
    /// <summary>画面外配置の Y 座標。</summary>
    private const double OFFSCREEN_TOP = -32000;
    /// <summary>ヘッドレス時のウィンドウ幅 [DIP]。ビューポートが実用的な解像度になる値。</summary>
    private const double HEADLESS_WIDTH = 1920;
    /// <summary>ヘッドレス時のウィンドウ高さ [DIP]。</summary>
    private const double HEADLESS_HEIGHT = 1080;

    /// <summary>
    /// ウィンドウをヘッドレス構成へ切り替える。<b>Show() より前</b>に呼ぶこと
    /// （<c>ShowActivated</c> は表示後に変更しても効かないため、コンストラクタから呼ぶ）。
    /// </summary>
    public static void Apply(Window window)
    {
        // XAML は WindowState="Maximized" 指定。そのままだとプライマリモニタへ
        // 吸着して画面に出てしまうので、必ず Normal + 明示サイズへ戻す。
        window.WindowState   = WindowState.Normal;
        window.WindowStyle   = WindowStyle.None;
        window.ResizeMode    = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Topmost       = false;
        window.Width         = HEADLESS_WIDTH;
        window.Height        = HEADLESS_HEIGHT;
        window.Left          = OFFSCREEN_LEFT;
        window.Top           = OFFSCREEN_TOP;
    }

    /// <summary>
    /// 位置・サイズを画面外構成へ再適用する。
    /// レイアウト復元やドッキング処理がウィンドウを動かし得るため、
    /// Loaded 後にもう一度呼んで「画面に出てしまう」事故を防ぐ。
    /// </summary>
    public static void Reapply(Window window)
    {
        if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
        window.Width  = HEADLESS_WIDTH;
        window.Height = HEADLESS_HEIGHT;
        window.Left   = OFFSCREEN_LEFT;
        window.Top    = OFFSCREEN_TOP;
    }
}
