// ============================================================
//  primitive3d/queue.rs — スクリプトが積む 3D プリミティブ描画コマンドキュー
//
//  【役割】
//  C# の `SEED.Draw3D.*`（イミディエイトモード API）が FFI 経由で積んだ
//  「このフレームに描くワールド空間の図形」を 1 フレームぶん貯める。
//  貯めたコマンドはフレーム描画時に `take_commands()` で丸ごと引き取られ、
//  キューは空になる（＝毎フレーム自動クリア。前フレームの図形は残らない）。
//
//  【2D 版（primitive2d/queue.rs）との違い】
//  - 座標は**ワールド空間の Vector3**。キャンバス／スクリーン座標の概念は無い。
//  - レイヤーの概念が無い（前後関係は深度テストとコマンド順で決まる）。
//  - 太さは**画面ピクセル**で指定し、頂点シェーダーが距離に依らず一定幅の
//    リボンへ広げる（`primitive3d.wgsl`）。CPU 側は太さを持ったまま渡すだけ。
//  - 図形ごとに深度テストの有無を選べる（`depth_test`）。
//
//  【スレッド】
//  スクリプトは CLR メインスレッド専用（scripting/mod.rs）で、描画も同じ
//  メインスレッドで行うため thread_local で足りる（2D 版と同じ方針）。
// ============================================================

use std::cell::{Cell, RefCell};

// ─── 上限・データ表現の定数 ──────────────────────────────────

/// 1 フレームに積める 3D プリミティブの上限。
/// これを超えた分は捨てて 1 フレーム 1 回だけ警告ログを出す
/// （無限に積まれてメモリと CPU を食い潰すのを防ぐ安全弁）。
pub const MAX_PRIMITIVES3D_PER_FRAME: usize = 4096;

/// 1 プリミティブが持てる点の上限（Polyline の頂点数）。
/// 超過分は切り捨てる（スクリプト側の暴走を描画側で吸収する）。
pub const MAX_POINTS_PER_PRIMITIVE3D: usize = 1024;

/// FFI パラメータ配列の共通ヘッダ長（float 個数）。
/// 内訳: color RGBA(4) + mode(1) + thickness_px(1) + depth_test(1) = 7
pub const PRIM3D_HEADER_FLOATS: usize = 7;

/// 共通ヘッダに続く「図形ごとの追加スカラ」の個数（固定長）。
/// 最も多いのは WireBox（サイズ XYZ + 回転オイラー XYZ = 6）。
pub const PRIM3D_EXTRA_FLOATS: usize = 6;

/// FFI パラメータ配列の総 float 個数（C# 側 `Draw3D.cs` と完全一致必須）。
pub const PRIM3D_PARAM_FLOATS: usize = PRIM3D_HEADER_FLOATS + PRIM3D_EXTRA_FLOATS;

// ─── 図形種別・描画モード ────────────────────────────────────

/// 3D プリミティブの図形種別。値は C# 側 `Draw3D.cs` の kind 定数と一致必須。
///
/// `points` / `extras` の意味は種別ごとに異なる（下記コメントが正典）。
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Primitive3dKind {
    /// 折れ線（Line もこれに集約される）。points = 頂点列 / extras[0] = 閉じるか（0/1）。
    Polyline = 0,
    /// 平面多角形（Triangle / Quad）。points = 頂点列（3 点以上）。
    /// Fill は扇状に三角形分割（両面・アンリット）、Outline は閉じた折れ線。
    Polygon = 1,
    /// 円。points[0] = 中心 / points[1] = 法線 /
    /// extras[0] = 半径 / extras[1] = 分割数。
    Circle = 2,
    /// リング（円環バンド。常に塗り）。points[0] = 中心 / points[1] = 法線 /
    /// extras[0] = 内半径 / extras[1] = 外半径 / extras[2] = 開始角（度）/
    /// extras[3] = 終了角（度）/ extras[4] = 分割数。
    Ring = 3,
    /// 円弧（線のみ）。points[0] = 中心 / points[1] = 法線 /
    /// extras[0] = 半径 / extras[1] = 開始角（度）/ extras[2] = 終了角（度）/
    /// extras[3] = 分割数。
    Arc = 4,
    /// ワイヤ球（3 つの大円）。points[0] = 中心 /
    /// extras[0] = 半径 / extras[1] = 分割数。
    WireSphere = 5,
    /// ワイヤ直方体（12 辺）。points[0] = 中心 /
    /// extras[0..3] = サイズ XYZ / extras[3..6] = 回転オイラー角 XYZ（度・YXZ 規約）。
    WireBox = 6,
    /// ワイヤカプセル。points[0] = 一方の球中心 / points[1] = もう一方の球中心 /
    /// extras[0] = 半径 / extras[1] = 分割数。
    WireCapsule = 7,
    /// 矢印（軸は線・矢尻は塗りの円錐）。points[0] = 始点 / points[1] = 終点 /
    /// extras[0] = 矢尻の長さ / extras[1] = 矢尻の半径 / extras[2] = 分割数。
    Arrow = 8,
    /// 点（常に画面を向く正方形）。points[0] = 位置 / extras[0] = 一辺の px。
    Point = 9,
}

impl Primitive3dKind {
    /// FFI で渡された整数値から図形種別へ変換する。未知の値は None（コマンドを捨てる）。
    pub fn from_i32(v: i32) -> Option<Self> {
        match v {
            0 => Some(Self::Polyline),
            1 => Some(Self::Polygon),
            2 => Some(Self::Circle),
            3 => Some(Self::Ring),
            4 => Some(Self::Arc),
            5 => Some(Self::WireSphere),
            6 => Some(Self::WireBox),
            7 => Some(Self::WireCapsule),
            8 => Some(Self::Arrow),
            9 => Some(Self::Point),
            _ => None,
        }
    }
}

/// 塗りつぶし／輪郭線の描画モード。値は C# 側 `DrawMode`（2D と共用）と一致必須。
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum Primitive3dDrawMode {
    /// 内側を塗りつぶす。
    Fill = 0,
    /// 輪郭を太さ `thickness_px` の線で描く。
    Outline = 1,
}

impl Primitive3dDrawMode {
    /// FFI の float 値（0.0/1.0）から変換する。範囲外は Fill 扱い。
    pub fn from_f32(v: f32) -> Self {
        if v >= 0.5 {
            Self::Outline
        } else {
            Self::Fill
        }
    }
}

// ─── コマンド ────────────────────────────────────────────────

/// スクリプトが積んだ 1 図形ぶんの描画コマンド（座標は常にワールド空間）。
#[derive(Clone, Debug)]
pub struct Primitive3dCommand {
    /// 図形種別。
    pub kind: Primitive3dKind,
    /// RGBA カラー（0..1・ストレートアルファ）。
    pub color: [f32; 4],
    /// 塗り／輪郭。
    pub mode: Primitive3dDrawMode,
    /// 線の太さ（**画面ピクセル**。距離に依らず一定）。
    pub thickness_px: f32,
    /// 深度テスト（LessEqual）を行うか。false なら常に手前へ描く。
    pub depth_test: bool,
    /// 図形ごとの追加スカラ（意味は `Primitive3dKind` の説明を参照）。
    pub extras: [f32; PRIM3D_EXTRA_FLOATS],
    /// 図形の点列（意味は `Primitive3dKind` の説明を参照）。
    pub points: Vec<[f32; 3]>,
}

// ─── スレッドローカルキュー ──────────────────────────────────

thread_local! {
    /// 現フレームぶんの描画コマンド。`take_commands` で引き取ると空になる。
    static PRIMITIVE3D_COMMANDS: RefCell<Vec<Primitive3dCommand>> =
        const { RefCell::new(Vec::new()) };

    /// 上限超過の警告をこのフレームで既に出したか（ログ爆発防止）。
    static OVERFLOW_WARNED: Cell<bool> = const { Cell::new(false) };
}

/// コマンドを 1 件積む。上限超過時は捨てて false を返す。
///
/// 戻り値は FFI の成否（C# 側は無視して良い）。
pub fn push_command(cmd: Primitive3dCommand) -> bool {
    PRIMITIVE3D_COMMANDS.with(|q| {
        let mut q = q.borrow_mut();
        if q.len() >= MAX_PRIMITIVES3D_PER_FRAME {
            // フレームに 1 回だけ警告する（毎コマンド出すとログで描画が止まる）。
            OVERFLOW_WARNED.with(|w| {
                if !w.get() {
                    w.set(true);
                    eprintln!(
                        "[SEED DRAW3D] 1 フレームの 3D プリミティブ上限 {MAX_PRIMITIVES3D_PER_FRAME} 件を超えました。超過分は描画されません（SEED.Draw3D の呼び出し回数を見直してください）。"
                    );
                }
            });
            return false;
        }
        q.push(cmd);
        true
    })
}

/// 現フレームぶんのコマンドを引き取り、キューを空にする。
///
/// App（frame_renderer）がフレームごとに 1 回だけ呼ぶ。
/// 描画されないフレームでも必ず呼ぶことで「フレーム外に積まれたコマンドは捨てる」
/// 仕様を満たす。
pub fn take_commands() -> Vec<Primitive3dCommand> {
    OVERFLOW_WARNED.with(|w| w.set(false));
    PRIMITIVE3D_COMMANDS.with(|q| std::mem::take(&mut *q.borrow_mut()))
}

/// キューを破棄する（Play 終了・シーン切り替えで残骸を消す）。
pub fn clear_commands() {
    OVERFLOW_WARNED.with(|w| w.set(false));
    PRIMITIVE3D_COMMANDS.with(|q| q.borrow_mut().clear());
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の最小コマンドを作る。
    fn dummy() -> Primitive3dCommand {
        Primitive3dCommand {
            kind: Primitive3dKind::Polyline,
            color: [1.0, 1.0, 1.0, 1.0],
            mode: Primitive3dDrawMode::Outline,
            thickness_px: 1.0,
            depth_test: true,
            extras: [0.0; PRIM3D_EXTRA_FLOATS],
            points: vec![[0.0, 0.0, 0.0], [1.0, 0.0, 0.0]],
        }
    }

    /// take_commands はキューを空にする（毎フレームクリアの保証）。
    #[test]
    fn primitive3d_queue_take_clears() {
        clear_commands();
        assert!(push_command(dummy()));
        assert!(push_command(dummy()));
        let taken = take_commands();
        assert_eq!(taken.len(), 2);
        assert!(take_commands().is_empty());
    }

    /// 上限を超えた push は false を返し、キュー長は上限で止まる。
    #[test]
    fn primitive3d_queue_caps_at_limit() {
        clear_commands();
        for _ in 0..MAX_PRIMITIVES3D_PER_FRAME {
            assert!(push_command(dummy()));
        }
        assert!(!push_command(dummy()));
        let taken = take_commands();
        assert_eq!(taken.len(), MAX_PRIMITIVES3D_PER_FRAME);
    }

    /// clear_commands は積んだコマンドを捨てる。
    #[test]
    fn primitive3d_queue_clear_discards() {
        clear_commands();
        assert!(push_command(dummy()));
        clear_commands();
        assert!(take_commands().is_empty());
    }

    /// FFI パラメータ長は「ヘッダ + 追加スカラ」で固定（C# 側と一致必須）。
    #[test]
    fn primitive3d_param_floats_is_header_plus_extras() {
        assert_eq!(
            PRIM3D_PARAM_FLOATS,
            PRIM3D_HEADER_FLOATS + PRIM3D_EXTRA_FLOATS
        );
        assert_eq!(PRIM3D_PARAM_FLOATS, 13);
    }

    /// 図形種別の数値表現は C# 側の kind 定数と 1:1 対応する。
    #[test]
    fn primitive3d_kind_roundtrip() {
        let all = [
            Primitive3dKind::Polyline,
            Primitive3dKind::Polygon,
            Primitive3dKind::Circle,
            Primitive3dKind::Ring,
            Primitive3dKind::Arc,
            Primitive3dKind::WireSphere,
            Primitive3dKind::WireBox,
            Primitive3dKind::WireCapsule,
            Primitive3dKind::Arrow,
            Primitive3dKind::Point,
        ];
        for (i, k) in all.iter().enumerate() {
            assert_eq!(Primitive3dKind::from_i32(i as i32), Some(*k));
        }
        assert_eq!(Primitive3dKind::from_i32(all.len() as i32), None);
        assert_eq!(Primitive3dKind::from_i32(-1), None);
    }
}
