// ============================================================
//  terrain/scatter/tests_scatter.rs — 散布レイヤ（T3）のユニットテスト
//
//  1. .tscatter ラウンドトリップ（ビット厳密）と各種エラー検出
//  2. props.json の serde 往復・後方互換（省略フィールド／未知フィールド）
//  3. TerrainPropSet::from_json_str の切り詰めと空→既定フォールバック
//  4. ScatterRule::evaluate（窓の内外・フェード単調性・レイヤ条件ゲート）
//  5. surface_hit_down（平面／斜面／地面なし／洞窟の上面優先）
//  6. scatter_chunk_by_rules の決定性（ビット同一）とシード依存
//  7. 密度→本数の期待値、ブラシ追加／消去、最小間隔
//  8. restick_instances（追従・削除・順序保存）
// ============================================================

use super::super::chunk_coord::ChunkCoord;
use super::super::settings::TerrainSettings;
use super::generate::{
    MAX_SCATTER_GRID_PER_AXIS, ScatterField, ScatterRng, hash_seed, restick_instances,
    scatter_brush, scatter_chunk_by_rules, surface_hit_down,
};
use super::props::{
    GRASS_MAX_SEGMENTS, LayerCondition, PropKind, ScatterParams, ScatterRule, TERRAIN_MAX_PROPS,
    TerrainProp, TerrainPropSet,
};
use super::tscatter::{
    ScatterInstance, TSCATTER_MAGIC, TscatterError, read_chunk, read_header, write_chunk,
};

// ─── テスト用定数（マジックナンバー回避）─────────────────────────────────────

/// 接地点 Y の許容誤差（メートル）。二分探索 8 回・step 0.25m で ~1mm 精度。
const HIT_Y_EPS: f32 = 5.0e-3;
/// 法線成分の許容誤差。
const NORMAL_EPS: f32 = 1.0e-3;
/// 確率比較の許容誤差。
const PROB_EPS: f32 = 1.0e-4;
/// テスト地面の高さ（ワールド Y・メートル）。チャンク 0 の内側に入る値。
const TEST_GROUND_HEIGHT: f32 = 4.0;
/// 再接地テストで地面を持ち上げる量（メートル）。
const RESTICK_RAISE: f32 = 2.0;
/// 再接地の探索半幅（メートル）。
const RESTICK_SEARCH: f32 = 6.0;
/// 決定性テスト等で使うグローバルシード。
const TEST_SEED_A: u64 = 0x1234_5678_9ABC_DEF0;
/// 上と異なるグローバルシード（結果が変わることの確認用）。
const TEST_SEED_B: u64 = 0x0FED_CBA9_8765_4321;
/// 本数期待値テストで使う散布密度（1 m² あたり）。
/// sqrt(0.25)*16 = 8 とちょうど整数になるので、グリッド分割数が解析的に決まる。
const COUNT_TEST_DENSITY: f32 = 0.25;
/// 本数期待値の許容比（下限）。ceil による切り上げしか起きないので 1.0。
const COUNT_BAND_LO: f32 = 1.0;
/// 本数期待値の許容比（上限）。
/// cells = ceil(sqrt(D)*E) なので最悪 (sqrt(D)*E + 1)² / (D*E²) 倍まで膨らむ。
/// D=0.25, E=16 では sqrt(D)*E=8 ちょうどなので実際には 1.0 に一致するが、
/// グリッド切り上げの性質上の上限として 1.30 を帯の上端に取る。
const COUNT_BAND_HI: f32 = 1.30;
/// ブラシテストのブラシ半径（メートル）。
const BRUSH_RADIUS: f32 = 3.0;
/// ブラシテストの散布密度（1 m² あたり）。
const BRUSH_DENSITY: f32 = 1.0;
/// 半径判定の許容誤差（メートル）。接地の丸めぶんの余裕。
const RADIUS_EPS: f32 = 1.0e-3;

// ============================================================
//  テスト用の合成密度場
// ============================================================

/// テスト用の合成密度場。
///
/// `density(p) = p.y - height_at(p)` と定義する。
/// settings.rs の規約どおり density < iso(=0) が SOLID（地中）になり、
/// height_at より下が地面、上が空になる。
/// 勾配は (-dh/dx, 1, -dh/dz) なので外向き（上向き）法線と一致する。
struct TestField {
    /// 地形設定（既定＝voxel 0.5m・チャンク 32 セル・extent 16m）。
    settings: TerrainSettings,
    /// 平面の高さ（メートル）。
    height: f32,
    /// X 方向の傾き（dh/dx）。0 なら水平面。
    slope_x: f32,
    /// 地面が存在するか。false なら全域 AIR（密度を常に正にする）。
    has_ground: bool,
    /// `layer_weight_at` が返す固定重み（レイヤ名は無視する簡易スタブ）。
    layer_weight: f32,
}

impl TestField {
    /// 水平な平面地形（高さ h・レイヤ重み 1.0）。
    fn flat(h: f32) -> Self {
        Self {
            settings: TerrainSettings::default(),
            height: h,
            slope_x: 0.0,
            has_ground: true,
            layer_weight: 1.0,
        }
    }

    /// X 方向に傾いた斜面（dh/dx = slope_x）。
    fn sloped(h: f32, slope_x: f32) -> Self {
        Self {
            slope_x,
            ..Self::flat(h)
        }
    }

    /// 地面が一切ない場（全域 AIR）。
    fn empty() -> Self {
        Self {
            has_ground: false,
            ..Self::flat(0.0)
        }
    }

    /// レイヤ重みを差し替えたコピーを作る。
    fn with_layer_weight(mut self, w: f32) -> Self {
        self.layer_weight = w;
        self
    }
}

impl ScatterField for TestField {
    fn settings(&self) -> &TerrainSettings {
        &self.settings
    }

    fn density_at(&self, p: [f32; 3]) -> f32 {
        if !self.has_ground {
            // 地面なし = どこも AIR。iso(0) より確実に大きい値を返す。
            return 1.0;
        }
        p[1] - (self.height + self.slope_x * p[0])
    }

    fn layer_weight_at(&self, _p: [f32; 3], _layer: &str) -> f32 {
        self.layer_weight
    }
}

// ─── テスト用ヘルパ ─────────────────────────────────────────────────────────

/// 任意のフィールド値を持つ散布インスタンスを作る。
fn instance(pos: [f32; 3], prop_id: u32, seed: u32) -> ScatterInstance {
    ScatterInstance {
        pos,
        normal: [0.0, 1.0, 0.0],
        yaw: 1.25,
        scale: 0.75,
        prop_id,
        seed,
    }
}

/// 2 つのインスタンス列が **ビット単位で** 一致するか調べる。
///
/// f32 の `==` は -0.0 == 0.0 や NaN で誤魔化されるため、決定性の検証には
/// 必ず `to_bits()` を使う。
fn bit_identical(a: &[ScatterInstance], b: &[ScatterInstance]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    a.iter().zip(b.iter()).all(|(x, y)| {
        x.pos
            .iter()
            .zip(y.pos.iter())
            .all(|(p, q)| p.to_bits() == q.to_bits())
            && x.normal
                .iter()
                .zip(y.normal.iter())
                .all(|(p, q)| p.to_bits() == q.to_bits())
            && x.yaw.to_bits() == y.yaw.to_bits()
            && x.scale.to_bits() == y.scale.to_bits()
            && x.prop_id == y.prop_id
            && x.seed == y.seed
    })
}

/// 「必ず生える」プロップ（ルール無条件・確率 1）を指定密度で作る。
fn always_prop(density: f32) -> TerrainPropSet {
    TerrainPropSet {
        props: vec![TerrainProp {
            id: "always".to_string(),
            name: "Always".to_string(),
            scatter: ScatterParams {
                density,
                ..ScatterParams::default()
            },
            rule: ScatterRule {
                // 窓を全開・フェード 0・閾値 0 にして確率を厳密に 1 に固定する。
                slope_min_deg: 0.0,
                slope_max_deg: 90.0,
                slope_fade_deg: 0.0,
                height_min: -1.0e6,
                height_max: 1.0e6,
                height_fade: 0.0,
                layer_conditions: Vec::new(),
                threshold: 0.0,
            },
            ..TerrainProp::default()
        }],
    }
}

// ============================================================
//  1. .tscatter ラウンドトリップとエラー検出
// ============================================================

/// 空のインスタンス列でもヘッダだけが書かれ、読み戻して空になることを保証する。
/// （草を全部消したチャンクの保存が壊れる回帰を防ぐ）
#[test]
fn tscatter_roundtrip_empty() {
    let coord = ChunkCoord::new(1, -2, 3);
    let bytes = write_chunk(&[], coord);
    let (got, got_coord) = read_chunk(&bytes).expect("空チャンクが読めること");
    assert!(got.is_empty(), "空で書いたのに要素が復元された");
    assert_eq!(got_coord, coord, "チャンク座標が往復で変わった");
}

/// 1 件のインスタンスが全フィールド **ビット単位** で往復することを保証する。
/// （f32 を一度 f64 等に通して丸める実装ミスの回帰を防ぐ）
#[test]
fn tscatter_roundtrip_single_bit_exact() {
    let coord = ChunkCoord::new(-7, 0, 11);
    let src = vec![ScatterInstance {
        pos: [1.5, -2.25, 3.125],
        normal: [0.0, 1.0, 0.0],
        yaw: 2.718_281_8,
        scale: 0.333_333_34,
        prop_id: 5,
        seed: 0xDEAD_BEEF,
    }];
    let (got, got_coord) = read_chunk(&write_chunk(&src, coord)).expect("読めること");
    assert_eq!(got_coord, coord);
    assert!(
        bit_identical(&src, &got),
        "1 件のラウンドトリップがビット一致しない"
    );
}

/// 多数のインスタンスでも順序とビット値が完全に保たれることを保証する。
/// （オフセット計算のずれで隣のインスタンスを読む回帰を防ぐ）
#[test]
fn tscatter_roundtrip_many_bit_exact() {
    /// ラウンドトリップに使う件数。
    const N: u32 = 257;
    let coord = ChunkCoord::new(0, 0, 0);
    let src: Vec<ScatterInstance> = (0..N)
        .map(|i| ScatterInstance {
            pos: [i as f32 * 0.5, -(i as f32) * 0.25, i as f32],
            normal: [0.5, 0.5, 0.707_106_77],
            yaw: i as f32 * 0.01,
            scale: 1.0 + i as f32 * 0.001,
            prop_id: i % 7,
            seed: i.wrapping_mul(0x9E37_79B9),
        })
        .collect();
    let (got, _) = read_chunk(&write_chunk(&src, coord)).expect("読めること");
    assert!(
        bit_identical(&src, &got),
        "多数件のラウンドトリップがビット一致しない"
    );
}

/// マジックが違うバイト列を `BadMagic` として弾くことを保証する。
/// （他形式のファイルを散布データとして読み込む事故を防ぐ）
#[test]
fn tscatter_bad_magic_is_rejected() {
    let mut bytes = write_chunk(&[], ChunkCoord::new(0, 0, 0));
    bytes[0] = b'X';
    assert_eq!(read_header(&bytes), Err(TscatterError::BadMagic));
    assert_eq!(read_chunk(&bytes).err(), Some(TscatterError::BadMagic));
}

/// 未来のバージョン番号を `BadVersion` として弾くことを保証する。
/// （新形式を旧バイナリが誤解釈して壊れたデータを読む事故を防ぐ）
#[test]
fn tscatter_bad_version_is_rejected() {
    /// 存在しない未来のバージョン番号。
    const FUTURE_VERSION: u32 = 99;
    let mut bytes = write_chunk(&[], ChunkCoord::new(0, 0, 0));
    bytes[4..8].copy_from_slice(&FUTURE_VERSION.to_le_bytes());
    assert_eq!(read_header(&bytes), Err(TscatterError::BadVersion));
    assert_eq!(read_chunk(&bytes).err(), Some(TscatterError::BadVersion));
}

/// ヘッダ長に満たないバイト列を `Truncated` として弾くことを保証する。
/// （途中で切れたファイルを読んで添字外アクセスする回帰を防ぐ）
#[test]
fn tscatter_truncated_header_is_rejected() {
    let bytes = write_chunk(&[], ChunkCoord::new(0, 0, 0));
    // ヘッダ 24 バイトより 1 バイト短く切る。
    let short = &bytes[..bytes.len() - 1];
    assert_eq!(read_header(short), Err(TscatterError::Truncated));
    assert_eq!(read_chunk(short).err(), Some(TscatterError::Truncated));
    // マジックだけの 4 バイトでも同じ。
    assert_eq!(read_header(&TSCATTER_MAGIC), Err(TscatterError::Truncated));
}

/// 本体長がヘッダの件数と食い違うバイト列を `CountMismatch` として弾く。
/// （末尾が欠けた／余分が付いたファイルを黙って読み込む回帰を防ぐ）
#[test]
fn tscatter_count_mismatch_is_rejected() {
    let src = vec![
        instance([0.0, 0.0, 0.0], 0, 1),
        instance([1.0, 0.0, 0.0], 0, 2),
    ];
    let bytes = write_chunk(&src, ChunkCoord::new(0, 0, 0));

    // 末尾を 1 バイト削る → 本体長が 2 件ぶんに足りない。
    let short = &bytes[..bytes.len() - 1];
    assert_eq!(read_chunk(short).err(), Some(TscatterError::CountMismatch));

    // 末尾に余分を足す → 本体長が 2 件ぶんを超える。
    let mut long = bytes.clone();
    long.push(0);
    assert_eq!(read_chunk(&long).err(), Some(TscatterError::CountMismatch));
}

/// ヘッダだけの読み取りが本体を触らずに件数と座標を返すことを保証する。
/// （LOD 判定でヘッダのみ読む経路の回帰を防ぐ）
#[test]
fn tscatter_read_header_reports_count_and_coord() {
    let coord = ChunkCoord::new(9, -9, 0);
    let src = vec![instance([0.0; 3], 0, 0); 3];
    let header = read_header(&write_chunk(&src, coord)).expect("ヘッダが読めること");
    assert_eq!(header.instance_count, 3);
    assert_eq!(header.coord, coord);
}

// ============================================================
//  2. props.json の serde 往復と後方互換
// ============================================================

/// 既定プロップセットが JSON を往復しても内容を失わないことを保証する。
/// （serde 属性の付け忘れでフィールドが落ちる回帰を防ぐ）
#[test]
fn props_serde_roundtrip_preserves_default_set() {
    let src = TerrainPropSet::default();
    let json = serde_json::to_string(&src).expect("直列化できること");
    let got = TerrainPropSet::from_json_str(&json).expect("復元できること");

    assert_eq!(got.props.len(), src.props.len());
    for (a, b) in src.props.iter().zip(got.props.iter()) {
        assert_eq!(a.id, b.id);
        assert_eq!(a.kind, b.kind);
        assert_eq!(a.scatter.density.to_bits(), b.scatter.density.to_bits());
        assert_eq!(
            a.rule.slope_max_deg.to_bits(),
            b.rule.slope_max_deg.to_bits()
        );
        assert_eq!(a.rule.layer_conditions.len(), b.rule.layer_conditions.len());
    }
}

/// 最小限の JSON（id/name/kind のみ）が読め、省略フィールドが既定値になることを保証する。
/// （props.json へフィールドを追加したときに既存データが読めなくなる回帰を防ぐ）
#[test]
fn props_minimal_json_fills_documented_defaults() {
    let json = r#"{"props":[{"id":"g","name":"g","kind":"grass"}]}"#;
    let set = TerrainPropSet::from_json_str(json).expect("最小 JSON が読めること");
    assert_eq!(set.props.len(), 1);
    let p = &set.props[0];

    // 種別と省略された任意フィールド。
    assert_eq!(p.kind, PropKind::Grass);
    assert_eq!(p.model_path, None);

    // 各サブ構造体が「その構造体の Default」と一致すること。
    let d = TerrainProp::default();
    assert_eq!(p.grass.width.to_bits(), d.grass.width.to_bits());
    assert_eq!(p.grass.height.to_bits(), d.grass.height.to_bits());
    assert_eq!(p.grass.segments, d.grass.segments);
    assert_eq!(p.grass.cross_planes, d.grass.cross_planes);
    assert_eq!(p.wind.strength.to_bits(), d.wind.strength.to_bits());
    assert_eq!(p.wind.gust_speed.to_bits(), d.wind.gust_speed.to_bits());
    assert_eq!(p.scatter.density.to_bits(), d.scatter.density.to_bits());
    assert_eq!(p.scatter.align_to_normal, d.scatter.align_to_normal);
    assert_eq!(
        p.rule.slope_max_deg.to_bits(),
        d.rule.slope_max_deg.to_bits()
    );
    assert_eq!(p.rule.threshold.to_bits(), d.rule.threshold.to_bits());
    assert!(
        p.rule.layer_conditions.is_empty(),
        "レイヤ条件の既定は空であるべき"
    );
}

/// 未知のフィールドがあっても読み込みが壊れないことを保証する。
/// （新しいエディタで保存した props.json を旧ランタイムが開ける前方互換）
#[test]
fn props_unknown_field_is_ignored() {
    let json = r#"{"props":[{"id":"g","name":"g","kind":"grass","future_field":42}],"extra":true}"#;
    let set = TerrainPropSet::from_json_str(json).expect("未知フィールドを無視して読めること");
    assert_eq!(set.props.len(), 1);
    assert_eq!(set.props[0].id, "g");
}

/// kind の文字列表現が小文字であることを保証する（rename_all = "lowercase"）。
/// （手書き props.json が読めなくなる回帰を防ぐ）
#[test]
fn props_kind_is_serialized_lowercase() {
    let json = r#"{"props":[{"id":"t","name":"t","kind":"model","model_path":"a/b.glb"}]}"#;
    let set = TerrainPropSet::from_json_str(json).expect("model 種別が読めること");
    assert_eq!(set.props[0].kind, PropKind::Model);
    assert_eq!(set.props[0].model_path.as_deref(), Some("a/b.glb"));
    let out = serde_json::to_string(&set).expect("直列化できること");
    assert!(
        out.contains("\"model\""),
        "kind が小文字で書かれていない: {out}"
    );
}

/// レイヤ条件の min_weight が省略できることを保証する。
/// （条件を名前だけ書いた props.json が読めなくなる回帰を防ぐ）
#[test]
fn props_layer_condition_min_weight_defaults() {
    let json =
        r#"{"props":[{"id":"g","name":"g","rule":{"layer_conditions":[{"layer":"rock"}]}}]}"#;
    let set = TerrainPropSet::from_json_str(json).expect("読めること");
    let cond = &set.props[0].rule.layer_conditions[0];
    assert_eq!(cond.layer, "rock");
    assert!(cond.min_weight > 0.0, "min_weight の既定が 0 になっている");
}

// ============================================================
//  3. from_json_str の切り詰め・フォールバック
// ============================================================

/// TERRAIN_MAX_PROPS を超えるプロップ定義が切り詰められることを保証する。
/// （上限超過でエディタ UI や描画予算が破綻する回帰を防ぐ）
#[test]
fn props_are_truncated_at_max() {
    let over = TERRAIN_MAX_PROPS + 5;
    let items: Vec<String> = (0..over)
        .map(|i| format!(r#"{{"id":"p{i}","name":"p{i}"}}"#))
        .collect();
    let json = format!(r#"{{"props":[{}]}}"#, items.join(","));
    let set = TerrainPropSet::from_json_str(&json).expect("読めること");
    assert_eq!(
        set.props.len(),
        TERRAIN_MAX_PROPS,
        "上限で切り詰められていない"
    );
    assert_eq!(set.active_count(), TERRAIN_MAX_PROPS);
    // 先頭側が残ること（末尾から削られていない）。
    assert_eq!(set.props[0].id, "p0");
}

/// props が空の JSON が既定セットへフォールバックすることを保証する。
/// （props.json を空にしたときに地形が丸裸になる回帰を防ぐ）
#[test]
fn empty_props_falls_back_to_default_set() {
    let set = TerrainPropSet::from_json_str(r#"{"props":[]}"#).expect("読めること");
    assert!(
        !set.props.is_empty(),
        "空 JSON が既定へフォールバックしていない"
    );
    assert!(
        set.find_by_id("grass_field").is_some(),
        "既定の草プロップが無い"
    );
    assert!(
        set.find_by_id("tree_pine").is_some(),
        "既定の木プロップが無い"
    );
}

/// 既定セットの中身（種別・密度・草の可視性）が意図どおりであることを保証する。
/// （既定を弄って「平地に草が生えない」状態になる回帰を防ぐ）
#[test]
fn default_set_produces_visible_grass_on_flat_ground() {
    let set = TerrainPropSet::default();
    let (idx, grass) = set.find_by_id("grass_field").expect("既定の草プロップ");
    assert_eq!(
        idx, 0,
        "草プロップは先頭にあること（既定 prop_id=0 の前提）"
    );
    assert_eq!(grass.kind, PropKind::Grass);
    assert!(grass.scatter.density >= 1.0, "既定の草密度が疎すぎる");

    // 平地（斜度 0）・高さ 0・レイヤ "grass" 重み 1 で確率が十分に高いこと。
    let p = grass.rule.evaluate(0.0, 0.0, &|_| 1.0);
    assert!(p > 0.9, "平地で草が生える確率が低すぎる: {p}");

    let (_, tree) = set.find_by_id("tree_pine").expect("既定の木プロップ");
    assert_eq!(tree.kind, PropKind::Model);
    assert!(
        tree.scatter.density < grass.scatter.density,
        "木が草より密なのはおかしい"
    );
    assert!(
        !tree.scatter.align_to_normal,
        "木は地表法線に沿わせない想定"
    );
}

/// find_by_id が未知 ID に None を返すことを保証する。
/// （存在しない ID でパニックする回帰を防ぐ）
#[test]
fn find_by_id_returns_none_for_unknown() {
    assert!(
        TerrainPropSet::default()
            .find_by_id("no_such_prop")
            .is_none()
    );
}

/// 草の分割数が 1..=GRASS_MAX_SEGMENTS へ丸められることを保証する。
/// （props.json に 0 や巨大値を書かれて頂点バッファが破綻する回帰を防ぐ）
#[test]
fn grass_segments_are_clamped() {
    let json = r#"{"props":[{"id":"a","name":"a","grass":{"segments":0}},
                            {"id":"b","name":"b","grass":{"segments":9999}}]}"#;
    let set = TerrainPropSet::from_json_str(json).expect("読めること");
    assert_eq!(set.props[0].grass.clamped_segments(), 1);
    assert_eq!(set.props[1].grass.clamped_segments(), GRASS_MAX_SEGMENTS);
}

// ============================================================
//  4. ScatterRule::evaluate
// ============================================================

/// 窓の内側で確率 1、外側で 0 になることを保証する。
/// （台形ウィンドウの内外判定が反転する回帰を防ぐ）
#[test]
fn rule_evaluate_inside_is_one_outside_is_zero() {
    let rule = ScatterRule {
        slope_min_deg: 10.0,
        slope_max_deg: 30.0,
        slope_fade_deg: 0.0,
        height_min: 0.0,
        height_max: 10.0,
        height_fade: 0.0,
        layer_conditions: Vec::new(),
        threshold: 0.0,
    };
    let any = |_: &str| 1.0;
    assert!(
        (rule.evaluate(20.0, 5.0, &any) - 1.0).abs() < PROB_EPS,
        "窓の内側が 1 でない"
    );
    assert!(
        rule.evaluate(40.0, 5.0, &any) < PROB_EPS,
        "斜度が窓の外なのに 0 でない"
    );
    assert!(
        rule.evaluate(20.0, 50.0, &any) < PROB_EPS,
        "高度が窓の外なのに 0 でない"
    );
}

/// フェード領域が単調増加であることを保証する。
/// （フェード式の符号ミスで境界が逆勾配になる回帰を防ぐ）
#[test]
fn rule_evaluate_fade_edge_is_monotonic() {
    /// フェード区間を刻むサンプル数。
    const STEPS: u32 = 16;
    let rule = ScatterRule {
        slope_min_deg: 0.0,
        slope_max_deg: 30.0,
        slope_fade_deg: 10.0,
        height_fade: 0.0,
        threshold: 0.0,
        ..ScatterRule::default()
    };
    let any = |_: &str| 1.0;
    // 斜度 30→40 度（立ち下がりフェード）で確率が単調に減ること。
    let mut prev = f32::INFINITY;
    for i in 0..=STEPS {
        let slope = 30.0 + 10.0 * (i as f32 / STEPS as f32);
        let p = rule.evaluate(slope, 0.0, &any);
        assert!(
            p <= prev + PROB_EPS,
            "フェードが単調減少していない slope={slope} p={p}"
        );
        prev = p;
    }
    assert!(prev < PROB_EPS, "フェード終端で 0 に落ちていない: {prev}");
}

/// レイヤ条件が重み不足の場所で散布を止めることを保証する。
/// （レイヤ条件が無視されて岩場に草が生える回帰を防ぐ）
#[test]
fn rule_evaluate_layer_condition_gates() {
    let rule = ScatterRule {
        slope_fade_deg: 0.0,
        height_fade: 0.0,
        threshold: 0.0,
        layer_conditions: vec![LayerCondition {
            layer: "grass".to_string(),
            min_weight: 0.5,
        }],
        ..ScatterRule::default()
    };
    // 重み十分 → 1。
    assert!((rule.evaluate(0.0, 0.0, &|_| 1.0) - 1.0).abs() < PROB_EPS);
    // 重み 0 → 完全に不成立。
    assert!(rule.evaluate(0.0, 0.0, &|_| 0.0) < PROB_EPS);
    // 要求値ちょうど → 成立（フェードは min_weight より下側に張る規約）。
    assert!((rule.evaluate(0.0, 0.0, &|_| 0.5) - 1.0).abs() < PROB_EPS);
}

/// 複数のレイヤ条件が AND で合成されることを保証する。
/// （片方しか見ない実装ミスの回帰を防ぐ）
#[test]
fn rule_evaluate_multiple_layer_conditions_are_anded() {
    let rule = ScatterRule {
        slope_fade_deg: 0.0,
        height_fade: 0.0,
        threshold: 0.0,
        layer_conditions: vec![
            LayerCondition {
                layer: "grass".to_string(),
                min_weight: 0.5,
            },
            LayerCondition {
                layer: "rock".to_string(),
                min_weight: 0.5,
            },
        ],
        ..ScatterRule::default()
    };
    // "grass" だけ重みがあり "rock" は 0 → 全体は 0。
    let only_grass = |name: &str| if name == "grass" { 1.0 } else { 0.0 };
    assert!(
        rule.evaluate(0.0, 0.0, &only_grass) < PROB_EPS,
        "AND 合成になっていない"
    );
    // 両方あれば 1。
    assert!((rule.evaluate(0.0, 0.0, &|_| 1.0) - 1.0).abs() < PROB_EPS);
}

/// threshold 未満の確率が 0 に落とされることを保証する。
/// （裾の 1 本だけ生える個体が広範囲に散らばる回帰を防ぐ）
#[test]
fn rule_evaluate_threshold_cuts_tail() {
    let rule = ScatterRule {
        slope_min_deg: 0.0,
        slope_max_deg: 30.0,
        slope_fade_deg: 10.0,
        height_fade: 0.0,
        threshold: 0.5,
        ..ScatterRule::default()
    };
    let any = |_: &str| 1.0;
    // フェードの終盤（確率が 0.5 未満になる領域）は完全に 0 になる。
    let p = rule.evaluate(38.0, 0.0, &any);
    assert!(p < PROB_EPS, "threshold で切られていない: {p}");
    // 窓の中央は影響を受けない。
    assert!((rule.evaluate(10.0, 0.0, &any) - 1.0).abs() < PROB_EPS);
}

// ============================================================
//  5. surface_hit_down
// ============================================================

/// 水平面で接地点 Y が面の高さに一致し、法線が +Y になることを保証する。
///
/// これが本モジュールの **法線符号規約**（外向き = +勾配）の基準テストである。
/// 負勾配にすると法線が [0,-1,0] になり、ここで落ちる。
#[test]
fn surface_hit_on_flat_plane_returns_height_and_up_normal() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let (hit, normal) = surface_hit_down(&field, 1.0, 2.0, 16.0, 0.0).expect("平面に当たること");
    assert!(
        (hit[1] - TEST_GROUND_HEIGHT).abs() < HIT_Y_EPS,
        "接地 Y がずれている: {}",
        hit[1]
    );
    assert!(
        (hit[0] - 1.0).abs() < NORMAL_EPS && (hit[2] - 2.0).abs() < NORMAL_EPS,
        "XZ が動いた"
    );
    assert!(normal[1] > 0.0, "法線が下向き（符号規約違反）: {normal:?}");
    assert!((normal[0]).abs() < NORMAL_EPS, "水平面なのに X 成分がある");
    assert!(
        (normal[1] - 1.0).abs() < NORMAL_EPS,
        "水平面の法線 Y が 1 でない: {}",
        normal[1]
    );
    assert!((normal[2]).abs() < NORMAL_EPS, "水平面なのに Z 成分がある");
}

/// 地面が無い柱で None を返すことを保証する。
/// （空中に草が生える回帰を防ぐ）
#[test]
fn surface_hit_returns_none_without_ground() {
    let field = TestField::empty();
    assert!(surface_hit_down(&field, 0.0, 0.0, 16.0, 0.0).is_none());
}

/// 探索範囲より下にある地面を拾わないことを保証する。
/// （範囲外の地面に吸い付く回帰を防ぐ）
#[test]
fn surface_hit_respects_search_range() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    // 地面（Y=4）より上だけを探索 → 当たらない。
    assert!(surface_hit_down(&field, 0.0, 0.0, 16.0, 8.0).is_none());
    // 範囲に含めれば当たる。
    assert!(surface_hit_down(&field, 0.0, 0.0, 16.0, 0.0).is_some());
}

/// 傾いた面で斜度が期待どおりに出ることを保証する。
/// （法線から斜度を出す経路が壊れると散布ルールが全部おかしくなる）
#[test]
fn surface_hit_on_slope_yields_expected_tilt() {
    // dh/dx = 1 → 45 度の斜面。密度勾配は (-1, 1, 0)/√2。
    let field = TestField::sloped(TEST_GROUND_HEIGHT, 1.0);
    let (_, normal) = surface_hit_down(&field, 0.0, 0.0, 16.0, 0.0).expect("斜面に当たること");
    /// 45 度斜面の法線成分の期待値（1/√2）。
    const INV_SQRT2: f32 = std::f32::consts::FRAC_1_SQRT_2;
    assert!(
        (normal[1] - INV_SQRT2).abs() < NORMAL_EPS,
        "斜面法線 Y が想定外: {}",
        normal[1]
    );
    assert!(
        (normal[0] + INV_SQRT2).abs() < NORMAL_EPS,
        "斜面法線 X が想定外: {}",
        normal[0]
    );
    assert!(normal[1] > 0.0, "斜面でも法線は上向きであるべき");
}

/// 探索開始点が地中でも、その面を誤って採用しないことを保証する。
/// （カメラが地中にあるときに天井へ草が生える回帰を防ぐ）
#[test]
fn surface_hit_does_not_accept_starting_inside_solid() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    // 開始点 Y=2（地中）から下へ探索 → AIR→SOLID 遷移が無いので None。
    assert!(surface_hit_down(&field, 0.0, 0.0, 2.0, 0.0).is_none());
}

// ============================================================
//  6. 決定性
// ============================================================

/// 同一入力での自動散布がビット単位で同一結果になることを保証する。
/// （並列化や HashMap 走査順の混入で草原が毎回変わる回帰を防ぐ）
#[test]
fn scatter_is_bit_deterministic() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(COUNT_TEST_DENSITY);
    let coord = ChunkCoord::new(0, 0, 0);
    let a = scatter_chunk_by_rules(&field, &props, &[0], coord, TEST_SEED_A);
    let b = scatter_chunk_by_rules(&field, &props, &[0], coord, TEST_SEED_A);
    assert!(!a.is_empty(), "テスト前提: 何かしら生えていること");
    assert!(
        bit_identical(&a, &b),
        "同一入力なのに結果がビット一致しない"
    );
}

/// グローバルシードを変えると結果が変わることを保証する。
/// （シードが無視されて常に同じ配置になる回帰を防ぐ）
#[test]
fn scatter_differs_with_global_seed() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(COUNT_TEST_DENSITY);
    let coord = ChunkCoord::new(0, 0, 0);
    let a = scatter_chunk_by_rules(&field, &props, &[0], coord, TEST_SEED_A);
    let b = scatter_chunk_by_rules(&field, &props, &[0], coord, TEST_SEED_B);
    assert!(!bit_identical(&a, &b), "シードを変えても結果が同じ");
}

/// チャンク座標を変えると（同じ形状でも）配置が変わることを保証する。
/// （隣り合うチャンクで同じ模様が繰り返される回帰を防ぐ）
#[test]
fn scatter_differs_with_chunk_coord() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(COUNT_TEST_DENSITY);
    let a = scatter_chunk_by_rules(&field, &props, &[0], ChunkCoord::new(0, 0, 0), TEST_SEED_A);
    let b = scatter_chunk_by_rules(&field, &props, &[0], ChunkCoord::new(1, 0, 0), TEST_SEED_A);
    // 位置はチャンク原点ぶんずれるので、ジッタ量（小数部）が異なることを見る。
    let frac = |v: &[ScatterInstance]| -> Vec<u32> { v.iter().map(|i| i.seed).collect() };
    assert_ne!(frac(&a), frac(&b), "チャンク座標を変えても乱数列が同じ");
}

/// hash_seed が 4 つの入力すべてに反応することを保証する。
/// （どれか 1 つが混ぜ忘れられて模様が繰り返す回帰を防ぐ）
#[test]
fn hash_seed_reacts_to_every_input() {
    let base = hash_seed(1, ChunkCoord::new(1, 2, 3), 4, 5);
    assert_ne!(
        base,
        hash_seed(2, ChunkCoord::new(1, 2, 3), 4, 5),
        "global_seed が効いていない"
    );
    assert_ne!(
        base,
        hash_seed(1, ChunkCoord::new(9, 2, 3), 4, 5),
        "coord.x が効いていない"
    );
    assert_ne!(
        base,
        hash_seed(1, ChunkCoord::new(1, 9, 3), 4, 5),
        "coord.y が効いていない"
    );
    assert_ne!(
        base,
        hash_seed(1, ChunkCoord::new(1, 2, 9), 4, 5),
        "coord.z が効いていない"
    );
    assert_ne!(
        base,
        hash_seed(1, ChunkCoord::new(1, 2, 3), 9, 5),
        "prop_index が効いていない"
    );
    assert_ne!(
        base,
        hash_seed(1, ChunkCoord::new(1, 2, 3), 4, 9),
        "cell_index が効いていない"
    );
}

/// ScatterRng の next_f32 が [0,1) に収まることを保証する。
/// （1.0 が返ると「確率 1 でも落ちる候補」が生まれて密度が目減りする）
#[test]
fn rng_next_f32_is_in_unit_interval() {
    /// サンプル数。
    const SAMPLES: u32 = 10_000;
    let mut rng = ScatterRng::new(TEST_SEED_A);
    for _ in 0..SAMPLES {
        let v = rng.next_f32();
        assert!((0.0..1.0).contains(&v), "next_f32 が範囲外: {v}");
    }
}

// ============================================================
//  7. 密度→本数・ブラシ
// ============================================================

/// 密度 D・チャンク幅 E の全面採用ルールで、本数が D*E*E の許容帯に入ることを保証する。
///
/// グリッドは cells = ceil(sqrt(D)*E) なので、本数は必ず D*E² 以上・
/// 切り上げぶんだけ多くなる。帯はその性質そのものを表しており、
/// 実装のバグを隠すために広げたものではない（D=0.25/E=16 では
/// sqrt(D)*E=8 がちょうど整数なので、実際には比が厳密に 1.0 になる）。
#[test]
fn scatter_count_matches_density_within_band() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(COUNT_TEST_DENSITY);
    let extent = field.settings.chunk_extent();
    let insts = scatter_chunk_by_rules(&field, &props, &[0], ChunkCoord::new(0, 0, 0), TEST_SEED_A);

    let expected = COUNT_TEST_DENSITY * extent * extent;
    let ratio = insts.len() as f32 / expected;
    assert!(
        (COUNT_BAND_LO..=COUNT_BAND_HI).contains(&ratio),
        "本数 {} が期待 {expected} の帯 [{COUNT_BAND_LO}, {COUNT_BAND_HI}] を外れた（比 {ratio}）",
        insts.len()
    );
}

/// 生成されたインスタンスがチャンクの XZ 範囲内に収まることを保証する。
/// （ジッタがセルからはみ出して隣チャンクへ漏れる回帰を防ぐ）
#[test]
fn scatter_instances_stay_inside_chunk_footprint() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(COUNT_TEST_DENSITY);
    let coord = ChunkCoord::new(2, 0, -1);
    let origin = coord.world_origin(&field.settings);
    let extent = field.settings.chunk_extent();
    for inst in scatter_chunk_by_rules(&field, &props, &[0], coord, TEST_SEED_A) {
        assert!(
            inst.pos[0] >= origin[0] && inst.pos[0] < origin[0] + extent,
            "X がチャンク外: {}",
            inst.pos[0]
        );
        assert!(
            inst.pos[2] >= origin[2] && inst.pos[2] < origin[2] + extent,
            "Z がチャンク外: {}",
            inst.pos[2]
        );
    }
}

/// ルールが不成立なら 1 本も生えないことを保証する。
/// （確率判定を素通りする回帰を防ぐ）
#[test]
fn scatter_produces_nothing_when_rule_rejects() {
    // レイヤ重み 0 の場に、レイヤ条件付きのプロップを撒く。
    let field = TestField::flat(TEST_GROUND_HEIGHT).with_layer_weight(0.0);
    let mut props = always_prop(COUNT_TEST_DENSITY);
    props.props[0].rule.layer_conditions = vec![LayerCondition {
        layer: "grass".to_string(),
        min_weight: 0.5,
    }];
    let insts = scatter_chunk_by_rules(&field, &props, &[0], ChunkCoord::new(0, 0, 0), TEST_SEED_A);
    assert!(
        insts.is_empty(),
        "ルール不成立なのに {} 本生えた",
        insts.len()
    );
}

/// 範囲外のプロップ添字を指定してもパニックせず無視されることを保証する。
/// （エンジン層の指定ミスでランタイムが落ちる回帰を防ぐ）
#[test]
fn scatter_ignores_out_of_range_prop_index() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(COUNT_TEST_DENSITY);
    let insts =
        scatter_chunk_by_rules(&field, &props, &[99], ChunkCoord::new(0, 0, 0), TEST_SEED_A);
    assert!(insts.is_empty());
}

/// 極端な密度指定がグリッド上限で頭打ちになることを保証する。
/// （props.json の誤入力で 1 チャンク生成が事実上フリーズする回帰を防ぐ）
#[test]
fn scatter_grid_is_clamped_for_absurd_density() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    // 1 m² に 100 万本という非現実的な指定。
    let props = always_prop(1.0e6);
    let insts = scatter_chunk_by_rules(&field, &props, &[0], ChunkCoord::new(0, 0, 0), TEST_SEED_A);
    let cap = (MAX_SCATTER_GRID_PER_AXIS * MAX_SCATTER_GRID_PER_AXIS) as usize;
    assert!(
        insts.len() <= cap,
        "グリッド上限 {cap} を超えた: {}",
        insts.len()
    );
    assert_eq!(
        insts.len(),
        cap,
        "上限に張り付いていない（全採用ルールなので一致するはず）"
    );
}

/// ブラシ追加がブラシ半径の内側にだけインスタンスを置くことを保証する。
/// （外接正方形の角に草が漏れる回帰を防ぐ）
#[test]
fn brush_adds_only_inside_radius() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(BRUSH_DENSITY);
    let center = [0.0, TEST_GROUND_HEIGHT, 0.0];
    let mut insts = Vec::new();
    let changed = scatter_brush(
        &field,
        &props,
        0,
        &mut insts,
        center,
        BRUSH_RADIUS,
        BRUSH_DENSITY,
        false,
        TEST_SEED_A,
    );

    assert!(changed, "ブラシで何も追加されなかった");
    assert!(!insts.is_empty());
    for inst in &insts {
        let dx = inst.pos[0] - center[0];
        let dz = inst.pos[2] - center[2];
        let d = (dx * dx + dz * dz).sqrt();
        assert!(
            d <= BRUSH_RADIUS + RADIUS_EPS,
            "半径 {BRUSH_RADIUS} の外に置かれた: {d}"
        );
    }
}

/// ブラシ消去が半径の内側だけを取り除くことを保証する。
/// （外側の草まで巻き添えで消える回帰を防ぐ）
#[test]
fn brush_erase_removes_only_inside_radius() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(BRUSH_DENSITY);
    let center = [0.0, TEST_GROUND_HEIGHT, 0.0];
    let mut insts = vec![
        instance([0.0, TEST_GROUND_HEIGHT, 0.0], 0, 1), // 中心（消える）
        instance([2.0, TEST_GROUND_HEIGHT, 0.0], 0, 2), // 半径内（消える）
        instance([10.0, TEST_GROUND_HEIGHT, 0.0], 0, 3), // 半径外（残る）
    ];
    let changed = scatter_brush(
        &field,
        &props,
        0,
        &mut insts,
        center,
        BRUSH_RADIUS,
        BRUSH_DENSITY,
        true,
        TEST_SEED_A,
    );

    assert!(changed, "消去が変化なし扱いになった");
    assert_eq!(insts.len(), 1, "消去件数が想定外");
    assert_eq!(insts[0].seed, 3, "残ったのが半径外の個体ではない");
}

/// 消去が「半径内のすべてのプロップ種」を対象にすることを保証する（設計判断）。
/// （消しゴムをかけたのに別種だけ残る、という直感に反する挙動の回帰を防ぐ）
#[test]
fn brush_erase_removes_all_prop_kinds() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(BRUSH_DENSITY);
    let center = [0.0, TEST_GROUND_HEIGHT, 0.0];
    // prop_id が異なる 2 件を半径内に置く。
    let mut insts = vec![
        instance([0.0, TEST_GROUND_HEIGHT, 0.0], 0, 1),
        instance([1.0, TEST_GROUND_HEIGHT, 0.0], 7, 2),
    ];
    scatter_brush(
        &field,
        &props,
        0,
        &mut insts,
        center,
        BRUSH_RADIUS,
        BRUSH_DENSITY,
        true,
        TEST_SEED_A,
    );
    assert!(insts.is_empty(), "prop_index 以外の種が消し残った");
}

/// 何も無い場所での消去が「変化なし」を返すことを保証する。
/// （空振りの消去で undo スタックが汚れる回帰を防ぐ）
#[test]
fn brush_erase_on_empty_reports_no_change() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(BRUSH_DENSITY);
    let mut insts: Vec<ScatterInstance> = Vec::new();
    let changed = scatter_brush(
        &field,
        &props,
        0,
        &mut insts,
        [0.0, TEST_GROUND_HEIGHT, 0.0],
        BRUSH_RADIUS,
        BRUSH_DENSITY,
        true,
        TEST_SEED_A,
    );
    assert!(!changed, "空リストの消去が変化ありと報告された");
}

/// 同じ場所を 2 度塗っても最小間隔判定でほぼ増えないことを保証する。
/// （同一箇所に草が無限に積み上がる回帰を防ぐ）
#[test]
fn brush_min_spacing_rejects_stacking() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(BRUSH_DENSITY);
    let center = [0.0, TEST_GROUND_HEIGHT, 0.0];
    let mut insts = Vec::new();

    scatter_brush(
        &field,
        &props,
        0,
        &mut insts,
        center,
        BRUSH_RADIUS,
        BRUSH_DENSITY,
        false,
        TEST_SEED_A,
    );
    let after_first = insts.len();
    assert!(after_first > 0, "1 回目で何も生えていない");

    // まったく同じストロークを繰り返す → 全候補が最小間隔に引っかかる。
    let changed = scatter_brush(
        &field,
        &props,
        0,
        &mut insts,
        center,
        BRUSH_RADIUS,
        BRUSH_DENSITY,
        false,
        TEST_SEED_A,
    );
    assert!(!changed, "同一ストロークの重ね塗りで追加が発生した");
    assert_eq!(insts.len(), after_first, "重ね塗りで本数が増えた");
}

/// ブラシ追加後の全インスタンスが最小間隔を満たすことを保証する。
/// （1 ストローク内でも密集が起きる回帰を防ぐ）
#[test]
fn brush_result_satisfies_min_spacing() {
    use super::generate::MIN_INSTANCE_SPACING_FACTOR;
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(BRUSH_DENSITY);
    let mut insts = Vec::new();
    scatter_brush(
        &field,
        &props,
        0,
        &mut insts,
        [0.0, TEST_GROUND_HEIGHT, 0.0],
        BRUSH_RADIUS,
        BRUSH_DENSITY,
        false,
        TEST_SEED_A,
    );
    let min_spacing = MIN_INSTANCE_SPACING_FACTOR / BRUSH_DENSITY.sqrt();
    for i in 0..insts.len() {
        for j in (i + 1)..insts.len() {
            let dx = insts[i].pos[0] - insts[j].pos[0];
            let dz = insts[i].pos[2] - insts[j].pos[2];
            let d = (dx * dx + dz * dz).sqrt();
            assert!(
                d >= min_spacing - RADIUS_EPS,
                "最小間隔 {min_spacing} 未満の対がある: {d}"
            );
        }
    }
}

/// 半径が非正のブラシが何もしないことを保証する。
/// （0 除算やゼロ幅グリッドで無限ループする回帰を防ぐ）
#[test]
fn brush_with_non_positive_radius_does_nothing() {
    let field = TestField::flat(TEST_GROUND_HEIGHT);
    let props = always_prop(BRUSH_DENSITY);
    let mut insts = vec![instance([0.0, TEST_GROUND_HEIGHT, 0.0], 0, 1)];
    let changed = scatter_brush(
        &field,
        &props,
        0,
        &mut insts,
        [0.0, TEST_GROUND_HEIGHT, 0.0],
        0.0,
        BRUSH_DENSITY,
        true,
        TEST_SEED_A,
    );
    assert!(!changed);
    assert_eq!(insts.len(), 1, "半径 0 の消去で個体が消えた");
}

// ============================================================
//  8. restick_instances
// ============================================================

/// 地面を持ち上げたときにインスタンスが新しい高さへ追従することを保証する。
/// （地形編集後に草が地中へ埋まる／宙に浮く回帰を防ぐ）
#[test]
fn restick_follows_raised_ground() {
    let props = always_prop(BRUSH_DENSITY);
    let mut insts = vec![instance([1.0, TEST_GROUND_HEIGHT, 2.0], 0, 1)];

    // 地面を RESTICK_RAISE だけ持ち上げた場を用意する。
    let raised = TestField::flat(TEST_GROUND_HEIGHT + RESTICK_RAISE);
    let removed = restick_instances(&raised, &props, &mut insts, RESTICK_SEARCH);

    assert_eq!(removed, 0, "追従できるはずのインスタンスが消された");
    assert_eq!(insts.len(), 1);
    let y = insts[0].pos[1];
    assert!(
        (y - (TEST_GROUND_HEIGHT + RESTICK_RAISE)).abs() < HIT_Y_EPS,
        "新しい地表高へ追従していない: {y}"
    );
    assert!(insts[0].normal[1] > 0.0, "再接地後の法線が下向き");
}

/// 地面が消えたときにインスタンスが削除され、件数が返ることを保証する。
/// （地面を掘り抜いた跡に草が浮き続ける回帰を防ぐ）
#[test]
fn restick_deletes_instances_without_ground() {
    let props = always_prop(BRUSH_DENSITY);
    let mut insts = vec![
        instance([0.0, TEST_GROUND_HEIGHT, 0.0], 0, 1),
        instance([1.0, TEST_GROUND_HEIGHT, 0.0], 0, 2),
    ];
    let removed = restick_instances(&TestField::empty(), &props, &mut insts, RESTICK_SEARCH);
    assert_eq!(removed, 2, "削除件数が実際と合わない");
    assert!(insts.is_empty(), "地面が無いのにインスタンスが残った");
}

/// 生き残ったインスタンスの相対順序が保たれることを保証する。
/// （順序が入れ替わると保存データの差分が毎回発生する）
#[test]
fn restick_keeps_order_of_survivors() {
    let props = always_prop(BRUSH_DENSITY);
    // 探索範囲を狭くし、地面から遠い個体だけが落ちるようにする。
    let mut insts = vec![
        instance([0.0, TEST_GROUND_HEIGHT, 0.0], 0, 10),
        // 地面から 100m 上 → 探索半幅 RESTICK_SEARCH では届かず削除される。
        instance([1.0, TEST_GROUND_HEIGHT + 100.0, 0.0], 0, 20),
        instance([2.0, TEST_GROUND_HEIGHT, 0.0], 0, 30),
    ];
    let removed = restick_instances(
        &TestField::flat(TEST_GROUND_HEIGHT),
        &props,
        &mut insts,
        RESTICK_SEARCH,
    );
    assert_eq!(removed, 1);
    let seeds: Vec<u32> = insts.iter().map(|i| i.seed).collect();
    assert_eq!(seeds, vec![10, 30], "生存個体の順序が保たれていない");
}

/// プロップ定義が減ったときの孤児インスタンスが掃除されることを保証する。
/// （範囲外 prop_id で描画側が添字外アクセスする回帰を防ぐ）
#[test]
fn restick_removes_orphan_prop_ids() {
    let props = always_prop(BRUSH_DENSITY); // プロップは 1 種（添字 0）のみ
    let mut insts = vec![
        instance([0.0, TEST_GROUND_HEIGHT, 0.0], 0, 1),
        instance([1.0, TEST_GROUND_HEIGHT, 0.0], 5, 2), // 存在しない添字
    ];
    let removed = restick_instances(
        &TestField::flat(TEST_GROUND_HEIGHT),
        &props,
        &mut insts,
        RESTICK_SEARCH,
    );
    assert_eq!(removed, 1);
    assert_eq!(insts.len(), 1);
    assert_eq!(insts[0].prop_id, 0);
}

/// 再接地の結果が .tscatter を往復してもビット一致することを保証する。
/// （再接地→保存→読み込みで微妙にズレる回帰を防ぐ）
#[test]
fn restick_result_survives_tscatter_roundtrip() {
    let props = always_prop(BRUSH_DENSITY);
    let field = TestField::flat(TEST_GROUND_HEIGHT + RESTICK_RAISE);
    let mut insts = vec![instance([1.0, TEST_GROUND_HEIGHT, 2.0], 0, 1)];
    restick_instances(&field, &props, &mut insts, RESTICK_SEARCH);

    let coord = ChunkCoord::new(0, 0, 0);
    let (got, _) = read_chunk(&write_chunk(&insts, coord)).expect("読めること");
    assert!(
        bit_identical(&insts, &got),
        "再接地結果の往復がビット一致しない"
    );
}
