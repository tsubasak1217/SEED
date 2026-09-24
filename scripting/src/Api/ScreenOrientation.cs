namespace SEED;

/// <summary>
/// 画面の向き（<see cref="Screen.Orientation"/>）。意味は Unity の ScreenOrientation と同じ。
///
/// 【重要】数値は Rust 側 runtime/src/engine/platform/screen/orientation.rs の ScreenOrientation と
/// 必ず一致させること（FFI では数値で受け渡す。ずれると別の向きとして読まれる）。
/// </summary>
public enum ScreenOrientation
{
    /// <summary>縦長・正立（端末の上端が上）。</summary>
    Portrait = 0,
    /// <summary>縦長・逆さ（Portrait を 180 度回した向き）。</summary>
    PortraitUpsideDown = 1,
    /// <summary>横長。縦持ちから反時計回りに 90 度倒した向き（端末の上端が左）。デスクトップの横長ウィンドウもこれ。</summary>
    LandscapeLeft = 2,
    /// <summary>横長。縦持ちから時計回りに 90 度倒した向き（端末の上端が右）。</summary>
    LandscapeRight = 3,
}
