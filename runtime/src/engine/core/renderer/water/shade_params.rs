// ============================================================
//  water/shade_params.rs — 水面シェーディングアセットの「パラメータ宣言」解析（Phase W8.2 / W5.2）
//
//  ## 役割（単一責任）
//  ユーザーが書いた `.wgsl`（水面シェーディングアセット）の中に置かれた
//  **`override` 宣言ブロック**を読み取り、「インスペクタに出す 1 行ぶんの宣言」の
//  リストへ変換する。あわせて、その宣言行を**連結前に除去する**ための行番号も返す。
//  ファイル I/O も GPU も触らない**純粋関数**の集まりであり、この 1 ファイルが
//  構文の正典である（C# 側は解析を持たず、ランタイムが送る JSON を表示するだけ）。
//
//  ## 構文（C# の属性に似せた宣言。W5.2 で `//! param` コメント方式から移行）
//  ```wgsl
//  @color
//  override emission_color: vec3<f32> = vec3(1.0, 0.4, 0.1);  // 発光色
//
//  @range(0.0, 10.0)
//  override crack_speed: f32 = 1.5;                           // 亀裂の流れる速さ
//
//  @reset @range(0.0, 4.0)
//  override glow_boost: f32 = 2.0;                            // リセットボタン付き
//
//  @ref
//  override player_speed: f32 = 0.0;                          // 参照バインド行（W8.3）
//
//  override plain: f32 = 1.0;                                 // 属性なし = 数値行
//  ```
//    ・属性は**宣言の上の行**にも**同一行**にも書ける。複数併記は空白区切り。
//    ・`@color` … カラーピッカー（型は `vec3<f32>`）。`vec3<f32>` なら属性を省いても色扱い。
//    ・`@range(min, max)` … スライダー（型は `f32`）。
//    ・`@reset` … インスペクタ行の右端に「デフォルトに戻す」ボタンを出す。
//    ・`@ref` … 値の入力欄の代わりに**参照バインド行**を出す（W8.3）。
//               シーン内コンポーネントの変数を毎フレーム流し込む。
//               `@reset` と併用すると「バインドを外して既定値へ戻す」ボタンが付く。
//    ・属性なしの `f32` … 数値行。
//  行末の `// …` は**インスペクタの表示ラベル**（省略時は識別子そのもの）。
//
//  ## なぜ「素の WGSL の override」ではないのか
//  WGSL の `override` は**スカラーしか許さず**、独自属性（`@color` 等）も通らない。
//  そのため素のまま naga へ渡すことはできない。エンジンは連結の前に
//  `strip_declarations` でこのブロックを**行ごと空行へ潰して除去**し、
//  代わりに `var<private> <名前>: <型>;` を生成してアセット本体の前へ置き、
//  `water_shade_entry` の先頭でストレージバッファの値を代入する
//  （生成は `water/shading_asset.rs` の責務）。
//  除去は**行数を変えない**ので、naga が報告する行番号はアセットの行番号のまま使える。
//
//  ## 宣言した名前は WGSL からそのまま使える
//  アセット作者は「宣言を 1 行書けば `water_shade` の中でその名前を値として読める」だけでよく、
//  バインディングもインデックスも一切知らなくてよい。
//
//  ## 上限
//  1 アセットあたり `WATER_SHADE_PARAM_MAX` 個まで。超過ぶんは**黙って切らず**、
//  警告メッセージ（`warnings`）に理由を残したうえで無視する。
//
//  ## 依存
//  なし（標準ライブラリのみ）。消費側は `water/shading_asset.rs`（除去・生成）・
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

/// パラメータ宣言のキーワード（WGSL の `override` を借りている）。
const DECL_KEYWORD: &str = "override";

/// 属性の開始文字（`@color` / `@range(0.0, 1.0)`）。
const ATTR_SIGIL: char = '@';

/// 属性 `@color`（vec3・カラーピッカー）。
const ATTR_COLOR: &str = "color";
/// 属性 `@range(min, max)`（f32・スライダー）。
const ATTR_RANGE: &str = "range";
/// 属性 `@reset`（インスペクタ行に「デフォルトに戻す」ボタンを出す）。
const ATTR_RESET: &str = "reset";
/// 属性 `@ref`（インスペクタ行を「値の入力」ではなく「参照バインド」にする。W8.3）。
///
/// この属性が付いたパラメータは、シーン内のコンポーネントの変数を毎フレーム流し込む
/// 対象になる。値そのものを編集する UI は出さず、参照ピッカーの行に置き換わる。
const ATTR_REF: &str = "ref";

/// エンジンが解釈する属性名の一覧。
///
/// **この一覧に 1 つでも該当する属性行だけを「パラメータ宣言ブロック」とみなす。**
/// `@group` / `@binding` / `@must_use` のような WGSL 本来の属性しか無い行は
/// エンジンの宣言ではないので、除去も警告もしない（ユーザーのコードを壊さないため）。
/// 後続フェーズで `@ref` 等を足すときは、ここへ名前を追加してから
/// `apply_attributes` に解釈を書く。
const KNOWN_ATTRS: [&str; 4] = [ATTR_COLOR, ATTR_RANGE, ATTR_RESET, ATTR_REF];

/// 行末ラベルコメントの区切り。
const LABEL_COMMENT: &str = "//";

/// 属性引数・ベクタ初期化子の開き括弧。
const PAREN_OPEN: char = '(';
/// 属性引数・ベクタ初期化子の閉じ括弧。
const PAREN_CLOSE: char = ')';
/// 引数の区切り。
const ARG_SEPARATOR: char = ',';
/// 宣言の終端（WGSL に合わせて必須）。
const STATEMENT_END: char = ';';
/// 型注釈の区切り（`name: type`）。
const TYPE_ANNOTATION: char = ':';
/// 既定値の代入記号。
const ASSIGN: char = '=';

/// スカラー型の綴り（空白除去後の比較用）。
const TYPE_F32: &str = "f32";
/// 色型の綴り（空白除去後の比較用）。長い綴りと短い綴りの両方を許す。
const TYPE_VEC3_LONG: &str = "vec3<f32>";
/// 色型の短い綴り（WGSL の型エイリアス）。
const TYPE_VEC3_SHORT: &str = "vec3f";

/// ベクタ初期化子の関数名（`vec3(...)` / `vec3<f32>(...)` / `vec3f(...)`）。
const VEC3_CTORS: [&str; 3] = ["vec3<f32>", "vec3f", "vec3"];

/// 色の成分数（RGB）。
const COLOR_COMPONENT_COUNT: usize = 3;
/// `@range(min, max)` の引数の個数。
const RANGE_ARG_COUNT: usize = 2;

/// GPU へ運ぶ 1 パラメータぶんの成分数（vec4 固定）。
pub const PARAM_VALUE_COMPONENTS: usize = 4;

/// WGSL の浮動小数リテラルに付く型サフィックス（`1.5f` / `1.5h`）。
const FLOAT_SUFFIXES: [char; 2] = ['f', 'h'];

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
            WaterShadeParamKind::Color        => ATTR_COLOR,
            WaterShadeParamKind::Range { .. } => ATTR_RANGE,
            WaterShadeParamKind::Float        => "float",
        }
    }

    /// 生成する `var<private>` の WGSL 型名。
    pub fn wgsl_type(self) -> &'static str {
        match self {
            WaterShadeParamKind::Color => TYPE_VEC3_LONG,
            _                          => TYPE_F32,
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
    /// `@reset` が付いているか（インスペクタ行に「デフォルトに戻す」ボタンを出す）。
    pub resettable: bool,
    /// `@ref` が付いているか（インスペクタ行を参照バインド行にする。W8.3）。
    ///
    /// true のパラメータは、シーン内コンポーネントの変数を毎フレーム読んで
    /// 値を上書きできる。バインドが未設定・解決不能なら保存値／既定値へ落ちる。
    pub bindable: bool,
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
    /// 解析中に見つかった問題（構文エラー・上限超過・重複・未知の属性）。
    pub warnings: Vec<WaterShadeParamWarning>,
    /// **連結前に除去すべき行**（1 始まり）。
    ///
    /// 宣言行と、その宣言に付いた属性だけの行。素の naga はこれらを解釈できないため、
    /// `strip_declarations` が空行へ潰す（行数は保存されるので行番号写像は不変）。
    pub consumed_lines: Vec<usize>,
}

impl WaterShadeParamSet {
    /// 名前から宣言を引く（見つからなければ None）。
    pub fn find(&self, name: &str) -> Option<&WaterShadeParamDecl> {
        self.params.iter().find(|p| p.name == name)
    }
}

/// 1 個の属性（`@name` / `@name(args...)`）。
///
/// 未知の属性も**捨てずにここへ入れる**（`apply_attributes` が警告を出すため）。
#[derive(Clone, PartialEq, Debug)]
struct Attribute {
    /// `@` を除いた属性名。
    name: String,
    /// 括弧内の引数（無ければ空 Vec）。
    args: Vec<String>,
    /// この属性が書かれていた行番号（1 始まり。ぶら下がり警告に使う）。
    line: usize,
}

// ============================================================
//  解析（本体）
// ============================================================

/// アセットの WGSL ソースからパラメータ宣言を解析する。
///
/// 走査は行単位で、次のいずれかに該当する行だけを「エンジンの宣言」とみなす:
///   ・`override` キーワードで始まる行（属性が同一行に前置されていてもよい）
///   ・エンジンが解釈する属性（`KNOWN_ATTRS`）を含む、属性だけの行
///
/// **ブロックコメントの中身は考慮しない**（`/* override x: f32 = 1.0; */` も宣言として拾う）。
/// 宣言はコードであってコメントアウトで無効化する用途を想定していない
/// （無効化したいなら宣言ごと消すこと）。
pub fn parse_params(asset_src: &str) -> WaterShadeParamSet {
    let mut set = WaterShadeParamSet::default();
    // 直前までに現れた「宣言待ちの属性」（上の行に書かれた属性）。
    let mut pending: Vec<Attribute> = Vec::new();

    for (idx, raw) in asset_src.split('\n').enumerate() {
        let line_no = idx + 1;
        // CRLF 対応（行末の \r は解析の邪魔にしかならない）。
        let raw = raw.trim_end_matches('\r');

        // ── ① 行末のラベルコメントを切り離す ──────────────────
        let (code, label) = split_line_comment(raw);
        let code = code.trim();

        // 空行・コメントだけの行は「属性と宣言の間に挟まっても良い」ものとして素通しする
        //（属性の直後に説明コメントを書く書き方を許すため）。
        if code.is_empty() { continue; }

        // ── ② 行頭の属性列を読む ──────────────────────────────
        let (attrs, rest) = split_leading_attributes(code, line_no);
        let rest = rest.trim();

        if rest.is_empty() {
            // 属性だけの行。エンジンが解釈する属性を 1 つも含まないなら
            // WGSL 本来の属性（`@group` 等）なので**一切触らない**。
            if attrs.iter().any(|a| KNOWN_ATTRS.contains(&a.name.as_str())) {
                pending.extend(attrs);
                set.consumed_lines.push(line_no);
            }
            continue;
        }

        if !starts_with_keyword(rest, DECL_KEYWORD) {
            // 宣言ではない普通のコード行。ここまでにぶら下がっていた属性は
            // 対応する宣言を持たない＝書き間違いなので、警告して捨てる。
            flush_dangling(&mut pending, &mut set);
            continue;
        }

        // ── ③ 宣言行 ──────────────────────────────────────────
        //     ここから先は必ず除去する（素の naga は override の vec3 も
        //     独自属性も解釈できないため、残すとアセット全体が壊れる）。
        set.consumed_lines.push(line_no);
        let mut all_attrs = std::mem::take(&mut pending);
        all_attrs.extend(attrs);

        // 非致命の指摘（未知の属性など）はここへ溜め、行番号を付けて警告にする。
        let mut notes: Vec<String> = Vec::new();
        let parsed = parse_declaration(rest, &all_attrs, label, &mut notes);
        for message in notes {
            set.warnings.push(WaterShadeParamWarning { line: line_no, message });
        }
        match parsed {
            Ok(decl)     => push_declaration(&mut set, decl, line_no),
            Err(message) => set.warnings.push(WaterShadeParamWarning { line: line_no, message }),
        }
    }

    // 末尾にぶら下がったままの属性も警告する。
    flush_dangling(&mut pending, &mut set);
    set
}

/// 解析済みの宣言を集合へ追加する（重複・上限のチェック込み）。
fn push_declaration(set: &mut WaterShadeParamSet, decl: WaterShadeParamDecl, line_no: usize) {
    // 重複名は最初の 1 個だけを採る（後勝ちにすると GPU のスロット順が
    // 「後ろの行」に引っ張られて、既に保存された値との対応が壊れる）。
    if set.params.iter().any(|p| p.name == decl.name) {
        set.warnings.push(WaterShadeParamWarning {
            line:    line_no,
            message: format!("パラメータ `{}` が重複しています（最初の宣言だけを使います）", decl.name),
        });
        return;
    }
    // 上限超過は黙って切らず、理由を残して無視する。
    if set.params.len() >= WATER_SHADE_PARAM_MAX {
        set.warnings.push(WaterShadeParamWarning {
            line:    line_no,
            message: format!(
                "パラメータ `{}` は上限 {WATER_SHADE_PARAM_MAX} 個を超えるため \
                 無視しました（不要な宣言を減らしてください）", decl.name),
        });
        return;
    }
    set.params.push(decl);
}

/// 対応する宣言を持たない属性を警告に変えて捨てる。
fn flush_dangling(pending: &mut Vec<Attribute>, set: &mut WaterShadeParamSet) {
    for a in pending.drain(..) {
        set.warnings.push(WaterShadeParamWarning {
            line:    a.line,
            message: format!(
                "属性 `{ATTR_SIGIL}{}` に対応する `{DECL_KEYWORD}` 宣言がありません", a.name),
        });
    }
}

/// 行を「コード部分」と「行末コメント（＝表示ラベル）」に割る。
///
/// 宣言の値に `//` が現れることは無い（数値と `vec3(...)` だけ）ので、
/// 最初の `//` から後ろをラベルとして扱ってよい。
fn split_line_comment(line: &str) -> (&str, &str) {
    match line.find(LABEL_COMMENT) {
        Some(i) => (&line[..i], line[i + LABEL_COMMENT.len()..].trim()),
        None    => (line, ""),
    }
}

/// 行頭に並んだ属性列を読み、`(属性, 残りのテキスト)` を返す。
///
/// 属性でない文字が現れた時点で打ち切る（`@group(0) var ...` は属性 1 個＋残り）。
fn split_leading_attributes(code: &str, line_no: usize) -> (Vec<Attribute>, &str) {
    let mut attrs = Vec::new();
    let mut rest  = code.trim_start();
    while let Some(after_sigil) = rest.strip_prefix(ATTR_SIGIL) {
        // 属性名（英数字と `_`）。
        let name_len = after_sigil.chars()
            .take_while(|c| c.is_ascii_alphanumeric() || *c == '_')
            .map(char::len_utf8)
            .sum::<usize>();
        if name_len == 0 { break; }          // `@` の直後が名前でない＝属性ではない
        let (name, tail) = after_sigil.split_at(name_len);
        let tail_trimmed = tail.trim_start();

        // 引数（あれば）。閉じ括弧が無い行は属性として成立しないので打ち切る。
        let (args, after) = if let Some(inner_start) = tail_trimmed.strip_prefix(PAREN_OPEN) {
            match inner_start.find(PAREN_CLOSE) {
                Some(end) => (
                    inner_start[..end].split(ARG_SEPARATOR).map(|s| s.trim().to_string()).collect(),
                    &inner_start[end + PAREN_CLOSE.len_utf8()..],
                ),
                None => break,
            }
        } else {
            (Vec::new(), tail)
        };

        attrs.push(Attribute { name: name.to_string(), args, line: line_no });
        rest = after.trim_start();
    }
    (attrs, rest)
}

/// テキストがキーワードで始まるか（直後が識別子文字でないこと＝語境界）。
fn starts_with_keyword(text: &str, keyword: &str) -> bool {
    let Some(rest) = text.strip_prefix(keyword) else { return false };
    match rest.chars().next() {
        None    => true,
        Some(c) => !(c.is_ascii_alphanumeric() || c == '_'),
    }
}

/// `override <名前>: <型> = <既定値>;` を 1 宣言へ変換する。
///
/// - 戻り値 `Err` : その行は宣言として成立しない（呼び出し側が行番号を付けて警告にする）。
/// - `notes`      : 致命的ではない指摘（未知の属性など）。宣言自体は成立する。
fn parse_declaration(
    rest:  &str,
    attrs: &[Attribute],
    label: &str,
    notes: &mut Vec<String>,
) -> Result<WaterShadeParamDecl, String> {
    // ── ① キーワードを外す ────────────────────────────────────
    let body = rest[DECL_KEYWORD.len()..].trim();

    // ── ② `名前 : 型 = 既定値 ;` に割る ───────────────────────
    let Some((name, after_name)) = body.split_once(TYPE_ANNOTATION) else {
        return Err(format!(
            "型注釈がありません（例: `{DECL_KEYWORD} glow{TYPE_ANNOTATION} {TYPE_F32} \
             {ASSIGN} 2.0{STATEMENT_END}`）"));
    };
    let Some((type_text, init_text)) = after_name.split_once(ASSIGN) else {
        return Err(format!("既定値がありません（`{ASSIGN}` の右に初期値を書いてください）"));
    };
    let init_text = init_text.trim();
    let Some(init_text) = init_text.strip_suffix(STATEMENT_END) else {
        return Err(format!("宣言の行末に `{STATEMENT_END}` が必要です"));
    };
    let init_text = init_text.trim();
    if init_text.is_empty() {
        return Err("既定値が空です".to_string());
    }

    // ── ③ 名前の検査 ──────────────────────────────────────────
    let name = name.trim();
    validate_name(name)?;

    // ── ④ 型（空白を除いて比較する。`vec3 < f32 >` のような書き方も許す）──
    let type_key: String = type_text.chars().filter(|c| !c.is_whitespace()).collect();
    let is_color = match type_key.as_str() {
        TYPE_F32                         => false,
        TYPE_VEC3_LONG | TYPE_VEC3_SHORT => true,
        other => return Err(format!(
            "未対応の型 `{other}`（使えるのは `{TYPE_F32}` と `{TYPE_VEC3_LONG}` です）")),
    };

    // ── ⑤ 属性の解釈 ──────────────────────────────────────────
    let (kind, resettable, bindable) = apply_attributes(attrs, is_color, notes)?;

    // ── ⑥ 既定値 ──────────────────────────────────────────────
    let default = if is_color { parse_color_init(init_text)? } else { parse_scalar_init(init_text)? };

    // ── ⑦ ラベル（無ければ識別子そのもの）────────────────────
    let label = if label.is_empty() { name.to_string() } else { label.to_string() };

    Ok(WaterShadeParamDecl { name: name.to_string(), kind, default, label, resettable, bindable })
}

/// 属性列から「UI 種別」「リセットボタンの有無」「参照バインド可否」を決める。
///
/// 未知の属性は**エラーにせず警告**にする（後続フェーズで属性が増える前提のため、
/// 新しい属性を書いたアセットが古いエンジンで丸ごと壊れないようにする）。
fn apply_attributes(
    attrs:    &[Attribute],
    is_color: bool,
    notes:    &mut Vec<String>,
) -> Result<(WaterShadeParamKind, bool, bool), String> {
    let mut range: Option<(f32, f32)> = None;
    let mut resettable = false;
    let mut bindable   = false;
    let mut color_attr = false;

    for a in attrs {
        match a.name.as_str() {
            ATTR_COLOR => color_attr = true,
            ATTR_RESET => resettable = true,
            ATTR_REF   => bindable   = true,
            ATTR_RANGE => {
                if a.args.len() != RANGE_ARG_COUNT {
                    return Err(format!(
                        "`{ATTR_SIGIL}{ATTR_RANGE}` の引数は min と max の 2 個です"));
                }
                let min = parse_number(&a.args[0])?;
                let max = parse_number(&a.args[1])?;
                if !(max > min) {
                    return Err(format!(
                        "`{ATTR_SIGIL}{ATTR_RANGE}` の max({max}) は min({min}) より \
                         大きい必要があります"));
                }
                range = Some((min, max));
            }
            other => notes.push(format!("未知の属性 `{ATTR_SIGIL}{other}` は無視しました")),
        }
    }

    // 型と属性の食い違いは「動くが意図と違う」ではなく明確な書き間違いなので弾く。
    if color_attr && !is_color {
        return Err(format!(
            "`{ATTR_SIGIL}{ATTR_COLOR}` は `{TYPE_VEC3_LONG}` 型にだけ付けられます"));
    }
    if range.is_some() && is_color {
        return Err(format!(
            "`{ATTR_SIGIL}{ATTR_RANGE}` は `{TYPE_F32}` 型にだけ付けられます"));
    }

    // `vec3<f32>` は属性を省いても色として扱う（他に UI の形が無いため）。
    let kind = if is_color {
        WaterShadeParamKind::Color
    } else if let Some((min, max)) = range {
        WaterShadeParamKind::Range { min, max }
    } else {
        WaterShadeParamKind::Float
    };
    Ok((kind, resettable, bindable))
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

/// スカラー既定値（`2.0` / `2.0f`）を読む。
fn parse_scalar_init(text: &str) -> Result<[f32; PARAM_VALUE_COMPONENTS], String> {
    let v = parse_number(text)?;
    Ok([v, 0.0, 0.0, 0.0])
}

/// 色既定値（`vec3(1.0, 0.4, 0.1)` / `vec3<f32>(...)` / `vec3(0.5)`）を読む。
///
/// 引数 1 個の書き方（splat）は WGSL と同じく「3 成分すべて同じ値」と解釈する。
fn parse_color_init(text: &str) -> Result<[f32; PARAM_VALUE_COMPONENTS], String> {
    // どの綴りのコンストラクタかを判定する（長い綴りから順に見る）。
    let compact: String = text.chars().filter(|c| !c.is_whitespace()).collect();
    let inner = VEC3_CTORS.iter()
        .find_map(|ctor| compact.strip_prefix(*ctor))
        .and_then(|s| s.strip_prefix(PAREN_OPEN))
        .and_then(|s| s.strip_suffix(PAREN_CLOSE));
    let Some(inner) = inner else {
        return Err(format!(
            "`{TYPE_VEC3_LONG}` の既定値は `vec3(r, g, b)` の形で書いてください"));
    };
    let parts: Vec<&str> = inner.split(ARG_SEPARATOR).collect();
    match parts.len() {
        // 1 個なら splat（WGSL と同じ意味）。
        1 => {
            let v = parse_number(parts[0])?;
            Ok([v, v, v, 0.0])
        }
        COLOR_COMPONENT_COUNT =>
            Ok([parse_number(parts[0])?, parse_number(parts[1])?, parse_number(parts[2])?, 0.0]),
        n => Err(format!("`vec3` の引数は 1 個か {COLOR_COMPONENT_COUNT} 個です（{n} 個ありました）")),
    }
}

/// 数値 1 個を読む（前後の空白と WGSL の型サフィックス `f` / `h` を許す）。
fn parse_number(text: &str) -> Result<f32, String> {
    let t = text.trim();
    // `1.5f` のような型サフィックスは落としてから読む（`f32` の f と紛れないよう、
    // 末尾 1 文字だけを対象にする）。
    let body = match t.chars().last() {
        Some(c) if FLOAT_SUFFIXES.contains(&c) && t.len() > 1 => &t[..t.len() - c.len_utf8()],
        _ => t,
    };
    body.parse::<f32>().map_err(|_| format!("`{t}` を数値として読めません"))
}

// ============================================================
//  宣言ブロックの除去（連結前の前処理）
// ============================================================

/// パラメータ宣言の行を**空行へ潰した**ソースを返す。
///
/// 素の naga は `override` の `vec3` も独自属性も解釈できないため、
/// 連結の前に必ずこれを通す。**行数は 1 行も変えない**ので、
/// naga が報告する行番号はアセットの行番号としてそのまま使える
/// （行番号写像 `map_reported_line` の前提が壊れない）。
pub fn strip_declarations(asset_src: &str) -> String {
    let consumed = parse_params(asset_src).consumed_lines;
    if consumed.is_empty() { return asset_src.to_string(); }

    let mut out   = String::with_capacity(asset_src.len());
    let mut first = true;
    for (idx, raw) in asset_src.split('\n').enumerate() {
        if !first { out.push('\n'); }
        first = false;
        // 除去対象の行は空にする（行そのものは残すので総行数は不変）。
        if !consumed.contains(&(idx + 1)) { out.push_str(raw); }
    }
    out
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

/// 宣言と現在値を、インスペクタが行を作れる JSON 配列にする（ワイヤ契約）。
///
/// 出力例:
/// ```json
/// [{"name":"emission_color","type":"color","label":"発光色",
///   "min":0.0,"max":0.0,"reset":true,"ref":false,
///   "binding":"","binding_ok":true,"value":[1.0,0.4,0.1,0.0]}]
/// ```
/// - `type` は `color` / `range` / `float`。C# はこの文字列で行の形（ピッカー／
///   スライダー／数値）を選ぶ。
/// - `min` / `max` は `range` のときだけ意味を持つ（他の型では 0 を送る）。
/// - `reset` は `@reset` 属性の有無。true の行だけ「デフォルトに戻す」ボタンを出す。
/// - `ref` は `@ref` 属性の有無（W8.3）。true の行は値の入力欄ではなく
///   **参照バインド行**（参照ピッカー）になる。
/// - `binding` は保存されているバインド先文字列 `"アクタ名|スロット名|変数名"`。
///   未設定なら空文字列。`ref` が false の行では常に空。
/// - `binding_ok` はバインドが**今このフレームで解決できたか**。
///   バインド未設定なら true（＝警告を出さない）、設定済みで解決できなければ false
///   （C# は ⚠ を出し、値は保存値／既定値のまま表示する）。
/// - `value` は**常に 4 成分**。解決済みバインドがあればその実値、
///   無ければ保存値、それも無ければアセットの既定値。
///
/// **解析は Rust 側だけが行う**という設計なので、C# はこの配列を表示するだけでよい。
///
/// `live` は「バインドが解決できたパラメータ名 → その実値」。
/// 解決できなかったバインドはここに**入れない**（それが `binding_ok=false` の定義）。
pub fn params_json(
    decls:    &[WaterShadeParamDecl],
    saved:    &BTreeMap<String, [f32; PARAM_VALUE_COMPONENTS]>,
    bindings: &BTreeMap<String, String>,
    live:     &BTreeMap<String, [f32; PARAM_VALUE_COMPONENTS]>,
) -> String {
    let items: Vec<serde_json::Value> = decls.iter().map(|d| {
        // 表示値の優先順位: 解決済みバインド > シーン保存値 > アセット既定値。
        let value = live.get(&d.name).copied()
            .or_else(|| saved.get(&d.name).copied())
            .unwrap_or(d.default);
        let (min, max) = match d.kind {
            WaterShadeParamKind::Range { min, max } => (min, max),
            // range 以外では使わない枠。キーを常に出しておくと C# の分岐が減る。
            _ => (0.0, 0.0),
        };
        // `@ref` でない行にバインドが残っていても表示しない（属性を外したときの
        // 孤児バインドを UI へ漏らさない。保存値としては残るので属性を戻せば復活する）。
        let binding = if d.bindable {
            bindings.get(&d.name).map(String::as_str).unwrap_or("")
        } else {
            ""
        };
        serde_json::json!({
            "name":       d.name,
            "type":       d.kind.as_str(),
            "label":      d.label,
            "min":        min,
            "max":        max,
            "reset":      d.resettable,
            "ref":        d.bindable,
            "binding":    binding,
            "binding_ok": binding.is_empty() || live.contains_key(&d.name),
            "value":      [value[0], value[1], value[2], value[3]],
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
@color
override emission_color: vec3<f32> = vec3(1.0, 0.4, 0.1);  // 発光色
@range(0.0, 10.0)
override crack_speed: f32 = 1.5;                           // 亀裂の流速
override glow_boost: f32 = 2.0;                            // 発光の強さ
fn water_shade(input: WaterShadeInput) -> vec4<f32> { return vec4<f32>(0.0); }
";
        let set = parse_params(src);
        assert!(set.warnings.is_empty(), "警告が出た: {:?}", set.warnings);
        assert_eq!(set.params.len(), 3);

        assert_eq!(set.params[0].name, "emission_color");
        assert_eq!(set.params[0].kind, WaterShadeParamKind::Color);
        assert_eq!(set.params[0].default, [1.0, 0.4, 0.1, 0.0]);
        assert_eq!(set.params[0].label, "発光色");
        assert!(!set.params[0].resettable, "@reset を書いていない");

        assert_eq!(set.params[1].name, "crack_speed");
        assert_eq!(set.params[1].kind, WaterShadeParamKind::Range { min: 0.0, max: 10.0 });
        assert_eq!(set.params[1].default, [1.5, 0.0, 0.0, 0.0]);
        assert_eq!(set.params[1].label, "亀裂の流速");

        assert_eq!(set.params[2].name, "glow_boost");
        assert_eq!(set.params[2].kind, WaterShadeParamKind::Float);
        assert_eq!(set.params[2].default, [2.0, 0.0, 0.0, 0.0]);
    }

    /// 属性は同一行にも書け、複数併記できること。
    #[test]
    fn attributes_may_be_inline_and_combined() {
        let src = "\
@reset @range(0.0, 4.0) override glow: f32 = 1.0;  // 発光
@color @reset
override tint: vec3<f32> = vec3(0.5);
";
        let set = parse_params(src);
        assert!(set.warnings.is_empty(), "{:?}", set.warnings);
        assert_eq!(set.params.len(), 2);
        assert_eq!(set.params[0].kind, WaterShadeParamKind::Range { min: 0.0, max: 4.0 });
        assert!(set.params[0].resettable, "@reset が効くこと");
        assert_eq!(set.params[1].kind, WaterShadeParamKind::Color);
        assert!(set.params[1].resettable);
        assert_eq!(set.params[1].default, [0.5, 0.5, 0.5, 0.0], "vec3(x) は splat");
    }

    /// 属性を省いた `vec3<f32>` も色として扱われること（UI の形が他に無いため）。
    #[test]
    fn vec3_without_color_attribute_is_color() {
        let set = parse_params("override tint: vec3f = vec3<f32>(1.0, 2.0, 3.0);\n");
        assert!(set.warnings.is_empty(), "{:?}", set.warnings);
        assert_eq!(set.params[0].kind, WaterShadeParamKind::Color);
        assert_eq!(set.params[0].default, [1.0, 2.0, 3.0, 0.0]);
    }

    /// ラベルが無ければ識別子名がラベルになること。
    #[test]
    fn label_falls_back_to_identifier() {
        let set = parse_params("override glow: f32 = 1.0;\n");
        assert_eq!(set.params.len(), 1);
        assert_eq!(set.params[0].label, "glow");
    }

    /// 旧構文（`//! param`）はもう解釈されないこと（W5.2 で廃止）。
    #[test]
    fn legacy_comment_syntax_is_no_longer_parsed() {
        let set = parse_params("//! param float glow = 1.0\n//! param color c = (1.0,0.0,0.0)\n");
        assert!(set.params.is_empty(), "旧構文を拾ってはならない: {:?}", set.params);
        assert!(set.warnings.is_empty(), "ただのコメントなので警告も出さない: {:?}", set.warnings);
        assert!(set.consumed_lines.is_empty(), "除去対象にもしない");
    }

    /// 宣言でない行（普通のコード・普通のコメント）は拾わないこと。
    #[test]
    fn ignores_non_declaration_lines() {
        let src = "\
// override float a = 1.0;
const e: f32 = 1.0;
var<private> f: f32;
fn overrider(x: f32) -> f32 { return x; }
";
        let set = parse_params(src);
        assert!(set.params.is_empty(), "拾ってはならない行を拾った: {:?}", set.params);
        assert!(set.warnings.is_empty(), "無関係な行で警告を出してはならない: {:?}", set.warnings);
        assert!(set.consumed_lines.is_empty(), "無関係な行を除去してはならない");
    }

    /// WGSL 本来の属性（`@group` など）だけの行には触れないこと。
    #[test]
    fn leaves_plain_wgsl_attributes_alone() {
        let src = "@group(1) @binding(9)\nvar<storage> foo: array<f32>;\n";
        let set = parse_params(src);
        assert!(set.consumed_lines.is_empty(), "WGSL の属性行を除去してはならない");
        assert!(set.warnings.is_empty(), "{:?}", set.warnings);
        assert_eq!(strip_declarations(src), src, "ソースが変わってはならない");
    }

    /// 構文エラーの行は「無視 ＋ 行番号つき警告」になること（アセット全体は壊さない）。
    #[test]
    fn malformed_lines_warn_with_line_number() {
        let src = "\
override ok: f32 = 1.0;
override missing_type = 1.0;
override missing_semicolon: f32 = 1.0
override weird: i32 = 1;
@color override bad_color: f32 = 1.0;
@range(1.0) override bad_range: f32 = 1.0;
";
        let set = parse_params(src);
        assert_eq!(set.params.len(), 1, "正しい 1 行だけが通ること: {:?}", set.params);
        assert_eq!(set.warnings.len(), 5, "壊れた 5 行ぶん警告が出ること: {:?}", set.warnings);
        assert_eq!(set.warnings[0].line, 2, "{:?}", set.warnings[0]);
        assert_eq!(set.warnings[1].line, 3, "{:?}", set.warnings[1]);
        assert_eq!(set.warnings[4].line, 6, "{:?}", set.warnings[4]);
        // 壊れていても除去はされる（残すと naga が落ちる）。
        assert_eq!(set.consumed_lines, vec![1, 2, 3, 4, 5, 6]);
    }

    /// 対応する宣言を持たない属性は警告になること。
    #[test]
    fn dangling_attribute_warns() {
        let set = parse_params("@color\nconst c: f32 = 1.0;\n");
        assert_eq!(set.warnings.len(), 1, "{:?}", set.warnings);
        assert_eq!(set.warnings[0].line, 1);
        assert!(set.warnings[0].message.contains(DECL_KEYWORD), "{:?}", set.warnings[0]);
    }

    /// 未知の属性は警告のみで、宣言自体は通ること（属性追加への前方互換）。
    #[test]
    fn unknown_attribute_warns_but_keeps_declaration() {
        let set = parse_params("@reset @future_thing override a: f32 = 1.0;\n");
        assert_eq!(set.params.len(), 1, "宣言は生きること: {:?}", set.params);
        assert!(set.params[0].resettable, "既知の属性は効くこと");
        assert_eq!(set.warnings.len(), 1);
        assert!(set.warnings[0].message.contains("未知の属性"), "{:?}", set.warnings[0]);
    }

    /// 上限を超えた宣言は「無視 ＋ 警告」で、黙って切られないこと。
    #[test]
    fn exceeding_max_warns_and_ignores() {
        let mut src = String::new();
        for i in 0..(WATER_SHADE_PARAM_MAX + 2) {
            src.push_str(&format!("override p{i}: f32 = {i}.0;\n"));
        }
        let set = parse_params(&src);
        assert_eq!(set.params.len(), WATER_SHADE_PARAM_MAX);
        assert_eq!(set.warnings.len(), 2, "超過ぶんの警告が出ること: {:?}", set.warnings);
        assert!(set.warnings[0].message.contains("上限"), "{:?}", set.warnings[0]);
    }

    /// 重複名は最初の宣言だけを採り、警告を出すこと。
    #[test]
    fn duplicate_names_keep_first() {
        let set = parse_params("override a: f32 = 1.0;\noverride a: f32 = 2.0;\n");
        assert_eq!(set.params.len(), 1);
        assert_eq!(set.params[0].default[0], 1.0, "先に書いた宣言が残ること");
        assert_eq!(set.warnings.len(), 1);
        assert!(set.warnings[0].message.contains("重複"), "{:?}", set.warnings[0]);
    }

    /// エンジン予約の接頭辞を持つ名前は弾かれること（WGSL の名前衝突を防ぐ）。
    #[test]
    fn rejects_reserved_prefixes() {
        for name in ["water_shade_x", "u_water_foo", "WATER_MAX"] {
            let set = parse_params(&format!("override {name}: f32 = 1.0;\n"));
            assert!(set.params.is_empty(), "{name} は弾かれること");
            assert_eq!(set.warnings.len(), 1);
            assert!(set.warnings[0].message.contains("予約"), "{:?}", set.warnings[0]);
        }
    }

    /// 識別子として不正な名前は弾かれること。
    #[test]
    fn rejects_invalid_identifiers() {
        for name in ["1abc", "a-b", "a.b"] {
            let set = parse_params(&format!("override {name}: f32 = 1.0;\n"));
            assert!(set.params.is_empty(), "{name} は弾かれること: {:?}", set.params);
            assert_eq!(set.warnings.len(), 1, "{name}: {:?}", set.warnings);
        }
    }

    /// range の max <= min は弾かれること（スライダーが成立しない）。
    #[test]
    fn rejects_inverted_range() {
        let set = parse_params("@range(1.0, 1.0) override a: f32 = 1.0;\n");
        assert!(set.params.is_empty());
        assert_eq!(set.warnings.len(), 1);
    }

    /// WGSL の型サフィックス付きリテラル（`1.5f`）も読めること。
    #[test]
    fn accepts_float_suffix_literals() {
        let set = parse_params("override a: f32 = 1.5f;\n@color override c: vec3<f32> = vec3(1.0f, 0.0f, 0.5f);\n");
        assert!(set.warnings.is_empty(), "{:?}", set.warnings);
        assert_eq!(set.params[0].default[0], 1.5);
        assert_eq!(set.params[1].default, [1.0, 0.0, 0.5, 0.0]);
    }

    // ── 除去（前処理）────────────────────────────────────────

    /// 宣言ブロックが除去され、**行数が変わらない**こと（行番号写像の前提）。
    #[test]
    fn strip_removes_declarations_keeping_line_count() {
        let src = "\
// @water_shading_contract 1
@color
override tint: vec3<f32> = vec3(1.0, 0.0, 0.0);  // 色
fn water_shade(i: WaterShadeInput) -> vec4<f32> { return vec4<f32>(tint, 1.0); }
";
        let stripped = strip_declarations(src);
        assert_eq!(stripped.split('\n').count(), src.split('\n').count(),
            "行数が変わると naga の行番号がずれる");
        assert!(!stripped.contains(DECL_KEYWORD), "宣言が残っている: {stripped}");
        assert!(!stripped.contains("@color"), "属性が残っている: {stripped}");
        assert!(stripped.contains("fn water_shade"), "本体は残ること: {stripped}");
        // 除去された行の位置（3 行目）が空行になっていること。
        let lines: Vec<&str> = stripped.split('\n').collect();
        assert!(lines[1].is_empty() && lines[2].is_empty());
        assert!(lines[3].starts_with("fn water_shade"), "本体の行番号が動いていない");
    }

    /// CRLF のソースでも行数・内容が保たれること。
    #[test]
    fn strip_handles_crlf() {
        let src = "@color\r\noverride c: vec3<f32> = vec3(1.0);\r\nconst K: f32 = 1.0;\r\n";
        let set = parse_params(src);
        assert_eq!(set.params.len(), 1, "CRLF でも解析できること: {:?}", set.warnings);
        let stripped = strip_declarations(src);
        assert_eq!(stripped.split('\n').count(), src.split('\n').count());
        assert!(stripped.contains("const K: f32 = 1.0;"));
        assert!(!stripped.contains(DECL_KEYWORD));
    }

    /// 宣言が 1 個も無いソースはそのまま返ること（無駄なコピーの確認も兼ねる）。
    #[test]
    fn strip_is_identity_without_declarations() {
        let src = "fn water_shade(i: WaterShadeInput) -> vec4<f32> { return vec4<f32>(0.0); }\n";
        assert_eq!(strip_declarations(src), src);
    }

    // ── GPU ブロック / JSON ──────────────────────────────────

    /// GPU ブロックは宣言順に詰められ、保存値が既定値を上書きすること。
    #[test]
    fn block_uses_saved_values_over_defaults() {
        let set = parse_params(
            "@color override c: vec3<f32> = vec3(1.0, 0.0, 0.0);\noverride f: f32 = 2.0;\n");
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
        let set = parse_params("override f: f32 = 2.0;\n");
        let mut saved = BTreeMap::new();
        saved.insert("removed_param".to_string(), [7.0, 0.0, 0.0, 0.0]);
        let block = build_block(&set.params, &saved);
        assert_eq!(block.values[0], [2.0, 0.0, 0.0, 0.0], "既定値のまま");
    }

    /// インスペクタ向け JSON が宣言順・型・現在値・リセット可否を載せること（ワイヤ契約）。
    #[test]
    fn params_json_carries_kind_range_reset_and_current_value() {
        let set = parse_params(
            "@color override c: vec3<f32> = vec3(1.0, 0.0, 0.0);  // 色\n\
             @range(0.0, 10.0) @reset override r: f32 = 1.5;\n\
             override f: f32 = 2.0;\n");
        assert!(set.warnings.is_empty(), "{:?}", set.warnings);
        let mut saved = BTreeMap::new();
        saved.insert("c".to_string(), [0.0, 1.0, 0.0, 0.0]);
        let json: serde_json::Value = serde_json::from_str(
            &params_json(&set.params, &saved, &BTreeMap::new(), &BTreeMap::new()))
            .expect("JSON として読めること");
        let arr = json.as_array().expect("配列であること");
        assert_eq!(arr.len(), 3);
        // ① 宣言順・型・ラベル
        assert_eq!(arr[0]["name"], "c");
        assert_eq!(arr[0]["type"], "color");
        assert_eq!(arr[0]["label"], "色");
        assert_eq!(arr[0]["reset"], false);
        // ② 保存値が現在値として載ること
        assert_eq!(arr[0]["value"][1], 1.0);
        // ③ range の範囲とリセット可否が載ること（ラベル省略時は識別子）
        assert_eq!(arr[1]["type"], "range");
        assert_eq!(arr[1]["min"], 0.0);
        assert_eq!(arr[1]["max"], 10.0);
        assert_eq!(arr[1]["reset"], true);
        assert_eq!(arr[1]["label"], "r");
        // ④ 保存値が無ければアセット既定値
        assert_eq!(arr[2]["type"], "float");
        assert_eq!(arr[2]["value"][0], 2.0);
        // ⑤ @ref を書いていない行はバインド行にならず、警告状態にもならない
        for row in arr {
            assert_eq!(row["ref"], false, "{row}");
            assert_eq!(row["binding"], "", "{row}");
            assert_eq!(row["binding_ok"], true, "{row}");
        }
    }

    /// `@ref` 属性が解析され、他の属性と併用できること（W8.3）。
    #[test]
    fn ref_attribute_is_parsed_and_combines() {
        let set = parse_params(
            "@ref override speed: f32 = 0.0;                 // 速さ
             @ref @reset @range(0.0, 4.0) override glow: f32 = 1.0;
             @ref @color override tint: vec3<f32> = vec3(1.0);
             override plain: f32 = 1.0;
");
        assert!(set.warnings.is_empty(), "{:?}", set.warnings);
        assert_eq!(set.params.len(), 4);
        assert!(set.params[0].bindable, "@ref が効くこと");
        assert!(set.params[1].bindable && set.params[1].resettable,
            "@ref と @reset は併用できること");
        assert_eq!(set.params[1].kind, WaterShadeParamKind::Range { min: 0.0, max: 4.0 });
        assert!(set.params[2].bindable);
        assert_eq!(set.params[2].kind, WaterShadeParamKind::Color);
        assert!(!set.params[3].bindable, "@ref を書いていない行は false");
    }

    /// JSON にバインド状態が載ること（未設定・解決成功・解決失敗の 3 状態。W8.3）。
    #[test]
    fn params_json_carries_binding_state() {
        let set = parse_params(
            "@ref override a: f32 = 1.0;
             @ref override b: f32 = 2.0;
             @ref override c: f32 = 3.0;
");
        assert!(set.warnings.is_empty(), "{:?}", set.warnings);
        let saved = BTreeMap::from([("b".to_string(), [9.0, 0.0, 0.0, 0.0])]);
        let bindings = BTreeMap::from([
            ("b".to_string(), "Sun|MainLight|intensity".to_string()),
            ("c".to_string(), "Gone|MainLight|intensity".to_string()),
        ]);
        // b は解決できた、c は解決できなかった、という状態を作る。
        let live = BTreeMap::from([("b".to_string(), [5.5, 0.0, 0.0, 0.0])]);
        let json: serde_json::Value =
            serde_json::from_str(&params_json(&set.params, &saved, &bindings, &live))
                .expect("JSON として読めること");
        let arr = json.as_array().expect("配列であること");

        // ① バインド未設定 = 警告なし・アセット既定値
        assert_eq!(arr[0]["ref"], true);
        assert_eq!(arr[0]["binding"], "");
        assert_eq!(arr[0]["binding_ok"], true);
        assert_eq!(arr[0]["value"][0], 1.0);
        // ② 解決成功 = 実値が現在値になり、保存値(9.0)より優先される
        assert_eq!(arr[1]["binding"], "Sun|MainLight|intensity");
        assert_eq!(arr[1]["binding_ok"], true);
        assert_eq!(arr[1]["value"][0], 5.5);
        // ③ 解決失敗 = 警告つき・値は保存値／既定値のまま
        assert_eq!(arr[2]["binding"], "Gone|MainLight|intensity");
        assert_eq!(arr[2]["binding_ok"], false);
        assert_eq!(arr[2]["value"][0], 3.0);
    }

    /// `@ref` が外れた宣言に残った孤児バインドは JSON へ漏れないこと（W8.3）。
    #[test]
    fn orphan_binding_on_non_ref_param_is_hidden() {
        let set = parse_params("override a: f32 = 1.0;
");
        let bindings = BTreeMap::from([("a".to_string(), "Sun|MainLight|intensity".to_string())]);
        let json: serde_json::Value = serde_json::from_str(
            &params_json(&set.params, &BTreeMap::new(), &bindings, &BTreeMap::new()))
            .expect("JSON として読めること");
        let arr = json.as_array().unwrap();
        assert_eq!(arr[0]["ref"], false);
        assert_eq!(arr[0]["binding"], "", "孤児バインドは表示しない");
        assert_eq!(arr[0]["binding_ok"], true, "⚠ も出さない");
    }

    /// 宣言が無ければ空配列（インスペクタは行を 1 つも作らない）。
    #[test]
    fn params_json_is_empty_array_without_declarations() {
        assert_eq!(
            params_json(&[], &BTreeMap::new(), &BTreeMap::new(), &BTreeMap::new()), "[]");
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
