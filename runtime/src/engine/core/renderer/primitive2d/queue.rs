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

/// 見た目の拡張（W2-8。`PrimitiveStyle`）の float 個数。パラメータ配列の末尾に続く（無ければ従来の見た目）。
/// 内訳: 旗(1) + グラデーションの終わりの色 RGBA(4) + グラデーションの始点(2) + 終点(2) = 9
pub const PRIM_STYLE_FLOATS: usize = 9;

/// 見た目の拡張つきのパラメータ配列の総 float 個数（C# の `Draw` の拡張の呼び出しと一致必須）。
pub const PRIM_PARAM_FLOATS_STYLED: usize = PRIM_PARAM_FLOATS + PRIM_STYLE_FLOATS;

/// 見た目の拡張の旗: アンチエイリアスの帯を画面の 1 画素の幅にする。
pub const PRIM_STYLE_FLAG_PIXEL_FEATHER: u32 = 1;
/// 見た目の拡張の旗: 線形のグラデーションで塗る。
pub const PRIM_STYLE_FLAG_GRADIENT: u32 = 2;

/// 見た目の拡張の中の位置（旗）。
const STYLE_FLAGS: usize = 0;
/// 見た目の拡張の中の位置（終わりの色の先頭）。
const STYLE_COLOR_END: usize = 1;
/// 見た目の拡張の中の位置（始点の先頭）。
const STYLE_FROM: usize = 5;
/// 見た目の拡張の中の位置（終点の先頭）。
const STYLE_TO: usize = 7;
/// グラデーションの軸の長さの二乗の下限（これ以下は軸が無いとみなし、始めの色で塗る）。
const GRADIENT_AXIS_EPSILON_SQ: f32 = 1e-12;

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
    /// 線の下の塗り（W2-8。グラフの面）。points = 上の縁の折れ線（左 → 右）/ extras[0] = 基準線の y（描画空間）。
    /// 縁と基準線の間を縦の台形の帯で塗る（多角形の耳刈りを使わないので点が多くても線形の手間）。
    /// アンチエイリアスの帯は上の縁（基準線から遠い側）だけに張る。
    Area = 8,
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
            8 => Some(Self::Area),
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

// ─── 見た目の拡張（W2-8）───────────────────────────────────────

/// 線形のグラデーション（始点で `PrimitiveCommand::color`、終点で `color_end`。軸の外は端の色）。
#[derive(Copy, Clone, Debug, PartialEq)]
pub struct PrimitiveGradient {
    /// 終点の色（RGBA 0..1。ストレートアルファ）。
    pub color_end: [f32; 4],
    /// 始点（点列と同じ空間。SRT を掛ける前）。
    pub from: [f32; 2],
    /// 終点（同上）。
    pub to: [f32; 2],
}

/// 図形の見た目の拡張（W2-8）。既定は従来と同じ（描画空間の 1 単位のフェザー・単色・従来の三角形分割）。
#[derive(Copy, Clone, Debug, PartialEq, Default)]
pub struct PrimitiveStyle {
    /// 見た目の拡張つきで積まれた（C# の DrawStyle を受けるメソッド）。true なら軽い三角形分割を使う
    /// （折れ線は 1 本の帯と外側だけの丸いつなぎ、凸の塗りは扇。tessellate.rs の「軽い三角形分割」）。
    pub extended: bool,
    /// アンチエイリアスの帯を画面の 1 画素にする（dp のキャンバスでも縁がにじまない。3D ワールドキャンバスでは効かない）。
    pub pixel_feather: bool,
    /// 線形のグラデーション（None = 単色）。
    pub gradient: Option<PrimitiveGradient>,
}

impl PrimitiveStyle {
    /// パラメータ配列の末尾（`PRIM_STYLE_FLOATS` 個）から読む【純関数】。短ければ既定。
    pub fn from_params(tail: &[f32]) -> Self {
        if tail.len() < PRIM_STYLE_FLOATS {
            return Self::default();
        }
        let raw = tail[STYLE_FLAGS];
        let flags = if raw.is_finite() && raw > 0.0 { raw as u32 } else { 0 };
        let gradient = (flags & PRIM_STYLE_FLAG_GRADIENT != 0).then(|| PrimitiveGradient {
            color_end: [
                tail[STYLE_COLOR_END],
                tail[STYLE_COLOR_END + 1],
                tail[STYLE_COLOR_END + 2],
                tail[STYLE_COLOR_END + 3],
            ],
            from: [tail[STYLE_FROM], tail[STYLE_FROM + 1]],
            to: [tail[STYLE_TO], tail[STYLE_TO + 1]],
        });
        Self { extended: true, pixel_feather: flags & PRIM_STYLE_FLAG_PIXEL_FEATHER != 0, gradient }
    }

    /// 描画空間の点 `p` の色【純関数】（グラデーションが無ければ `base`）。
    ///
    /// `from`・`to` は SRT を掛けた後の描画空間の点で渡す（呼び出し側が `Transform2d::apply` する）。
    /// 軸へ射影した割合 t = clamp(dot(p − from, to − from) / |to − from|², 0, 1) で `base` → `color_end` を線形に補間する
    /// （三角形の中は頂点の色の線形補間なので、線形のグラデーションは頂点の色だけで正確に出る）。
    pub fn color_at(&self, base: [f32; 4], from: [f32; 2], to: [f32; 2], p: [f32; 2]) -> [f32; 4] {
        let Some(g) = self.gradient else { return base };
        let axis = [to[0] - from[0], to[1] - from[1]];
        let len_sq = axis[0] * axis[0] + axis[1] * axis[1];
        if len_sq <= GRADIENT_AXIS_EPSILON_SQ {
            return base;
        }
        let t = (((p[0] - from[0]) * axis[0] + (p[1] - from[1]) * axis[1]) / len_sq).clamp(0.0, 1.0);
        [
            base[0] + (g.color_end[0] - base[0]) * t,
            base[1] + (g.color_end[1] - base[1]) * t,
            base[2] + (g.color_end[2] - base[2]) * t,
            base[3] + (g.color_end[3] - base[3]) * t,
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
    /// 見た目の拡張（W2-8。既定は従来の見た目）。
    pub style: PrimitiveStyle,
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
            style: PrimitiveStyle::default(),
        }
    }

    /// 見た目の拡張（W2-8）: 短い配列・旗 0 は既定、旗で画面の画素のフェザーとグラデーションを選ぶ。
    #[test]
    fn primitive_style_parses_flags_and_gradient() {
        assert_eq!(PrimitiveStyle::from_params(&[]), PrimitiveStyle::default(), "拡張なし（従来の長さ）は既定");
        let plain = PrimitiveStyle::from_params(&[0.0; PRIM_STYLE_FLOATS]);
        assert!(plain.extended && !plain.pixel_feather && plain.gradient.is_none(), "拡張つき・旗 0 は軽い三角形分割だけ");
        let s = PrimitiveStyle::from_params(&[1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0]);
        assert!(s.pixel_feather && s.gradient.is_none());
        let s = PrimitiveStyle::from_params(&[3.0, 0.1, 0.2, 0.3, 0.0, 0.0, 10.0, 0.0, 110.0]);
        assert!(s.pixel_feather);
        let g = s.gradient.expect("旗 2 でグラデーション");
        assert_eq!(g.color_end, [0.1, 0.2, 0.3, 0.0]);
        assert_eq!((g.from, g.to), ([0.0, 10.0], [0.0, 110.0]));
        let broken = PrimitiveStyle::from_params(&[f32::NAN; PRIM_STYLE_FLOATS]);
        assert!(!broken.pixel_feather && broken.gradient.is_none(), "壊れた旗は旗なし");
    }

    /// グラデーションの色: 始点で元の色、終点で終わりの色、途中は線形、軸の外は端の色、軸が無ければ元の色。
    #[test]
    fn primitive_gradient_color_is_linear_along_axis() {
        let s = PrimitiveStyle::from_params(&[2.0, 1.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 100.0]);
        let base = [0.0, 0.0, 1.0, 1.0];
        let (from, to) = ([0.0, 0.0], [0.0, 100.0]);
        assert_eq!(s.color_at(base, from, to, [5.0, 0.0]), base, "始点の高さ");
        assert_eq!(s.color_at(base, from, to, [5.0, 100.0]), [1.0, 0.0, 0.0, 0.0], "終点の高さ");
        let mid = s.color_at(base, from, to, [30.0, 25.0]);
        assert!((mid[0] - 0.25).abs() < 1e-6 && (mid[2] - 0.75).abs() < 1e-6 && (mid[3] - 0.75).abs() < 1e-6, "{mid:?}");
        assert_eq!(s.color_at(base, from, to, [0.0, -50.0]), base, "軸の手前は始めの色");
        assert_eq!(s.color_at(base, from, to, [0.0, 500.0]), [1.0, 0.0, 0.0, 0.0], "軸の先は終わりの色");
        assert_eq!(s.color_at(base, from, from, [0.0, 50.0]), base, "軸の長さ 0 は元の色");
        assert_eq!(PrimitiveStyle::default().color_at(base, from, to, [0.0, 50.0]), base, "グラデーションなし");
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
