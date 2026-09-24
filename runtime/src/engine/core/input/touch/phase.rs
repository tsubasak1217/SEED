// ============================================================
//  touch/phase.rs — 指 1 本の「このフレームでの段階」（Unity の TouchPhase 相当）
//
//  【役割】
//  スクリプト（C# の SEED.TouchPhase）と診断ログへ見せる段階の列挙。
//  値は FFI で数値として受け渡すため、判別値（= C# 側の数値）をここで固定する。
//
//  段階の決まり方（state.rs が計算する）:
//    Began      … 触れ始めたフレームだけ
//    Moved      … 触れたまま、前フレーム末から位置が変わった
//    Stationary … 触れたまま、前フレーム末から位置が変わっていない
//    Ended      … 離れたフレームだけ（そのフレームは一覧に残り、次フレームで消える）
//    Canceled   … OS に取り消されたフレームだけ（Ended と同じく 1 フレームだけ残る）
// ============================================================

/// 指 1 本のこのフレームでの段階。
///
/// 【重要】判別値は C# 側 `scripting/src/Api/TouchPhase.cs` の `SEED.TouchPhase` と
/// 必ず一致させること（FFI では `id()` の数値で受け渡す。ずれると段階が入れ替わって見える）。
/// 並びは Unity の `TouchPhase` と同じ（Began=0 … Canceled=4）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum TouchPhase {
    /// このフレームで触れ始めた。
    Began = 0,
    /// 触れたまま、前フレーム末から位置が変わった。
    Moved = 1,
    /// 触れたまま、前フレーム末から位置が変わっていない。
    Stationary = 2,
    /// このフレームで離れた。
    Ended = 3,
    /// このフレームで OS に取り消された（着信・フォーカス喪失・システムジェスチャへの横取りなど）。
    Canceled = 4,
}

impl TouchPhase {
    /// FFI・ログで使う数値（C# の `(int)SEED.TouchPhase`）。
    #[inline]
    pub fn id(self) -> i32 {
        self as i32
    }

    /// 指が画面から離れた段階（Ended / Canceled）か。
    #[inline]
    pub fn is_finished(self) -> bool {
        matches!(self, TouchPhase::Ended | TouchPhase::Canceled)
    }

    /// 診断ログ用の短い名前。
    pub fn label(self) -> &'static str {
        match self {
            TouchPhase::Began => "Began",
            TouchPhase::Moved => "Moved",
            TouchPhase::Stationary => "Stationary",
            TouchPhase::Ended => "Ended",
            TouchPhase::Canceled => "Canceled",
        }
    }
}

// ============================================================
//  テスト（C# 側との数値の契約）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 数値は C# の SEED.TouchPhase（= Unity の並び）と同じであること。
    /// ここが変わると C# 側の enum と食い違うので、変えるなら両方を同時に直す。
    #[test]
    fn ids_match_csharp_contract() {
        assert_eq!(TouchPhase::Began.id(), 0);
        assert_eq!(TouchPhase::Moved.id(), 1);
        assert_eq!(TouchPhase::Stationary.id(), 2);
        assert_eq!(TouchPhase::Ended.id(), 3);
        assert_eq!(TouchPhase::Canceled.id(), 4);
    }

    /// 離れた段階の判定。
    #[test]
    fn finished_phases() {
        assert!(!TouchPhase::Began.is_finished());
        assert!(!TouchPhase::Moved.is_finished());
        assert!(!TouchPhase::Stationary.is_finished());
        assert!(TouchPhase::Ended.is_finished());
        assert!(TouchPhase::Canceled.is_finished());
    }
}
