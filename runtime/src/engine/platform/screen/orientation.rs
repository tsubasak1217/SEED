// ============================================================
//  platform/screen/orientation.rs — 画面の向き（ScreenOrientation）の判定
//
//  【役割】
//  スクリプトの `SEED.Screen.Orientation` が返す「画面の向き」を、OS から得た情報
//  （Android の表示の回転 Display.getRotation と、そのときの画面の縦横）か、
//  回転を知らない環境（デスクトップ）では画面の縦横比から決める純関数を置く。
//
//  【向きの定義（Unity の ScreenOrientation と同じ意味）】
//    Portrait            … 縦長・正立（端末の上端が上）
//    PortraitUpsideDown  … 縦長・逆さ
//    LandscapeLeft       … 横長。縦持ちから反時計回りに 90 度倒した向き（端末の上端が左）
//    LandscapeRight      … 横長。縦持ちから時計回りに 90 度倒した向き（端末の上端が右）
//
//  【Android の回転との対応】
//  Display.getRotation は「自然な向き（端末の既定の向き）からの、描画の回転」を 90 度単位で返す。
//  端末を反時計回りに 90 度倒すと描画は時計回りに 90 度回されて ROTATION_90 になる（Android の説明どおり）。
//  端末を反時計回りに倒していく順に並べると、縦持ちが自然な端末（スマートフォン）では
//    0 → Portrait / 1 → LandscapeLeft / 2 → PortraitUpsideDown / 3 → LandscapeRight
//  となる（ROTATION_CYCLE）。横持ちが自然な端末（多くのタブレット）は、自然な向きを LandscapeLeft と
//  みなして同じ並びを 1 つずらす。自然な向きは表示の物理的な縦横（Display.Mode の physicalWidth / Height。
//  回転 0 のときの大きさ）で決める（ウィンドウの大きさは分割画面などで表示と縦横が食い違うため使わない）。
//
//  数値（C# 側 scripting/src/Api/ScreenOrientation.cs と一致させる）は FFI でそのまま渡す。
// ============================================================

/// 画面の向き（数値は C# 側 `SEED.ScreenOrientation` と必ず一致させる）。
#[repr(i32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ScreenOrientation {
    /// 縦長・正立。
    Portrait = 0,
    /// 縦長・逆さ（Portrait を 180 度回した向き）。
    PortraitUpsideDown = 1,
    /// 横長。縦持ちから反時計回りに 90 度倒した向き（端末の上端が左）。
    LandscapeLeft = 2,
    /// 横長。縦持ちから時計回りに 90 度倒した向き（端末の上端が右）。
    LandscapeRight = 3,
}

/// 1 周の回転を構成する 90 度単位の段数（Display.getRotation の値域 0..=3）。
pub const QUARTER_TURNS_PER_REVOLUTION: u32 = 4;

/// 縦持ちが自然な端末で、端末を反時計回りに 90 度ずつ倒していったときの向きの並び
/// （添字 = Display.getRotation の値）。
const ROTATION_CYCLE: [ScreenOrientation; QUARTER_TURNS_PER_REVOLUTION as usize] = [
    ScreenOrientation::Portrait,
    ScreenOrientation::LandscapeLeft,
    ScreenOrientation::PortraitUpsideDown,
    ScreenOrientation::LandscapeRight,
];

/// 横持ちが自然な端末の、ROTATION_CYCLE 上での自然な向き（LandscapeLeft）の位置。
/// 自然な向きが横長の端末は、縦持ちの端末を反時計回りに 90 度倒した状態を既定とみなす。
const LANDSCAPE_NATURAL_CYCLE_OFFSET: u32 = 1;

impl ScreenOrientation {
    /// FFI・ログで使う数値（C# 側の列挙値と同じ）。
    pub fn id(self) -> i32 {
        self as i32
    }

    /// ログ用の名前（C# 側の列挙子名と同じ）。
    pub fn label(self) -> &'static str {
        match self {
            Self::Portrait => "Portrait",
            Self::PortraitUpsideDown => "PortraitUpsideDown",
            Self::LandscapeLeft => "LandscapeLeft",
            Self::LandscapeRight => "LandscapeRight",
        }
    }

    /// 縦長の向きか（Portrait / PortraitUpsideDown）。
    pub fn is_portrait(self) -> bool {
        matches!(self, Self::Portrait | Self::PortraitUpsideDown)
    }
}

/// 画面の縦横比だけから向きを決める【純関数】（回転の情報を持たない環境の規則）。
///
/// 高さが幅より大きければ Portrait、それ以外（正方形を含む）は LandscapeLeft。
/// 逆さ（PortraitUpsideDown / LandscapeRight）は縦横比からは区別できないので返さない。
///
/// # 引数
/// * `width` / `height` - 画面（ウィンドウ）の大きさ（ピクセル。単位は縦横で揃っていればよい）
pub fn orientation_from_aspect(width: u32, height: u32) -> ScreenOrientation {
    if height > width {
        ScreenOrientation::Portrait
    } else {
        ScreenOrientation::LandscapeLeft
    }
}

/// 表示の回転（Display.getRotation）と、表示の自然な向きの縦横から向きを決める【純関数】。
///
/// # 引数
/// * `quarter_turns` - 自然な向きからの描画の回転（90 度単位。0..=3。4 以上は 4 で割った余り）
/// * `natural_width` / `natural_height` - 表示の自然な向き（回転 0）のときの大きさ
///   （Android の Display.Mode の physicalWidth / physicalHeight）
///
/// 自然な向きが縦長（正方形を含む）なら ROTATION_CYCLE をそのまま、横長なら 1 つずらして引く。
pub fn orientation_from_rotation(
    quarter_turns: u32,
    natural_width: u32,
    natural_height: u32,
) -> ScreenOrientation {
    let turns = quarter_turns % QUARTER_TURNS_PER_REVOLUTION;
    let natural_is_portrait = natural_height >= natural_width;
    let offset = if natural_is_portrait { 0 } else { LANDSCAPE_NATURAL_CYCLE_OFFSET };
    ROTATION_CYCLE[((turns + offset) % QUARTER_TURNS_PER_REVOLUTION) as usize]
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// スマートフォン（自然な向き＝縦 1080x2400）の 4 方向。
    /// 自然な向きの大きさは回転しても同じ値（表示の物理的な縦横）で届く。
    #[test]
    fn natural_portrait_device_maps_each_rotation() {
        assert_eq!(orientation_from_rotation(0, 1080, 2400), ScreenOrientation::Portrait);
        assert_eq!(orientation_from_rotation(1, 1080, 2400), ScreenOrientation::LandscapeLeft);
        assert_eq!(orientation_from_rotation(2, 1080, 2400), ScreenOrientation::PortraitUpsideDown);
        assert_eq!(orientation_from_rotation(3, 1080, 2400), ScreenOrientation::LandscapeRight);
    }

    /// タブレット（自然な向き＝横 2560x1600）は自然な向きを LandscapeLeft として同じ並びをずらす。
    #[test]
    fn natural_landscape_device_is_offset_by_one_step() {
        assert_eq!(orientation_from_rotation(0, 2560, 1600), ScreenOrientation::LandscapeLeft);
        assert_eq!(orientation_from_rotation(1, 2560, 1600), ScreenOrientation::PortraitUpsideDown);
        assert_eq!(orientation_from_rotation(2, 2560, 1600), ScreenOrientation::LandscapeRight);
        assert_eq!(orientation_from_rotation(3, 2560, 1600), ScreenOrientation::Portrait);
    }

    /// 回転の値域外（4 以上）は 1 周分を落として扱う（防御的な丸め。panic しない）。
    /// 自然な向きの大きさが分からない（0x0）ときは縦持ちの端末として扱う。
    #[test]
    fn rotation_wraps_around_full_revolution() {
        assert_eq!(orientation_from_rotation(4, 1080, 2400), ScreenOrientation::Portrait);
        assert_eq!(orientation_from_rotation(5, 1080, 2400), ScreenOrientation::LandscapeLeft);
        assert_eq!(orientation_from_rotation(1, 0, 0), ScreenOrientation::LandscapeLeft);
    }

    /// 縦横比の規則: 縦長は Portrait、横長と正方形は LandscapeLeft（デスクトップの規則）。
    #[test]
    fn aspect_rule_distinguishes_only_portrait_and_landscape_left() {
        assert_eq!(orientation_from_aspect(540, 960), ScreenOrientation::Portrait);
        assert_eq!(orientation_from_aspect(1920, 1080), ScreenOrientation::LandscapeLeft);
        assert_eq!(orientation_from_aspect(800, 800), ScreenOrientation::LandscapeLeft);
        // 最小化などで 0 が来ても panic しない（横長扱い）。
        assert_eq!(orientation_from_aspect(0, 0), ScreenOrientation::LandscapeLeft);
    }

    /// 数値と名前は C# の SEED.ScreenOrientation と同じ（ずれると別の向きとして読まれる）。
    #[test]
    fn ids_and_labels_match_csharp_enum() {
        assert_eq!(ScreenOrientation::Portrait.id(), 0);
        assert_eq!(ScreenOrientation::PortraitUpsideDown.id(), 1);
        assert_eq!(ScreenOrientation::LandscapeLeft.id(), 2);
        assert_eq!(ScreenOrientation::LandscapeRight.id(), 3);
        assert_eq!(ScreenOrientation::LandscapeRight.label(), "LandscapeRight");
        assert!(ScreenOrientation::PortraitUpsideDown.is_portrait());
        assert!(!ScreenOrientation::LandscapeLeft.is_portrait());
    }
}
