namespace SEED;

/// <summary>
/// 指 1 本のこのフレームでの段階（<see cref="Touch.Phase"/>）。並びは Unity の TouchPhase と同じ。
///
/// 【重要】数値は Rust 側 runtime/src/engine/core/input/touch/phase.rs の TouchPhase と
/// 必ず一致させること（FFI では数値で受け渡す。ずれると段階が入れ替わって見える）。
/// </summary>
public enum TouchPhase
{
    /// <summary>このフレームで触れ始めた（触れ始めたフレームだけ）。</summary>
    Began = 0,
    /// <summary>触れたまま、前フレームから位置が変わった。</summary>
    Moved = 1,
    /// <summary>触れたまま、前フレームから位置が変わっていない。</summary>
    Stationary = 2,
    /// <summary>このフレームで離れた（そのフレームだけ一覧に残り、次フレームで消える）。</summary>
    Ended = 3,
    /// <summary>このフレームで OS に取り消された（着信・フォーカス喪失など）。Ended と同じく 1 フレームだけ残る。</summary>
    Canceled = 4,
}
