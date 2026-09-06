// ============================================================
//  primitive2d/queue.rs — スクリプトが積む 2D プリミティブ描画コマンドキュー
//
//  【役割】
//  C# の `SEED.Draw.*`（イミディエイトモード API）が FFI 経由で積んだ
//  「このフレームに描く図形」のコマンドを 1 フレームぶん貯める。
//  貯めたコマンドはフレーム描画時に `take_commands()` で丸ごと引き取られ、
//  キューは空になる（＝毎フレーム自動クリア。前フレームの図形は残らない）。
//
//  【なぜキューか】
//  スクリプト実行中は GPU リソース（DrawContext）へ触れないため、
//  他のスクリプト API（SCENE_COMMANDS / AUDIO_COMMANDS）と同じく
//  「スクリプトは積むだけ・App が消費する」構造に揃える。
//
//  【スレッド】
//  スクリプトは CLR メインスレッド専用（scripting/mod.rs）で、描画も同じ
//  メインスレッドで行うため thread_local で足りる（他キューと同じ方針）。
// ============================================================

use std::cell::{Cell, RefCell};

use crate::engine::ecs::Entity;

// ─── 上限・データ表現の定数 ──────────────────────────────────

/// 1 フレームに積めるプリミティブの上限。
/// これを超えた分は捨てて 1 フレーム 1 回だけ警告ログを出す
/// （無限に積まれてメモリと CPU を食い潰すのを防ぐ安全弁）。
pub const MAX_PRIMITIVES_PER_FRAME: usize = 4096;

/// 1 プリミティブが持てる点の上限（Polyline / Polygon の頂点数）。
/// 超過分は切り捨てる（スクリプト側の暴走を描画側で吸収する）。
pub const MAX_POINTS_PER_PRIMITIVE: usize = 1024;

/// FFI パラメータ配列の共通ヘッダ長（float 個数）。
/// 内訳: color RGBA(4) + mode(1) + thickness(1) + layer(1)
///       + srt.position(2) + srt.rotation_deg(1) + srt.scale(2) = 12
pub const PRIM_HEADER_FLOATS: usize = 12;

/// 共通ヘッダに続く「図形ごとの追加スカラ」の個数（固定長）。
/// 最も多いのは RegularPolygon（半径・頂点数・回転・スケール XY = 5）。
pub const PRIM_EXTRA_FLOATS: usize = 5;

/// FFI パラメータ配列の総 float 個数（C# 側と完全一致必須）。
pub const PRIM_PARAM_FLOATS: usize = PRIM_HEADER_FLOATS + PRIM_EXTRA_FLOATS;

// ─── 図形種別・描画モード ────────────────────────────────────

/// プリミティブの図形種別。値は C# 側 `Draw.cs` の kind 定数と一致必須。
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum PrimitiveKind {
    /// 任意の閉じた多角形（Rect / Triangle / Polygon が集約される）。points = 輪郭。
    Polygon = 0,
    /// 折れ線（Line もこれに集約される）。extras[0] = 閉じるか（0/1）。
    Polyline = 1,
    /// 円・楕円。points[0] = 中心 / extras[0] = 半径 / extras[1..2] = XY スケール。
    Circle = 2,
    /// 正多角形。points[0] = 中心 / extras[0] = 半径 / extras[1] = 頂点数 /
    /// extras[2] = 回転（度）/ extras[3..4] = XY スケール。
    RegularPolygon = 3,
    /// リング（円環セクタ）。points[0] = 中心 / extras[0] = 内半径 /
    /// extras[1] = 外半径 / extras[2] = 開始角（度）/ extras[3] = 終了角（度）。
    Ring = 4,
    /// 円弧。points[0] = 中心 / extras[0] = 半径 / extras[1] = 開始角 / extras[2] = 終了角。
    /// Fill は「太さ thickness のリング」、Outline は「太さ thickness の線」として描く。
    Arc = 5,
    /// 角丸多角形（角丸矩形が主用途）。points = 輪郭 / extras[0] = 角丸半径。
    RoundedRect = 6,
    /// 3 次ベジエ曲線。points = p0..p3 / extras[0] = 分割数。常に線として描く。
    Bezier = 7,
}

impl PrimitiveKind {
    /// FFI で渡された整数値から図形種別へ変換する。未知の値は None（コマンドを捨てる）。
    pub fn from_i32(v: i32) -> Option<Self> {
        match v {
            0 => Some(Self::Polygon),
            1 => Some(Self::Polyline),
            2 => Some(Self::Circle),
            3 => Some(Self::RegularPolygon),
            4 => Some(Self::Ring),
            5 => Some(Self::Arc),
            6 => Some(Self::RoundedRect),
            7 => Some(Self::Bezier),
            _ => None,
        }
    }
}

/// 塗りつぶし／輪郭線の描画モード。値は C# 側 `DrawMode` と一致必須。
#[derive(Copy, Clone, Debug, PartialEq, Eq)]
pub enum PrimitiveDrawMode {
    /// 内側を塗りつぶす。
    Fill = 0,
    /// 輪郭を太さ `thickness` の線で描く。
    Outline = 1,
}

impl PrimitiveDrawMode {
    /// FFI の float 値（0.0/1.0）から変換する。範囲外は Fill 扱い。
    pub fn from_f32(v: f32) -> Self {
        if v >= 0.5 {
            Self::Outline
        } else {
            Self::Fill
        }
    }
}

// ─── Transform2D ─────────────────────────────────────────────

/// スクリプトから渡される 2D の SRT（スケール → 回転 → 平行移動）。
///
/// ローカル点列へ「スケール → 回転 → 平行移動」の順で適用する
/// （C# 側 `Transform2D` と同じ規約）。
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct Transform2d {
    /// 平行移動（描画空間の単位 = px）。
    pub position: [f32; 2],
    /// Z 軸まわりの回転（度。画面座標系は Y 下向きなので時計回りが正）。
    pub rotation_deg: f32,
    /// XY スケール。
    pub scale: [f32; 2],
}

impl Default for Transform2d {
    fn default() -> Self {
        Self::IDENTITY
    }
}

impl Transform2d {
    /// 何もしない SRT。
    pub const IDENTITY: Self = Self {
        position: [0.0, 0.0],
        rotation_deg: 0.0,
        scale: [1.0, 1.0],
    };

    /// ローカル点へ SRT を適用する（スケール → 回転 → 平行移動の順）。
    pub fn apply(&self, p: [f32; 2]) -> [f32; 2] {
        let sx = p[0] * self.scale[0];
        let sy = p[1] * self.scale[1];
        let rad = self.rotation_deg.to_radians();
        let (s, c) = rad.sin_cos();
        [
            sx * c - sy * s + self.position[0],
            sx * s + sy * c + self.position[1],
        ]
    }
}

// ─── コマンド ────────────────────────────────────────────────

/// スクリプトが積んだ 1 図形ぶんの描画コマンド。
///
/// 座標系は `space` で決まる:
/// - `None`      : スクリーンスペース（左上原点・px・Y 下向き）
/// - `Some(ent)` : そのアクター（CanvasTransform を持つ）のローカル空間。
///   アンカー・ピボット・親子スケールはスプライトとまったく同じ連鎖を通る。
#[derive(Clone, Debug)]
pub struct PrimitiveCommand {
    /// 図形種別。
    pub kind: PrimitiveKind,
    /// 座標空間の基準アクター（None = スクリーンスペース）。
    pub space: Option<Entity>,
    /// RGBA カラー（0..1）。
    pub color: [f32; 4],
    /// 塗り／輪郭。
    pub mode: PrimitiveDrawMode,
    /// 線の太さ（描画空間の px）。Outline / 線系の図形でのみ使う。
    pub thickness: f32,
    /// 描画レイヤー（大きいほど手前。スプライト／テキストと同じソート軸）。
    pub layer: i32,
    /// 点列へ適用する SRT。
    pub srt: Transform2d,
    /// 図形ごとの追加スカラ（意味は `PrimitiveKind` の説明を参照）。
    pub extras: [f32; PRIM_EXTRA_FLOATS],
    /// 図形の点列（意味は `PrimitiveKind` の説明を参照）。
    pub points: Vec<[f32; 2]>,
}

// ─── スレッドローカルキュー ──────────────────────────────────

thread_local! {
    /// 現フレームぶんの描画コマンド。`take_commands` で引き取ると空になる。
    static PRIMITIVE_COMMANDS: RefCell<Vec<PrimitiveCommand>> =
        const { RefCell::new(Vec::new()) };

    /// 上限超過の警告をこのフレームで既に出したか（ログ爆発防止）。
    static OVERFLOW_WARNED: Cell<bool> = const { Cell::new(false) };
}

/// コマンドを 1 件積む。上限超過時は捨てて false を返す。
///
/// 戻り値は FFI の成否（C# 側は無視して良い）。
pub fn push_command(cmd: PrimitiveCommand) -> bool {
    PRIMITIVE_COMMANDS.with(|q| {
        let mut q = q.borrow_mut();
        if q.len() >= MAX_PRIMITIVES_PER_FRAME {
            // フレームに 1 回だけ警告する（毎コマンド出すとログで描画が止まる）。
            OVERFLOW_WARNED.with(|w| {
                if !w.get() {
                    w.set(true);
                    eprintln!(
                        "[SEED DRAW] 1 フレームのプリミティブ上限 {MAX_PRIMITIVES_PER_FRAME} 件を超えました。超過分は描画されません（SEED.Draw の呼び出し回数を見直してください）。"
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
/// 描画されないフレーム（非 Play・ウィンドウ最小化等）でも必ず呼ぶことで
/// 「フレーム外に積まれたコマンドは捨てる」仕様を満たす。
pub fn take_commands() -> Vec<PrimitiveCommand> {
    OVERFLOW_WARNED.with(|w| w.set(false));
    PRIMITIVE_COMMANDS.with(|q| std::mem::take(&mut *q.borrow_mut()))
}

/// キューを破棄する（Play 終了・シーン切り替えで残骸を消す）。
pub fn clear_commands() {
    OVERFLOW_WARNED.with(|w| w.set(false));
    PRIMITIVE_COMMANDS.with(|q| q.borrow_mut().clear());
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の最小コマンドを作る。
    fn dummy() -> PrimitiveCommand {
        PrimitiveCommand {
            kind: PrimitiveKind::Polygon,
            space: None,
            color: [1.0, 1.0, 1.0, 1.0],
            mode: PrimitiveDrawMode::Fill,
            thickness: 1.0,
            layer: 0,
            srt: Transform2d::IDENTITY,
            extras: [0.0; PRIM_EXTRA_FLOATS],
            points: vec![[0.0, 0.0], [1.0, 0.0], [0.0, 1.0]],
        }
    }

    /// take_commands はキューを空にする（毎フレームクリアの保証）。
    #[test]
    fn primitive_queue_take_clears() {
        clear_commands();
        assert!(push_command(dummy()));
        assert!(push_command(dummy()));
        let taken = take_commands();
        assert_eq!(taken.len(), 2);
        // 2 回目は空
        assert!(take_commands().is_empty());
    }

    /// 上限を超えた push は false を返し、キュー長は上限で止まる。
    #[test]
    fn primitive_queue_caps_at_limit() {
        clear_commands();
        for _ in 0..MAX_PRIMITIVES_PER_FRAME {
            assert!(push_command(dummy()));
        }
        assert!(!push_command(dummy()));
        let taken = take_commands();
        assert_eq!(taken.len(), MAX_PRIMITIVES_PER_FRAME);
    }

    /// Transform2D はスケール → 回転 → 平行移動の順で適用される。
    #[test]
    fn primitive_transform2d_order() {
        let t = Transform2d {
            position: [10.0, 20.0],
            rotation_deg: 90.0,
            scale: [2.0, 3.0],
        };
        // (1,0) → スケール (2,0) → 90° 回転 (0,2) → 平行移動 (10,22)
        let p = t.apply([1.0, 0.0]);
        assert!((p[0] - 10.0).abs() < 1e-4, "x={}", p[0]);
        assert!((p[1] - 22.0).abs() < 1e-4, "y={}", p[1]);
    }
}
