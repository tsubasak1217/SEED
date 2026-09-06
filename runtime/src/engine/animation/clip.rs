// ============================================================
//  clip.rs — キーフレームアニメーションのアセットデータ型 & ロード
//
//  【役割】
//  .anim（JSON）ファイルを表す AnimationClip とその構成要素
//  （トラック・キーフレーム・値型・補間種別・ループ種別・イベント）を定義し、
//  asset_fs 経由でロードする。
//
//  【設計方針】
//  - シリアライズは「生データ型（Raw*）」で受け、ロード時に「型付きデータ型」へ
//    変換する 2 段構えにする。これにより value_type に応じた value のパース
//    （スカラー / 配列 / bool）を 1 か所に集約でき、serde のカスタム実装を避ける。
//  - 値は AnimValue で統一（Float / Vec2 / Vec3 / Color / Bool）。
//  - 回転はクォータニオンを扱わない（2D=角度 float, 3D=Euler[f32;3] を vec3 として補間）。
// ============================================================

use serde::Deserialize;

// ─── 列挙型 ───────────────────────────────────────────────────

/// クリップの値型。トラック単位で 1 つ持ち、キーフレームの value 解釈を決める。
#[derive(Clone, Copy, PartialEq, Eq, Debug, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ValueType {
    /// スカラー（f32 1 個）
    Float,
    /// 2 次元ベクトル（[f32;2]）
    Vec2,
    /// 3 次元ベクトル（[f32;3]）
    Vec3,
    /// RGBA カラー（[f32;4]）
    Color,
    /// 真偽値（常に step 補間）
    Bool,
}

/// キーフレーム間の補間種別。
#[derive(Clone, Copy, PartialEq, Eq, Debug, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Interp {
    /// ステップ（次のキーまで値を保持）
    Step,
    /// 線形補間
    Linear,
    /// ベジェ（エルミート）補間。タンジェント省略時は Catmull-Rom 風の自動タンジェント。
    Bezier,
}

impl Default for Interp {
    /// interp 省略時の既定は線形補間とする。
    fn default() -> Self {
        Interp::Linear
    }
}

/// クリップ全体のループ再生種別。
#[derive(Clone, Copy, PartialEq, Eq, Debug, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum LoopMode {
    /// 一度だけ再生し、末尾で停止する
    Once,
    /// 末尾に達したら先頭へ戻って繰り返す
    Loop,
    /// 末尾に達したら逆再生して往復する
    PingPong,
}

impl Default for LoopMode {
    /// loop_mode 省略時の既定は once とする。
    fn default() -> Self {
        LoopMode::Once
    }
}

// ─── AnimValue（統一値型）─────────────────────────────────────

/// アニメーションで扱う値の統一表現。
///
/// 補間は内部的に f32 成分列（to_components）へ落としてから行い、
/// 評価後に value_type に応じて from_components で再構築する。
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum AnimValue {
    /// スカラー
    Float(f32),
    /// 2 次元ベクトル
    Vec2([f32; 2]),
    /// 3 次元ベクトル
    Vec3([f32; 3]),
    /// RGBA カラー
    Color([f32; 4]),
    /// 真偽値
    Bool(bool),
}

impl AnimValue {
    /// この値の値型を返す。
    pub fn value_type(&self) -> ValueType {
        match self {
            AnimValue::Float(_) => ValueType::Float,
            AnimValue::Vec2(_) => ValueType::Vec2,
            AnimValue::Vec3(_) => ValueType::Vec3,
            AnimValue::Color(_) => ValueType::Color,
            AnimValue::Bool(_) => ValueType::Bool,
        }
    }

    /// 補間用に f32 成分列へ変換する（Bool は 0.0 / 1.0）。
    pub fn to_components(&self) -> Vec<f32> {
        match self {
            AnimValue::Float(v) => vec![*v],
            AnimValue::Vec2(v) => v.to_vec(),
            AnimValue::Vec3(v) => v.to_vec(),
            AnimValue::Color(v) => v.to_vec(),
            AnimValue::Bool(b) => vec![if *b { 1.0 } else { 0.0 }],
        }
    }

    /// 値型と f32 成分列から AnimValue を再構築する。
    /// 成分数が不足している場合は 0.0 で補う（安全側フォールバック）。
    pub fn from_components(vt: ValueType, c: &[f32]) -> AnimValue {
        // 指定インデックスの成分を安全に取り出すヘルパー
        let g = |i: usize| c.get(i).copied().unwrap_or(0.0);
        match vt {
            ValueType::Float => AnimValue::Float(g(0)),
            ValueType::Vec2 => AnimValue::Vec2([g(0), g(1)]),
            ValueType::Vec3 => AnimValue::Vec3([g(0), g(1), g(2)]),
            ValueType::Color => AnimValue::Color([g(0), g(1), g(2), g(3)]),
            // Bool は 0.5 を境に true/false 判定
            ValueType::Bool => AnimValue::Bool(g(0) >= 0.5),
        }
    }
}

// ─── 型付きデータ型 ──────────────────────────────────────────

/// トラックの適用先（どのアクター・どのコンポーネント・どのプロパティか）。
#[derive(Clone, Debug)]
pub struct TrackTarget {
    /// Animator 保持アクタからの相対パス（"/" 区切りの子アクタ名。空 = 自分）
    pub actor_path: String,
    /// 対象コンポーネント種別のレジストリキー（例 "actor_transform"）
    pub component: String,
    /// 対象プロパティ名（例 "position"）
    pub property: String,
}

/// 1 本のキーフレーム。
#[derive(Clone, Debug)]
pub struct Keyframe {
    /// クリップ先頭からの時刻（秒）
    pub time: f32,
    /// キーの値
    pub value: AnimValue,
    /// このキーから次のキーへ向かう区間の補間種別
    pub interp: Interp,
    /// 入りタンジェント（value と同数の成分。省略時は自動計算）
    pub in_tan: Option<Vec<f32>>,
    /// 出タンジェント（value と同数の成分。省略時は自動計算）
    pub out_tan: Option<Vec<f32>>,
}

/// 1 本のトラック（1 プロパティの時系列）。
#[derive(Clone, Debug)]
pub struct Track {
    /// 適用先
    pub target: TrackTarget,
    /// 値型
    pub value_type: ValueType,
    /// 時刻昇順のキーフレーム列
    pub keys: Vec<Keyframe>,
}

/// アニメーションイベント（P1 ではフォーマット保持のみ・ディスパッチ未対応）。
#[derive(Clone, Debug, Deserialize)]
pub struct AnimEvent {
    /// 発火時刻（秒）
    pub time: f32,
    /// イベント名
    pub name: String,
}

/// キーフレームアニメーションクリップ（.anim アセットの型付き表現）。
#[derive(Clone, Debug)]
pub struct AnimationClip {
    /// クリップ名
    pub name: String,
    /// 全体尺（秒）。0 以下ならロード時に最大キー時刻から補完する。
    pub duration: f32,
    /// ループ種別
    pub loop_mode: LoopMode,
    /// トラック列
    pub tracks: Vec<Track>,
    /// イベント列（P1 未使用）
    pub events: Vec<AnimEvent>,
}

// ─── 生データ型（serde 受け口）────────────────────────────────

/// TrackTarget の serde 受け口。
#[derive(Deserialize)]
struct RawTarget {
    #[serde(default)]
    actor_path: String,
    component: String,
    property: String,
}

/// Keyframe の serde 受け口。value は value_type 依存のため生 JSON で受ける。
#[derive(Deserialize)]
struct RawKeyframe {
    time: f32,
    /// 値。value_type に応じて後段でパースする（スカラー / 配列 / bool）。
    value: serde_json::Value,
    #[serde(default)]
    interp: Interp,
    #[serde(default)]
    in_tan: Option<Vec<f32>>,
    #[serde(default)]
    out_tan: Option<Vec<f32>>,
}

/// Track の serde 受け口。
#[derive(Deserialize)]
struct RawTrack {
    target: RawTarget,
    value_type: ValueType,
    #[serde(default)]
    keys: Vec<RawKeyframe>,
}

/// duration 省略時の既定値関数（0.0 = 後段でキー時刻から補完）。
fn default_duration() -> f32 {
    0.0
}

/// AnimationClip の serde 受け口。
#[derive(Deserialize)]
struct RawClip {
    #[serde(default)]
    name: String,
    #[serde(default = "default_duration")]
    duration: f32,
    #[serde(default)]
    loop_mode: LoopMode,
    #[serde(default)]
    tracks: Vec<RawTrack>,
    #[serde(default)]
    events: Vec<AnimEvent>,
}

// ─── 生データ → 型付きデータ 変換 ─────────────────────────────

/// 生 JSON value を value_type に従って AnimValue へ変換する。
/// 型不一致は Err（呼び出し側で該当トラックを警告付きで無視する）。
fn parse_value(vt: ValueType, raw: &serde_json::Value) -> Result<AnimValue, String> {
    // 配列を固定長 f32 スライスとして取り出すヘルパー
    let as_arr = |n: usize| -> Result<Vec<f32>, String> {
        let arr = raw
            .as_array()
            .ok_or_else(|| format!("expected array of len {n}, got {raw}"))?;
        if arr.len() != n {
            return Err(format!("expected array len {n}, got {}", arr.len()));
        }
        arr.iter()
            .map(|v| {
                v.as_f64()
                    .map(|f| f as f32)
                    .ok_or_else(|| format!("non-number element in array: {v}"))
            })
            .collect()
    };
    match vt {
        ValueType::Float => {
            let f = raw
                .as_f64()
                .ok_or_else(|| format!("expected number, got {raw}"))?;
            Ok(AnimValue::Float(f as f32))
        }
        ValueType::Vec2 => {
            let a = as_arr(2)?;
            Ok(AnimValue::Vec2([a[0], a[1]]))
        }
        ValueType::Vec3 => {
            let a = as_arr(3)?;
            Ok(AnimValue::Vec3([a[0], a[1], a[2]]))
        }
        ValueType::Color => {
            let a = as_arr(4)?;
            Ok(AnimValue::Color([a[0], a[1], a[2], a[3]]))
        }
        ValueType::Bool => {
            let b = raw
                .as_bool()
                .ok_or_else(|| format!("expected bool, got {raw}"))?;
            Ok(AnimValue::Bool(b))
        }
    }
}

impl AnimationClip {
    /// asset_fs 経由で .anim（JSON）をロードして型付きクリップを返す。
    ///
    /// - path は "assets://..." 仮想パスまたは絶対パス。
    /// - 個々のトラックで値パースに失敗した場合、そのトラックのみ警告して無視し、
    ///   クリップ全体のロードは継続する（1 トラックの不備で全滅させない）。
    pub fn load(path: &str) -> Result<AnimationClip, String> {
        // ファイル読み込み（PAK / FS フォールバックは asset_fs が処理）
        let text =
            crate::engine::asset_fs::read_string(path).map_err(|e| format!("{path}: {e}"))?;
        Self::from_json(path, &text)
    }

    /// .anim の JSON 文字列を型付きクリップへ変換する（I/O を伴わない純関数）。
    ///
    /// `label` はエラーメッセージ・警告に出す識別子（通常はアセットパス）。
    /// ファイル読み込みと分離してあるのは、フォーマットの検証を単体テストできるようにするため。
    pub fn from_json(label: &str, text: &str) -> Result<AnimationClip, String> {
        let path = label;
        let raw: RawClip =
            serde_json::from_str(text).map_err(|e| format!("{path}: JSON parse error: {e}"))?;

        // トラックを型付きへ変換する
        let mut tracks = Vec::new();
        for (ti, rt) in raw.tracks.into_iter().enumerate() {
            let vt = rt.value_type;
            // キーを変換（bool トラックは常に step へ矯正する）
            let mut keys = Vec::with_capacity(rt.keys.len());
            let mut track_ok = true;
            for rk in &rt.keys {
                match parse_value(vt, &rk.value) {
                    Ok(value) => {
                        // bool は補間不可のため interp を step に固定する
                        let interp = if vt == ValueType::Bool {
                            Interp::Step
                        } else {
                            rk.interp
                        };
                        keys.push(Keyframe {
                            time: rk.time,
                            value,
                            interp,
                            in_tan: rk.in_tan.clone(),
                            out_tan: rk.out_tan.clone(),
                        });
                    }
                    Err(err) => {
                        eprintln!(
                            "[SEED anim] track {ti} ({}/{}): 値パース失敗のためトラックを無視: {err}",
                            rt.target.component, rt.target.property
                        );
                        track_ok = false;
                        break;
                    }
                }
            }
            if !track_ok {
                continue;
            }
            // 時刻昇順に整列（評価は昇順前提）
            keys.sort_by(|a, b| {
                a.time
                    .partial_cmp(&b.time)
                    .unwrap_or(std::cmp::Ordering::Equal)
            });
            tracks.push(Track {
                target: TrackTarget {
                    actor_path: rt.target.actor_path,
                    component: rt.target.component,
                    property: rt.target.property,
                },
                value_type: vt,
                keys,
            });
        }

        // duration が未指定（0 以下）なら全トラックの最大キー時刻から補完する
        let mut duration = raw.duration;
        if duration <= 0.0 {
            duration = tracks
                .iter()
                .flat_map(|t| t.keys.iter().map(|k| k.time))
                .fold(0.0_f32, f32::max);
        }

        Ok(AnimationClip {
            name: raw.name,
            duration,
            loop_mode: raw.loop_mode,
            tracks,
            events: raw.events,
        })
    }
}

// ============================================================
//  テスト（.anim フォーマットの往復と、明示タンジェントによるイージング再現）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::animation::sampler::sample_track;

    /// HIT 帯演出クリップが置かれているディレクトリ（runtime/assets は junction）。
    const CLIP_DIR: &str = concat!(env!("CARGO_MANIFEST_DIR"), "/assets/mainGame/animations/");

    /// 帯（上）のクリップ名。入場は法線の逆側から降りてくる。
    const BAND_TOP_CLIP: &str = "hit_banner_band_top.anim";

    /// 帯（下）のクリップ名。入場は法線側から上がってくる。
    const BAND_BOTTOM_CLIP: &str = "hit_banner_band_bottom.anim";

    /// 「Lv◯ 魚名」文字のクリップ名。
    const TEXT_LEVEL_CLIP: &str = "hit_banner_text_level.anim";

    /// 「HIT!!!」文字のクリップ名。
    const TEXT_HIT_CLIP: &str = "hit_banner_text_hit.anim";

    /// 4 つのクリップ（アイテム 1 つにつき 1 ファイル）。
    const ALL_CLIPS: [&str; 4] = [
        BAND_TOP_CLIP,
        BAND_BOTTOM_CLIP,
        TEXT_LEVEL_CLIP,
        TEXT_HIT_CLIP,
    ];

    /// 文字クリップが画面外で静止している時間（秒）。文字の出遅れ表現。
    const TEXT_DELAY_SECONDS: f32 = 0.10;

    /// 位置の比較に許す誤差（px）。明示タンジェントの丸め誤差ぶんだけ緩める。
    const POSITION_EPSILON: f32 = 0.5;

    /// クリップを実ファイルから読む（読めない・壊れているならテストを落とす）。
    fn load(file_name: &str) -> AnimationClip {
        let path = format!("{CLIP_DIR}{file_name}");
        let text = std::fs::read_to_string(&path)
            .unwrap_or_else(|e| panic!("{file_name} が読めること: {e}"));
        AnimationClip::from_json(&path, &text)
            .unwrap_or_else(|e| panic!("{file_name} の JSON が解析できること: {e}"))
    }

    /// 指定トラックを探す（見つからなければパニックしてテストを落とす）。
    /// アイテムごとにクリップを分けたので、束縛先は常に自分自身（actor_path 空文字）。
    fn find_track<'a>(clip: &'a AnimationClip, comp: &str, prop: &str) -> &'a Track {
        clip.tracks
            .iter()
            .find(|t| t.target.actor_path.is_empty() && t.target.component == comp && t.target.property == prop)
            .unwrap_or_else(|| panic!("{comp}.{prop} トラックが無い"))
    }

    /// Vec2 の成分を取り出す（値型違いはテスト失敗）。
    fn vec2_of(v: AnimValue) -> [f32; 2] {
        match v {
            AnimValue::Vec2(a) => a,
            other => panic!("vec2 を期待したが {other:?}"),
        }
    }

    /// 4 つの実ファイルを読んで構造（尺・ループ・トラック構成・束縛先）を検証する。
    #[test]
    fn hit_banner_clips_parse() {
        for file_name in ALL_CLIPS {
            let clip = load(file_name);

            assert_eq!(clip.name, "Hit", "{file_name}");
            assert_eq!(clip.duration, 1.75, "{file_name}");
            assert_eq!(clip.loop_mode, LoopMode::Once, "{file_name}");
            // 1 アイテムにつき 位置・回転・色 の 3 トラック
            assert_eq!(clip.tracks.len(), 3, "{file_name}");

            // すべてのトラックが「Animator を持つアクタ自身」を指すこと
            // （アンカーが異なるアイテムを 1 本のクリップでまとめて動かせないための分割）
            for track in &clip.tracks {
                assert!(track.target.actor_path.is_empty(), "{file_name}");
            }

            // 傾きは演出中ずっと一定（−12°）
            let rotation = find_track(&clip, "canvas_transform", "rotation");
            assert_eq!(rotation.keys.len(), 1, "{file_name}");
            assert_eq!(rotation.keys[0].value, AnimValue::Float(-12.0), "{file_name}");
        }
    }

    /// 色トラックが「不透明 → 尺の末尾で透明」の 2 キーであること
    /// （帯は sprite.color・文字は text.color を駆動する）。
    #[test]
    fn hit_banner_clips_fade_out_at_end() {
        let cases: [(&str, &str, [f32; 3]); 4] = [
            (BAND_TOP_CLIP, "sprite", [0.0, 0.0, 0.0]),
            (BAND_BOTTOM_CLIP, "sprite", [0.0, 0.0, 0.0]),
            (TEXT_LEVEL_CLIP, "text", [1.0, 1.0, 1.0]),
            (TEXT_HIT_CLIP, "text", [1.0, 1.0, 1.0]),
        ];

        for (file_name, component, rgb) in cases {
            let clip = load(file_name);
            let color = find_track(&clip, component, "color");

            assert_eq!(color.value_type, ValueType::Color, "{file_name}");
            assert_eq!(color.keys.len(), 2, "{file_name}");
            assert_eq!(
                color.keys[0].value,
                AnimValue::Color([rgb[0], rgb[1], rgb[2], 1.0]),
                "{file_name}"
            );
            assert_eq!(
                color.keys[1].value,
                AnimValue::Color([rgb[0], rgb[1], rgb[2], 0.0]),
                "{file_name}"
            );
        }
    }

    /// 入場区間の明示タンジェントが easeOutCubic を再現すること
    /// （旧 HitBanner.cs の `1 - (1 - t)^3` と一致する）。4 クリップすべてで確認する。
    #[test]
    fn entrance_tangents_reproduce_ease_out_cubic() {
        for file_name in ALL_CLIPS {
            let clip = load(file_name);
            let track = find_track(&clip, "canvas_transform", "position");

            // 文字クリップは先頭に「画面外で静止する step キー」が 1 つ増えるので、
            // 入場区間は「最後から 3 番目 → 4 番目」＝ 静止キーの 1 つ手前から始まる。
            let entrance = track.keys.len() - 4;
            let start = vec2_of(track.keys[entrance].value);
            let rest = vec2_of(track.keys[entrance + 1].value);
            let (t0, t1) = (track.keys[entrance].time, track.keys[entrance + 1].time);

            // 区間内を細かく走査し、easeOutCubic の解析解と一致するか見る
            for step in 0..=10 {
                let u = step as f32 / 10.0;
                let eased = 1.0 - (1.0 - u).powi(3);
                let got = vec2_of(sample_track(track, t0 + (t1 - t0) * u).unwrap());
                for c in 0..2 {
                    let want = start[c] + (rest[c] - start[c]) * eased;
                    assert!(
                        (got[c] - want).abs() < POSITION_EPSILON,
                        "{file_name} u={u} 成分{c}: got {} want {}",
                        got[c],
                        want
                    );
                }
            }
        }
    }

    /// 退場区間の明示タンジェントが easeInCubic（t^3）を再現すること。4 クリップすべてで確認する。
    #[test]
    fn exit_tangents_reproduce_ease_in_cubic() {
        for file_name in ALL_CLIPS {
            let clip = load(file_name);
            let track = find_track(&clip, "canvas_transform", "position");

            let last = track.keys.len() - 1;
            let rest = vec2_of(track.keys[last - 1].value);
            let exit = vec2_of(track.keys[last].value);
            let (t0, t1) = (track.keys[last - 1].time, track.keys[last].time);

            for step in 0..=10 {
                let u = step as f32 / 10.0;
                let eased = u * u * u;
                let got = vec2_of(sample_track(track, t0 + (t1 - t0) * u).unwrap());
                for c in 0..2 {
                    let want = rest[c] + (exit[c] - rest[c]) * eased;
                    assert!(
                        (got[c] - want).abs() < POSITION_EPSILON,
                        "{file_name} u={u} 成分{c}: got {} want {}",
                        got[c],
                        want
                    );
                }
            }
        }
    }

    /// 文字は再生開始から TEXT_DELAY_SECONDS（0.10 秒）まで画面外で静止すること（step 区間）。
    /// 帯には遅れが無いので、この確認は文字クリップだけに行う。
    #[test]
    fn text_holds_offscreen_during_delay() {
        for file_name in [TEXT_LEVEL_CLIP, TEXT_HIT_CLIP] {
            let clip = load(file_name);
            let track = find_track(&clip, "canvas_transform", "position");

            let start = vec2_of(track.keys[0].value);
            assert_eq!(track.keys[1].time, TEXT_DELAY_SECONDS, "{file_name}");
            for t in [0.0_f32, 0.05, 0.099] {
                assert_eq!(
                    vec2_of(sample_track(track, t).unwrap()),
                    start,
                    "{file_name} t={t}"
                );
            }
        }
    }
}
