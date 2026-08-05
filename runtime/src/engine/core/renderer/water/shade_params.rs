// ============================================================
//  water/shade_params.rs — 水面シェーディングアセットの「パラメータ注釈」解析（Phase W8.2）
//
//  ## 役割（単一責任）
//  ユーザーが書いた `.wgsl`（水面シェーディングアセット）の中に置かれた
//  **解析用アノテーション行**を読み取り、「インスペクタに出す 1 行ぶんの宣言」の
//  リストへ変換する。ファイル I/O も GPU も触らない**純粋関数**の集まりであり、
//  この 1 ファイルが構文の正典である（C# 側は解析を持たず、ランタイムが送る
//  JSON を表示するだけ）。
//
//  ## 構文（アセット内のコメント行）
//  ```wgsl
//  //! param color  emission_color = (1.0, 0.4, 0.1)   // 発光色
//  //! param range(0.0, 10.0) crack_speed = 1.5        // 亀裂の流速
//  //! param float  glow_boost = 2.0                    // 発光の強さ
//  ```
//    ・`color`            … vec3。インスペクタはカラーピッカーを出す。既定値は `(r, g, b)`。
//    ・`range(min, max)`  … f32。インスペクタはスライダーを出す。既定値は数値 1 個。
//    ・`float`            … f32。インスペクタは数値行を出す。既定値は数値 1 個。
//  行末の `// …` は**インスペクタの表示ラベル**（省略時は識別子そのもの）。
//
//  ## 宣言した名前は WGSL からそのまま使える
//  エンジンは宣言 1 個につき `var<private> <名前>: <型>;` を生成してアセット本体の
//  **前**へ連結し、`water_shade_entry`（生成ディスパッチ）の先頭で
//  ストレージバッファの値を代入する。したがってアセット作者は
//  「宣言を 1 行書けば `water_shade` の中でその名前を値として読める」だけでよく、
//  バインディングもインデックスも一切知らなくてよい。
//  （WGSL のモジュールスコープに `let` は書けず、`const` は実行時の値を持てないため、
//    `var<private>` ＋ エントリでの代入という形を採っている）
//
//  ## 上限
//  1 アセットあたり `WATER_SHADE_PARAM_MAX` 個まで。超過ぶんは**黙って切らず**、
//  警告メッセージ（`warnings`）に理由を残したうえで無視する。
//
//  ## 依存
//  なし（標準ライブラリのみ）。消費側は `water/shading_asset.rs`（生成）・
//  `water/mod.rs`（GPU 転送）・`app/component_ops.rs`（インスペクタへの JSON）。
// ============================================================

use std::collections::BTreeMap;

// ============================================================
//  定数（マジックナンバーを名前で持つ）
// ============================================================

/// 1 アセットが宣言できるパラメータの最大個数。
///
/// GPU 側は 1 インスタンスあたり `vec4` をこの本数ぶん持つ固定長ブロックで運ぶ
/// （`water_shade_params.wgsl` の `WaterShadeParamBlock`）。増やすときは
/// WGSL 側の配列長も同時に直すこと（一致はテストが固定する）。
pub const WATER_SHADE_PARAM_MAX: usize = 8;

/// アノテーション行の先頭マーカー（行を trim した後の接頭辞）。
///
/// 通常のコメント `//` と区別するために `//!` を使う。さらに直後のキーワード
/// `param` まで一致した行だけを解析対象にするので、`//!` で始まる普通の
/// ドキュメントコメントを誤って拾うことはない。
const ANNOTATION_PREFIX: &str = "//!";

/// アノテーションの種別キーワード（`//! param ...`）。
const ANNOTATION_KEYWORD: &str = "param";

/// 型指定 `color`（vec3・カラーピッカー）。
const TYPE_COLOR: &str = "color";
/// 型指定 `float`（f32・数値行）。
const TYPE_FLOAT: &str = "float";
/// 型指定 `range(min, max)`（f32・スライダー）の接頭辞。
const TYPE_RANGE_PREFIX: &str = "range";

/// 行末ラベルコメントの区切り。
const LABEL_COMMENT: &str = "//";

/// `color` の既定値リテラルの開き括弧。
const TUPLE_OPEN: char = '(';
/// `color` の既定値リテラルの閉じ括弧。
const TUPLE_CLOSE: char = ')';
/// タプル要素の区切り。
const TUPLE_SEPARATOR: char = ',';
/// `color` の既定値の要素数（RGB）。
const COLOR_COMPONENT_COUNT: usize = 3;
/// `range(min, max)` の引数の個数。
const RANGE_ARG_COUNT: usize = 2;

/// GPU へ運ぶ 1 パラメータぶんの成分数（vec4 固定）。
pub const PARAM_VALUE_COMPONENTS: usize = 4;

/// エンジンが使う識別子の予約接頭辞。
///
/// これらで始まる名前を宣言されると、生成した `var<private>` が
/// 契約側の関数・定数と衝突して WGSL のコンパイルエラーになる。
/// 「アセット内の意味不明なエラー」より「その行を無視して警告」の方が親切なので、
/// 解析段階で弾く。
const RESERVED_NAME_PREFIXES: [&str; 3] = ["water_shade", "u_water", "WATER_"];

// ============================================================
//  データ型
// ============================================================

/// パラメータの種別（インスペクタの行の作り方＝UI の形も決める）。
#[derive(Clone, Copy, PartialEq, Debug)]
pub enum WaterShadeParamKind {
    /// リニア RGB の色（WGSL 型 `vec3<f32>`）。インスペクタはカラーピッカー。
    Color,
    /// 範囲つきスカラー（WGSL 型 `f32`）。インスペクタはスライダー。
    Range {
        /// スライダーの下限。
        min: f32,
        /// スライダーの上限。
        max: f32,
    },
    /// 範囲なしスカラー（WGSL 型 `f32`）。インスペクタは数値行。
    Float,
}

impl WaterShadeParamKind {
    /// インスペクタへ送る種別文字列（C# 側の分岐キーと一致させるワイヤ契約）。
    pub fn as_str(self) -> &'static str {
        match self {
            WaterShadeParamKind::Color    => TYPE_COLOR,
            WaterShadeParamKind::Range { .. } => TYPE_RANGE_PREFIX,
            WaterShadeParamKind::Float    => TYPE_FLOAT,
        }
    }

    /// 生成する `var<private>` の WGSL 型名。
    pub fn wgsl_type(self) -> &'static str {
        match self {
            WaterShadeParamKind::Color => "vec3<f32>",
            _                          => "f32",
        }
    }

    /// ストレージの `vec4` から値を取り出す WGSL の swizzle（`.xyz` / `.x`）。
    pub fn wgsl_swizzle(self) -> &'static str {
        match self {
            WaterShadeParamKind::Color => "xyz",
            _                          => "x",
        }
    }
}

/// アセットが宣言した 1 パラメータ。
#[derive(Clone, PartialEq, Debug)]
pub struct WaterShadeParamDecl {
    /// WGSL の識別子（アセット内でそのまま値として参照できる名前）。
    /// シーンへ保存する値のキーでもある。
    pub name: String,
    /// 種別（UI の形と WGSL の型を決める）。
    pub kind: WaterShadeParamKind,
    /// 既定値（アセット側が書いた値）。シーンに保存値が無ければこれが使われる。
    /// Color は `[r, g, b, 0]`、Float/Range は `[v, 0, 0, 0]`。
    pub default: [f32; PARAM_VALUE_COMPONENTS],
    /// インスペクタの表示ラベル（行末コメント。無ければ `name` と同じ）。
    pub label: String,
}

/// 解析中に見つかった問題 1 件。
///
/// **エラーにはしない**（アセット自体は動くべき）が、黙って捨てもしない。
/// 行番号を構造として持つのは、エディタの診断（アセット内の該当行を指す）へ
/// そのまま渡せるようにするためである。
#[derive(Clone, PartialEq, Debug)]
pub struct WaterShadeParamWarning {
    /// アセット内の行番号（1 始まり）。
    pub line: usize,
    /// 人間向けの説明（行番号は含まない）。
    pub message: String,
}

/// 1 アセットぶんの解析結果。
#[derive(Clone, Default, PartialEq, Debug)]
pub struct WaterShadeParamSet {
    /// 宣言（アセット内の出現順＝GPU のスロット順＝インスペクタの行順）。
    pub params: Vec<WaterShadeParamDecl>,
    /// 解析中に見つかった問題（構文エラー・上限超過・重複）。
    pub warnings: Vec<WaterShadeParamWarning>,
}

impl WaterShadeParamSet {
    /// 名前から宣言を引く（見つからなければ None）。
    pub fn find(&self, name: &str) -> Option<&WaterShadeParamDecl> {
        self.params.iter().find(|p| p.name == name)
    }
}

// ============================================================
//  解析
// ============================================================

/// アセットの WGSL ソースからパラメータ注釈を解析する。
///
/// 走査は行単位で、`//! param` で始まる行だけを見る。
/// **ブロックコメントの中身は考慮しない**（`/* //! param ... */` も宣言として拾う）。
/// アノテーションは「宣言を書く場所」であってコードではないため、
/// コメントアウトで無効化する用途を想定していない。
pub fn parse_params(asset_src: &str) -> WaterShadeParamSet {
    let mut set = WaterShadeParamSet::default();

    for (idx, raw) in asset_src.lines().enumerate() {
        let line_no = idx + 1;
        let Some(body) = annotation_body(raw) else { continue };

        // ── 1 行を解析する（失敗したら理由を警告に残して次の行へ）──
        match parse_annotation_body(body) {
            Ok(decl) => {
                // 重複名は最初の 1 個だけを採る（後勝ちにすると GPU のスロット順が
                // 「後ろの行」に引っ張られて、既に保存された値との対応が壊れる）。
                if set.params.iter().any(|p| p.name == decl.name) {
                    set.warnings.push(WaterShadeParamWarning {
                        line:    line_no,
                        message: format!(
                            "パラメータ `{}` が重複しています（最初の宣言だけを使います）", decl.name),
                    });
                    continue;
                }
                // 上限超過は黙って切らず、理由を残して無視する。
                if set.params.len() >= WATER_SHADE_PARAM_MAX {
                    set.warnings.push(WaterShadeParamWarning {
                        line:    line_no,
                        message: format!(
                            "パラメータ `{}` は上限 {WATER_SHADE_PARAM_MAX} 個を超えるため \
                             無視しました（不要な宣言を減らしてください）", decl.name),
                    });
                    continue;
                }
                set.params.push(decl);
            }
            Err(message) => set.warnings.push(WaterShadeParamWarning { line: line_no, message }),
        }
    }

    set
}

/// 行がパラメータ注釈なら、`//! param` を取り除いた本文を返す。
///
/// 例: `    //! param float glow = 2.0  // 発光` → `float glow = 2.0  // 発光`
fn annotation_body(raw: &str) -> Option<&str> {
    let t = raw.trim_start();
    let rest = t.strip_prefix(ANNOTATION_PREFIX)?;
    // `//!param` のように空白が無い書き方も許す（キーワードの直後は空白必須）。
    let rest = rest.trim_start();
    let rest = rest.strip_prefix(ANNOTATION_KEYWORD)?;
    // `//! parameter foo` を `param` として誤認しないよう、直後は空白であること。
    if !rest.starts_with(char::is_whitespace) { return None; }
    Some(rest.trim())
}

/// 注釈本文（`<型> <名前> = <既定値> [// ラベル]`）を 1 宣言へ変換する。
fn parse_annotation_body(body: &str) -> Result<WaterShadeParamDecl, String> {
    // ── ① 行末のラベルコメントを切り離す ──────────────────────
    //     本文側に `//` が現れることは無い（値は数値かタプルだけ）ので、
    //     最初の `//` から後ろをラベルとして扱ってよい。
    let (decl_part, label_part) = match body.find(LABEL_COMMENT) {
        Some(i) => (&body[..i], body[i + LABEL_COMMENT.len()..].trim()),
        None    => (body, ""),
    };

    // ── ② `=` で「型と名前」「既定値」に割る ────────────────────
    let Some((head, default_part)) = decl_part.split_once('=') else {
        return Err("`=` と既定値がありません（例: `//! param float glow = 2.0`）".to_string());
    };
    let default_text = default_part.trim();
    if default_text.is_empty() {
        return Err("既定値が空です".to_string());
    }

    // ── ③ 型指定と名前を割る（名前は末尾の 1 語）────────────────
    //     `range(0.0, 10.0)` は空白を含みうるので、`rsplit_once` で
    //     **最後の空白**を境にする（型指定の中の空白に影響されない）。
    let head = head.trim();
    let Some((type_text, name)) = head.rsplit_once(char::is_whitespace) else {
        return Err(format!("型指定と名前を空白で区切ってください（`{head}`）"));
    };
    let type_text = type_text.trim();
    let name      = name.trim();
    validate_name(name)?;

    // ── ④ 型指定を解釈し、既定値をその型として読む ──────────────
    let (kind, default) = if type_text == TYPE_COLOR {
        (WaterShadeParamKind::Color, parse_color_default(default_text)?)
    } else if type_text == TYPE_FLOAT {
        (WaterShadeParamKind::Float, parse_scalar_default(default_text)?)
    } else if let Some(args) = type_text.strip_prefix(TYPE_RANGE_PREFIX) {
        let (min, max) = parse_range_args(args)?;
        let value = parse_scalar_default(default_text)?;
        (WaterShadeParamKind::Range { min, max }, value)
    } else {
        return Err(format!(
            "未知の型指定 `{type_text}`（使えるのは `{TYPE_COLOR}` / \
             `{TYPE_RANGE_PREFIX}(min, max)` / `{TYPE_FLOAT}`）"));
    };

    // ── ⑤ ラベル（無ければ識別子そのもの）──────────────────────
    let label = if label_part.is_empty() { name.to_string() } else { label_part.to_string() };

    Ok(WaterShadeParamDecl { name: name.to_string(), kind, default, label })
}

/// 識別子として使える名前かを検査する。
///
/// WGSL の識別子規則（先頭は英字か `_`、以降は英数字か `_`）に加えて、
/// エンジンの予約接頭辞を弾く。
fn validate_name(name: &str) -> Result<(), String> {
    if name.is_empty() {
        return Err("パラメータ名が空です".to_string());
    }
    let mut chars = name.chars();
    let first = chars.next().expect("空でないことは確認済み");
    if !(first.is_ascii_alphabetic() || first == '_') {
        return Err(format!("パラメータ名 `{name}` は英字か `_` で始めてください"));
    }
    if !chars.all(|c| c.is_ascii_alphanumeric() || c == '_') {
        return Err(format!("パラメータ名 `{name}` に使えない文字が含まれています（英数字と `_` のみ）"));
    }
    for prefix in RESERVED_NAME_PREFIXES {
        if name.starts_with(prefix) {
            return Err(format!(
                "パラメータ名 `{name}` はエンジン予約の接頭辞 `{prefix}` で始まっています \
                 （別の名前にしてください）"));
        }
    }
    Ok(())
}

/// `range(min, max)` の引数部（`(0.0, 10.0)`）を読む。
fn parse_range_args(args: &str) -> Result<(f32, f32), String> {
    let inner = args.trim();
    let Some(inner) = inner.strip_prefix(TUPLE_OPEN).and_then(|s| s.strip_suffix(TUPLE_CLOSE)) else {
        return Err(format!("`{TYPE_RANGE_PREFIX}` の範囲指定は `(min, max)` の形で書いてください"));
    };
    let parts: Vec<&str> = inner.split(TUPLE_SEPARATOR).collect();
    if parts.len() != RANGE_ARG_COUNT {
        return Err(format!("`{TYPE_RANGE_PREFIX}` の引数は min と max の 2 個です"));
    }
    let min = parse_number(parts[0])?;
    let max = parse_number(parts[1])?;
    if !(max > min) {
        return Err(format!("`{TYPE_RANGE_PREFIX}` の max({max}) は min({min}) より大きい必要があります"));
    }
    Ok((min, max))
}

/// スカラー既定値（`2.0`）を読む。
fn parse_scalar_default(text: &str) -> Result<[f32; PARAM_VALUE_COMPONENTS], String> {
    let v = parse_number(text)?;
    Ok([v, 0.0, 0.0, 0.0])
}

/// 色既定値（`(1.0, 0.4, 0.1)`）を読む。
fn parse_color_default(text: &str) -> Result<[f32; PARAM_VALUE_COMPONENTS], String> {
    let Some(inner) = text.strip_prefix(TUPLE_OPEN).and_then(|s| s.strip_suffix(TUPLE_CLOSE)) else {
        return Err(format!("`{TYPE_COLOR}` の既定値は `(r, g, b)` の形で書いてください"));
    };
    let parts: Vec<&str> = inner.split(TUPLE_SEPARATOR).collect();
    if parts.len() != COLOR_COMPONENT_COUNT {
        return Err(format!("`{TYPE_COLOR}` の既定値は RGB の 3 要素です"));
    }
    Ok([parse_number(parts[0])?, parse_number(parts[1])?, parse_number(parts[2])?, 0.0])
}

/// 数値 1 個を読む（前後の空白は許す）。
fn parse_number(text: &str) -> Result<f32, String> {
    text.trim().parse::<f32>()
        .map_err(|_| format!("`{}` を数値として読めません", text.trim()))
}

// ============================================================
//  GPU ブロックの組み立て
// ============================================================

/// GPU へ送る 1 インスタンスぶんのパラメータブロック。
///
/// WGSL 側 `water_shade_params.wgsl` の `WaterShadeParamBlock` と
/// **要素数・レイアウトを厳密一致**させること（`vec4` の配列なので std430 の
/// パディング問題は構造的に起きない）。
#[repr(C)]
#[derive(Clone, Copy, PartialEq, Debug, bytemuck::Pod, bytemuck::Zeroable)]
pub struct WaterShadeParamBlock {
    /// スロット 0..WATER_SHADE_PARAM_MAX の値。宣言の無いスロットはゼロ。
    pub values: [[f32; PARAM_VALUE_COMPONENTS]; WATER_SHADE_PARAM_MAX],
}

impl Default for WaterShadeParamBlock {
    /// 宣言が 1 個も無い（＝アセット未指定・ロード失敗）ときのブロック。全ゼロ。
    fn default() -> Self {
        Self { values: [[0.0; PARAM_VALUE_COMPONENTS]; WATER_SHADE_PARAM_MAX] }
    }
}

/// 宣言リストとシーン保存値から GPU ブロックを作る。
///
/// - スロット順は**宣言の出現順**（生成する WGSL の添字と一致する）。
/// - `saved` に同名の値があればそれを、無ければアセットの既定値を使う。
/// - `saved` にしか無い名前（アセット側の宣言が消えた・改名された）は**無視**する
///   （孤児は捨てるが、シーンからは消さない。アセットを戻せば復活する）。
pub fn build_block(
    decls: &[WaterShadeParamDecl],
    saved: &BTreeMap<String, [f32; PARAM_VALUE_COMPONENTS]>,
) -> WaterShadeParamBlock {
    let mut block = WaterShadeParamBlock::default();
    for (slot, decl) in decls.iter().take(WATER_SHADE_PARAM_MAX).enumerate() {
        block.values[slot] = saved.get(&decl.name).copied().unwrap_or(decl.default);
    }
    block
}

// ============================================================
//  インスペクタへのワイヤ表現（ACTOR_COMPONENTS の JSON）
// ============================================================

/// 宣言と現在値を、インスペクタが行を作れる JSON 配列にする（W8.2 のワイヤ契約）。
///
/// 出力例:
/// ```json
/// [{"name":"emission_color","type":"color","label":"発光色",
///   "min":0.0,"max":0.0,"value":[1.0,0.4,0.1,0.0]}]
/// ```
/// - `type` は `color` / `range` / `float`。C# はこの文字列で行の形（ピッカー／
///   スライダー／数値）を選ぶ。
/// - `min` / `max` は `range` のときだけ意味を持つ（他の型では 0 を送る）。
/// - `value` は**常に 4 成分**。保存値があればそれ、無ければアセットの既定値。
///
/// **解析は Rust 側だけが行う**という設計なので、C# はこの配列を表示するだけでよい。
pub fn params_json(
    decls: &[WaterShadeParamDecl],
    saved: &BTreeMap<String, [f32; PARAM_VALUE_COMPONENTS]>,
) -> String {
    let items: Vec<serde_json::Value> = decls.iter().map(|d| {
        let value = saved.get(&d.name).copied().unwrap_or(d.default);
        let (min, max) = match d.kind {
            WaterShadeParamKind::Range { min, max } => (min, max),
            // range 以外では使わない枠。キーを常に出しておくと C# の分岐が減る。
            _ => (0.0, 0.0),
        };
        serde_json::json!({
            "name":  d.name,
            "type":  d.kind.as_str(),
            "label": d.label,
            "min":   min,
            "max":   max,
            "value": [value[0], value[1], value[2], value[3]],
        })
    }).collect();
    serde_json::to_string(&items).unwrap_or_else(|_| "[]".to_string())
}

// ============================================================
//  テスト
// ============================================================
#[cfg(test)]
mod tests {
    use super::*;

    /// 3 種の型が正しく解析され、ラベルが行末コメントから取られること。
    #[test]
    fn parses_all_three_kinds() {
        let src = "\
// @water_shading_contract 1
//! param color  emission_color = (1.0, 0.4, 0.1)   // 発光色
//! param range(0.0, 10.0) crack_speed = 1.5        // 亀裂の流速
//! param float  glow_boost = 2.0                    // 発光の強さ
fn water_shade(input: WaterShadeInput) -> vec4<f32> { return vec4<f32>(0.0); }
";
        let set = parse_params(src);
        assert!(set.warnings.is_empty(), "警告が出た: {:?}", set.warnings);
        assert_eq!(set.params.len(), 3);

        assert_eq!(set.params[0].name, "emission_color");
        assert_eq!(set.params[0].kind, WaterShadeParamKind::Color);
        assert_eq!(set.params[0].default, [1.0, 0.4, 0.1, 0.0]);
        assert_eq!(set.params[0].label, "発光色");

        assert_eq!(set.params[1].name, "crack_speed");
        assert_eq!(set.params[1].kind, WaterShadeParamKind::Range { min: 0.0, max: 10.0 });
        assert_eq!(set.params[1].default, [1.5, 0.0, 0.0, 0.0]);
        assert_eq!(set.params[1].label, "亀裂の流速");

        assert_eq!(set.params[2].name, "glow_boost");
        assert_eq!(set.params[2].kind, WaterShadeParamKind::Float);
        assert_eq!(set.params[2].default, [2.0, 0.0, 0.0, 0.0]);
    }

    /// ラベルが無ければ識別子名がラベルになること。
    #[test]
    fn label_falls_back_to_identifier() {
        let set = parse_params("//! param float glow = 1.0\n");
        assert_eq!(set.params.len(), 1);
        assert_eq!(set.params[0].label, "glow");
    }

    /// 注釈でない行（普通のコメント・`//! param` 以外）は拾わないこと。
    #[test]
    fn ignores_non_annotation_lines() {
        let src = "\
// param float a = 1.0
/// param float b = 1.0
//! parameter float c = 1.0
//! uniform float d = 1.0
const e: f32 = 1.0;
";
        let set = parse_params(src);
        assert!(set.params.is_empty(), "拾ってはならない行を拾った: {:?}", set.params);
        assert!(set.warnings.is_empty(), "無関係な行で警告を出してはならない: {:?}", set.warnings);
    }

    /// 構文エラーの行は「無視 ＋ 行番号つき警告」になること（アセット全体は壊さない）。
    #[test]
    fn malformed_lines_warn_with_line_number() {
        let src = "\
//! param float ok = 1.0
//! param float missing_default
//! param bogus  weird = 1.0
//! param color  bad_color = 1.0
//! param range(1.0) bad_range = 1.0
";
        let set = parse_params(src);
        assert_eq!(set.params.len(), 1, "正しい 1 行だけが通ること: {:?}", set.params);
        assert_eq!(set.warnings.len(), 4, "壊れた 4 行ぶん警告が出ること: {:?}", set.warnings);
        assert_eq!(set.warnings[0].line, 2, "{:?}", set.warnings[0]);
        assert_eq!(set.warnings[1].line, 3, "{:?}", set.warnings[1]);
    }

    /// 上限を超えた宣言は「無視 ＋ 警告」で、黙って切られないこと。
    #[test]
    fn exceeding_max_warns_and_ignores() {
        let mut src = String::new();
        for i in 0..(WATER_SHADE_PARAM_MAX + 2) {
            src.push_str(&format!("//! param float p{i} = {i}.0\n"));
        }
        let set = parse_params(&src);
        assert_eq!(set.params.len(), WATER_SHADE_PARAM_MAX);
        assert_eq!(set.warnings.len(), 2, "超過ぶんの警告が出ること: {:?}", set.warnings);
        assert!(set.warnings[0].message.contains("上限"), "{:?}", set.warnings[0]);
    }

    /// 重複名は最初の宣言だけを採り、警告を出すこと。
    #[test]
    fn duplicate_names_keep_first() {
        let set = parse_params("//! param float a = 1.0\n//! param float a = 2.0\n");
        assert_eq!(set.params.len(), 1);
        assert_eq!(set.params[0].default[0], 1.0, "先に書いた宣言が残ること");
        assert_eq!(set.warnings.len(), 1);
        assert!(set.warnings[0].message.contains("重複"), "{:?}", set.warnings[0]);
    }

    /// エンジン予約の接頭辞を持つ名前は弾かれること（WGSL の名前衝突を防ぐ）。
    #[test]
    fn rejects_reserved_prefixes() {
        for name in ["water_shade_x", "u_water_foo", "WATER_MAX"] {
            let set = parse_params(&format!("//! param float {name} = 1.0\n"));
            assert!(set.params.is_empty(), "{name} は弾かれること");
            assert_eq!(set.warnings.len(), 1);
            assert!(set.warnings[0].message.contains("予約"), "{:?}", set.warnings[0]);
        }
    }

    /// 識別子として不正な名前は弾かれること。
    #[test]
    fn rejects_invalid_identifiers() {
        for name in ["1abc", "a-b", "a.b"] {
            let set = parse_params(&format!("//! param float {name} = 1.0\n"));
            assert!(set.params.is_empty(), "{name} は弾かれること: {:?}", set.params);
            assert_eq!(set.warnings.len(), 1, "{name}: {:?}", set.warnings);
        }
    }

    /// range の max <= min は弾かれること（スライダーが成立しない）。
    #[test]
    fn rejects_inverted_range() {
        let set = parse_params("//! param range(1.0, 1.0) a = 1.0\n");
        assert!(set.params.is_empty());
        assert_eq!(set.warnings.len(), 1);
    }

    /// GPU ブロックは宣言順に詰められ、保存値が既定値を上書きすること。
    #[test]
    fn block_uses_saved_values_over_defaults() {
        let set = parse_params(
            "//! param color c = (1.0, 0.0, 0.0)\n//! param float f = 2.0\n");
        let mut saved = BTreeMap::new();
        saved.insert("f".to_string(), [9.0, 0.0, 0.0, 0.0]);
        let block = build_block(&set.params, &saved);
        assert_eq!(block.values[0], [1.0, 0.0, 0.0, 0.0], "保存値が無ければ既定値");
        assert_eq!(block.values[1], [9.0, 0.0, 0.0, 0.0], "保存値があれば優先");
        assert_eq!(block.values[2], [0.0; PARAM_VALUE_COMPONENTS], "未使用スロットはゼロ");
    }

    /// 宣言に無い保存値（改名・削除後の孤児）は無視され、ブロックを壊さないこと。
    #[test]
    fn orphan_saved_values_are_ignored() {
        let set = parse_params("//! param float f = 2.0\n");
        let mut saved = BTreeMap::new();
        saved.insert("removed_param".to_string(), [7.0, 0.0, 0.0, 0.0]);
        let block = build_block(&set.params, &saved);
        assert_eq!(block.values[0], [2.0, 0.0, 0.0, 0.0], "既定値のまま");
    }

    /// インスペクタ向け JSON が宣言順・型・現在値を正しく載せること（ワイヤ契約）。
    #[test]
    fn params_json_carries_kind_range_and_current_value() {
        let set = parse_params(
            "//! param color c = (1.0, 0.0, 0.0)   // 色\n\
             //! param range(0.0, 10.0) r = 1.5\n\
             //! param float f = 2.0\n");
        let mut saved = BTreeMap::new();
        saved.insert("c".to_string(), [0.0, 1.0, 0.0, 0.0]);
        let json: serde_json::Value =
            serde_json::from_str(&params_json(&set.params, &saved)).expect("JSON として読めること");
        let arr = json.as_array().expect("配列であること");
        assert_eq!(arr.len(), 3);
        // ① 宣言順・型・ラベル
        assert_eq!(arr[0]["name"], "c");
        assert_eq!(arr[0]["type"], "color");
        assert_eq!(arr[0]["label"], "色");
        // ② 保存値が現在値として載ること
        assert_eq!(arr[0]["value"][1], 1.0);
        // ③ range の範囲が載ること（ラベル省略時は識別子）
        assert_eq!(arr[1]["type"], "range");
        assert_eq!(arr[1]["min"], 0.0);
        assert_eq!(arr[1]["max"], 10.0);
        assert_eq!(arr[1]["label"], "r");
        // ④ 保存値が無ければアセット既定値
        assert_eq!(arr[2]["type"], "float");
        assert_eq!(arr[2]["value"][0], 2.0);
    }

    /// 宣言が無ければ空配列（インスペクタは行を 1 つも作らない）。
    #[test]
    fn params_json_is_empty_array_without_declarations() {
        assert_eq!(params_json(&[], &BTreeMap::new()), "[]");
    }

    /// 型ごとの WGSL 型名・swizzle が一貫していること（生成コードの正しさの土台）。
    #[test]
    fn wgsl_type_and_swizzle_match_kind() {
        assert_eq!(WaterShadeParamKind::Color.wgsl_type(), "vec3<f32>");
        assert_eq!(WaterShadeParamKind::Color.wgsl_swizzle(), "xyz");
        assert_eq!(WaterShadeParamKind::Float.wgsl_type(), "f32");
        assert_eq!(WaterShadeParamKind::Float.wgsl_swizzle(), "x");
        let r = WaterShadeParamKind::Range { min: 0.0, max: 1.0 };
        assert_eq!(r.wgsl_type(), "f32");
        assert_eq!(r.wgsl_swizzle(), "x");
    }
}
