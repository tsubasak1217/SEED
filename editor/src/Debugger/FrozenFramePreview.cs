using System;
using System.Runtime.InteropServices;

namespace SEEDEditor.Debugger;

/// <summary>
/// ブレークポイント停止中の Play ランタイムウィンドウ（メインスレッド凍結状態）の
/// 「最後に描画されたフレーム」を、エディタの Viewport 上に静止画として表示する。
///
/// 【なぜ DWM サムネイルか】
/// 停止中はランタイムのメインスレッド（＝メッセージポンプ＋描画）が丸ごと凍結するため、
/// SetParent などの同期 Win32 でウィンドウを埋め込もうとすると呼び出し側スレッドごと
/// デッドロックする。また GPU（wgpu/DX12）ウィンドウは BitBlt では中身を取得できない
/// （黒画像になる）。
/// DWM サムネイル（DwmRegisterThumbnail）は、コンポジタが保持している「最後に present
/// された実サーフェス」を指定矩形へ合成するだけなので、
///   - ソースウィンドウのスレッド応答を必要とせず（＝デッドロックしない）、
///   - GPU ウィンドウの内容も正しく取得できる。
/// 停止中はソースが更新されないため、結果として「停止した瞬間のフレーム」が静止表示される。
///
/// 表示のみ（入力・見回しは不可）。継続すると解除する。
/// </summary>
public sealed class FrozenFramePreview : IDisposable
{
    // ── DWM サムネイル P/Invoke ─────────────────────────────────
    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(nint dest, nint src, out nint thumbId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(nint thumbId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint hThumbId, ref DwmThumbnailProperties props);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(nint hThumbId, out Size size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size { public int Cx, Cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public int  DwFlags;
        public Rect RcDestination;
        public Rect RcSource;
        public byte Opacity;
        public bool FVisible;
        public bool FSourceClientAreaOnly;
    }

    // DWM_TNP_* フラグ
    private const int DwmTnpRectDestination      = 0x00000001;
    private const int DwmTnpOpacity              = 0x00000004;
    private const int DwmTnpVisible              = 0x00000008;
    private const int DwmTnpSourceClientAreaOnly = 0x00000010;
    private const byte OpaqueAlpha               = 255;

    // ── 状態 ────────────────────────────────────────────────────
    private nint _thumb;

    /// <summary>現在プレビュー表示中か。</summary>
    public bool IsShowing => _thumb != nint.Zero;

    /// <summary>
    /// プレビューを表示（または再構成）する。
    /// </summary>
    /// <param name="destWindow">合成先のトップレベルウィンドウ（エディタのメインウィンドウ HWND）。</param>
    /// <param name="sourceWindow">ソース（凍結中の Play ランタイムウィンドウ HWND）。</param>
    /// <param name="left">合成先クライアント座標での表示矩形（物理ピクセル）。</param>
    public void Show(nint destWindow, nint sourceWindow, int left, int top, int right, int bottom)
    {
        Hide();
        if (destWindow == nint.Zero || sourceWindow == nint.Zero) return;

        if (DwmRegisterThumbnail(destWindow, sourceWindow, out var thumb) != 0 || thumb == nint.Zero)
            return;

        _thumb = thumb;
        UpdateRect(left, top, right, bottom);
    }

    /// <summary>表示矩形を更新する（Viewport リサイズ時などに呼ぶ）。</summary>
    public void UpdateRect(int left, int top, int right, int bottom)
    {
        if (_thumb == nint.Zero) return;

        // ソースのアスペクト比を維持してレターボックス配置する（歪み防止）。
        var dst = new Rect { Left = left, Top = top, Right = right, Bottom = bottom };
        if (DwmQueryThumbnailSourceSize(_thumb, out var src) == 0 && src.Cx > 0 && src.Cy > 0)
            dst = Letterbox(left, top, right, bottom, src.Cx, src.Cy);

        var props = new DwmThumbnailProperties
        {
            DwFlags               = DwmTnpRectDestination | DwmTnpVisible | DwmTnpOpacity | DwmTnpSourceClientAreaOnly,
            RcDestination         = dst,
            Opacity               = OpaqueAlpha,
            FVisible              = true,
            FSourceClientAreaOnly = true,
        };
        DwmUpdateThumbnailProperties(_thumb, ref props);
    }

    /// <summary>プレビューを消す。</summary>
    public void Hide()
    {
        if (_thumb == nint.Zero) return;
        DwmUnregisterThumbnail(_thumb);
        _thumb = nint.Zero;
    }

    public void Dispose() => Hide();

    /// <summary>
    /// 表示矩形 (left,top,right,bottom) の中に、幅 srcW × 高 srcH のソースを
    /// アスペクト比維持で内接（レターボックス）させた矩形を返す。
    /// </summary>
    private static Rect Letterbox(int left, int top, int right, int bottom, int srcW, int srcH)
    {
        int boxW = Math.Max(0, right - left);
        int boxH = Math.Max(0, bottom - top);
        if (boxW == 0 || boxH == 0) return new Rect { Left = left, Top = top, Right = right, Bottom = bottom };

        // ソース比に合わせてフィットさせるスケール（min でボックス内に収める）
        double scale = Math.Min((double)boxW / srcW, (double)boxH / srcH);
        int w = (int)Math.Round(srcW * scale);
        int h = (int)Math.Round(srcH * scale);
        int offX = left + (boxW - w) / 2;
        int offY = top  + (boxH - h) / 2;
        return new Rect { Left = offX, Top = offY, Right = offX + w, Bottom = offY + h };
    }
}
