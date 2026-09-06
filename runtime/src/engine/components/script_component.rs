// ============================================================
//  script_component.rs — ScriptComponent / PlaceholderScriptSlot
//
//  ScriptComponent は C# CLR へのブリッジコンポーネント。
//  毎フレームのライフサイクル呼び出しは
//  engine::systems::script_system の ScriptSystem 群が担う。
//
//  PlaceholderScriptSlot はエディタモード（CLR 不使用）のフォールバック。
//  スクリプトのパスとフィールド値だけ保持し、シリアライズ時に復元できる。
// ============================================================

use std::collections::BTreeMap;
use std::sync::Arc;
use serde::{Deserialize, Serialize};
use crate::engine::ecs::{Component, Entity};
use crate::engine::ecs::schedule::Phase;
use crate::engine::core::clock::FrameContext;
use crate::engine::core::scripting::{ScriptingHost, RawFrameContext};

// ─── ScriptComponentData ──────────────────────────────────────────────────────

/// シリアライズ用データ。
///
/// - type_name : スクリプトの .cs ファイルパス、または C# 型名
/// - fields    : [SerializeField] フィールドの値（フィールド名 → 文字列値）。
///               BTreeMap を使いシリアライズ順を安定させる。
#[derive(Serialize, Deserialize, Clone)]
pub struct ScriptComponentData {
    pub type_name: String,
    #[serde(default, skip_serializing_if = "BTreeMap::is_empty")]
    pub fields: BTreeMap<String, String>,
}

// ─── フィールド定義と引き継ぎ（ホットリロード）─────────────────────────────────

/// 新アセンブリ側の `[SerializeField]` フィールド定義 1 件。
///
/// CLR の `ScriptBridge.DescribeSerializeFields` が返す JSON 要素に対応する。
/// - `name`  : ドット区切りのフィールドパス（例 `"stats.hp"`）
/// - `type_tag`: `float`/`double`/`int`/`long`/`short`/`bool`/`string`/
///               `reference`/`scriptevent`/`unsupported`、および配列フィールドの
///               `array:<要素型タグ>`（例 `array:float` / `array:reference`）。
///               配列の値は JSON 配列文字列（例 `[1.0,2.5]` / `["a","b"]`）で保存される。
///               `scriptevent`（C# の `SEED.ScriptEvent` = UnityEvent 相当）も
///               値は JSON 配列文字列で、要素は結線 1 件を表す JSON オブジェクト
///               （`[{"actor":"...","script":"...","method":"...","argKind":"none","arg":""}]`）。
/// - `default_value`: 宣言時初期値の文字列化（`reference`/`unsupported` は空文字）
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ScriptFieldDef {
    pub name: String,
    #[serde(rename = "type")]
    pub type_tag: String,
    #[serde(rename = "default")]
    pub default_value: String,
    /// 構造体配列（`array:struct:Xxx`）のときだけ入る、要素構造体のメンバ定義。
    /// それ以外のフィールドでは空 Vec（CLR も `members` キー自体を出さない）。
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub members: Vec<ScriptStructMemberDef>,
}

/// 構造体配列の要素メンバ 1 件の定義（CLR 側 `ScriptStructMemberInfo` に対応）。
///
/// - `name`     : JSON オブジェクトのキー（＝メンバのフィールド名）
/// - `label`    : インスペクタ表示名（Rust では判定に使わないが、往復の情報欠落を避けて保持する）
/// - `type_tag` : `float`/`int`/`bool`/`string`/`reference`/`scriptevent` と
///                入れ子配列 `array:<要素型タグ>`
/// - `default_value`: 宣言時初期値の文字列化
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ScriptStructMemberDef {
    pub name: String,
    #[serde(default)]
    pub label: String,
    #[serde(rename = "type")]
    pub type_tag: String,
    #[serde(rename = "default", default)]
    pub default_value: String,
}

/// 保存済みの値が、新しいフィールド定義の型で解釈できるかを判定する。
///
/// 型タグは CLR 側 `ConvertValue` の対応型と 1 対 1 に対応させている。
/// `string` / `reference` は任意の文字列を受け付ける（前者は素の文字列、
/// 後者はアクター名の文字列として保存されるため）。
/// `scriptevent` は「JSON オブジェクトの配列」であることだけを見る
/// （キーの中身は CLR 側 `ScriptEvent.Decode` が寛容に読むので、
///  ここで厳しく見ると将来キーを足したときに結線が全消しになる）。
/// `unsupported`（インスペクタが扱えない型）は常に既定値へ落とす。
fn value_matches_type(type_tag: &str, value: &str, members: &[ScriptStructMemberDef]) -> bool {
    // 配列フィールド（"array:要素型タグ"）は JSON 配列として解釈し、
    // すべての要素が要素型タグに適合するかを見る。
    if let Some(element_tag) = type_tag.strip_prefix(ARRAY_TYPE_TAG_PREFIX) {
        // 構造体配列（"array:struct:Xxx"）は JSON オブジェクト配列として解釈する
        if element_tag.starts_with(STRUCT_TYPE_TAG_PREFIX) {
            return struct_array_value_matches(members, value);
        }
        return array_value_matches_type(element_tag, value);
    }

    match type_tag {
        "float" | "double"        => value.trim().parse::<f64>().is_ok(),
        "int" | "long" | "short"  => value.trim().parse::<i64>().is_ok(),
        "bool"                    => value == "true" || value == "false",
        "string" | "reference"    => true,
        SCRIPT_EVENT_TYPE_TAG     => script_event_value_matches(value),
        _                         => false,
    }
}

/// ScriptEvent フィールドの型タグ（C# 側 `SEED.ScriptEvent.TypeTag` と一致させること）。
const SCRIPT_EVENT_TYPE_TAG: &str = "scriptevent";

/// 保存済みの値が ScriptEvent（UnityEvent 相当）として引き継げるかを判定する。
///
/// 【規則】JSON 配列で、要素がすべて JSON オブジェクトであること。
/// 空配列（＝結線なし）は当然適合する。
///
/// 【なぜキーの中身を見ないのか】
/// 結線 1 件のキー集合は将来増える想定（例: 同型スクリプトの複数スロットを指す `"slot"`）で、
/// CLR 側 `ScriptEvent.Decode` は未知キーを無視し欠損キーを既定値で埋める寛容デコードになっている。
/// ここでキーを厳密に照合すると、エディタとランタイムの版が少しずれただけで
/// 「結線が全部消える」データ喪失になるため、外形（オブジェクトの配列）だけを見る。
fn script_event_value_matches(value: &str) -> bool {
    let Ok(parsed) = serde_json::from_str::<serde_json::Value>(value.trim()) else { return false };
    let serde_json::Value::Array(items) = parsed else { return false };
    items.iter().all(|item| item.is_object())
}

/// 配列フィールドの型タグ接頭辞（C# 側 `SEED.ScriptArray.TypeTagPrefix` と一致させること）。
const ARRAY_TYPE_TAG_PREFIX: &str = "array:";

/// 構造体要素の型タグ接頭辞（C# 側 `SEED.ScriptStructArray.StructTypeTagPrefix` と一致させること）。
const STRUCT_TYPE_TAG_PREFIX: &str = "struct:";

/// 保存済みの JSON オブジェクト配列文字列が、構造体配列として引き継げるかを判定する。
///
/// 【規則】
/// - 値全体が JSON 配列で、要素がすべて JSON オブジェクトであること
/// - 各オブジェクトについて、**宣言に存在するメンバと同名のキー**は
///   そのメンバの型タグに適合する JSON 値であること
/// - 宣言に無いキー（削除されたメンバの残骸）は**無視**する
/// - 宣言にあるがキーが無いメンバ（新設メンバ）は**欠損として許す**
///   （CLR 側 `ScriptStructArray.DecodeMembers` が宣言時初期値で埋める）
///
/// 【なぜ寛容側なのか】
/// 構造体はメンバを足し引きしながら育てるのが普通で、そのたびに
/// 「レベル定義リスト全体が消える」のはデータ喪失に等しい。
/// 一方、同名メンバの型が変わった（`float` → `string` など）場合は
/// 値の意味が変わるので、非配列フィールドと同じく引き継がず既定値へ戻す。
///
/// なお構造体名（`struct:Xxx` の `Xxx`）は旧値側に記録が無く比較できないため、
/// 一致判定はメンバ名＋型のみで行う（別構造体へ差し替えられた場合は
/// メンバが噛み合わず、結果として既定値へ落ちる）。
fn struct_array_value_matches(members: &[ScriptStructMemberDef], value: &str) -> bool {
    let Ok(parsed) = serde_json::from_str::<serde_json::Value>(value.trim()) else { return false };
    let serde_json::Value::Array(items) = parsed else { return false };

    items.iter().all(|item| {
        let Some(obj) = item.as_object() else { return false };
        members.iter().all(|m| match obj.get(&m.name) {
            None      => true,   // 新設メンバ（欠損）は既定値で埋まるので許す
            Some(v)   => json_value_matches_tag(&m.type_tag, v),
        })
    })
}

/// JSON 値 1 個が型タグに適合するかを判定する（構造体メンバ用）。
///
/// メンバの入れ子は 1 段まで（`array:<スカラ要素型>`）で、
/// 構造体の中の構造体は CLR 側が非対応にしているためここでも扱わない。
fn json_value_matches_tag(type_tag: &str, value: &serde_json::Value) -> bool {
    if let Some(element_tag) = type_tag.strip_prefix(ARRAY_TYPE_TAG_PREFIX) {
        let Some(items) = value.as_array() else { return false };
        return items.iter().all(|item| json_scalar_matches_tag(element_tag, item));
    }
    json_scalar_matches_tag(type_tag, value)
}

/// JSON 値 1 個がスカラ型タグに適合するかを判定する。
/// 数値・真偽・文字列の判定規則は `array_value_matches_type` と同一にする。
fn json_scalar_matches_tag(type_tag: &str, value: &serde_json::Value) -> bool {
    match type_tag {
        "float" | "double"       => value.is_number(),
        "int" | "long" | "short" => value.is_i64() || value.is_u64(),
        "bool"                   => value.is_boolean(),
        // 参照は未設定を null で書く場合があるので許容する（CLR 側は空文字として読む）
        "string" | "reference"   => value.is_string() || value.is_null(),
        // 構造体メンバの ScriptEvent は JSON 配列がそのまま入れ子で埋め込まれている。
        // 外形（配列であること）だけを見る点は script_event_value_matches と同じ理由。
        SCRIPT_EVENT_TYPE_TAG    => value.is_array(),
        _                        => false,
    }
}

/// 保存済みの JSON 配列文字列が、要素型タグで解釈できるかを判定する。
///
/// 【規則】
/// - 値全体が JSON 配列であること（配列でなければ引き継がない）
/// - 空配列は常に適合（要素が無いので型の食い違いは起きない）
/// - 数値要素は JSON の数値、真偽要素は JSON の真偽値、
///   文字列 / 参照要素は JSON の文字列であること
/// - 要素型タグ自体が未対応（`unsupported` など）なら常に不適合
///
/// 【なぜ要素を 1 つずつ見るのか】
/// `float[]` → `string[]` のような要素型の変更でも配列としては JSON なので、
/// 外形だけ見ると通ってしまう。要素の JSON 型まで確かめて初めて
/// 非配列フィールドと同じ厳しさ（型が変わったら既定値へ戻す）になる。
fn array_value_matches_type(element_tag: &str, value: &str) -> bool {
    let Ok(parsed) = serde_json::from_str::<serde_json::Value>(value.trim()) else { return false };
    let serde_json::Value::Array(items) = parsed else { return false };

    items.iter().all(|item| match element_tag {
        // 実数は整数値も受け付ける（int[] → float[] は値の意味が保たれる）
        "float" | "double"       => item.is_number(),
        // 整数は小数を弾く（スカラの "12.5" → int が引き継がれないのと同じ扱い）
        "int" | "long" | "short" => item.is_i64() || item.is_u64(),
        "bool"                   => item.is_boolean(),
        "string" | "reference"   => item.is_string(),
        _                        => false,
    })
}

/// ホットリロード時のフィールド値引き継ぎ（純粋関数）。
///
/// 【規則】新しいフィールド定義に**存在し、かつ旧値がその型で解釈できる**ものだけを残す。
/// - 名前・型ともに一致            → 旧値を引き継ぐ
/// - 新設フィールド（旧値が無い）  → **含めない**
/// - 型が変わった（旧値が解釈不能）→ **含めない**
/// - 定義から消えた（削除・改名）  → **含めない**
///
/// 【なぜ「既定値を書き込む」ではなく「含めない」なのか】
/// `fields` は「ユーザーがインスペクタで**明示的に設定した値**」を表すマップであり、
/// 未設定フィールドはキーごと存在しないのが従来からの規約である
/// （インスペクタはキーが無ければスクリプト宣言側の初期値を表示し、
///  CLR インスタンスも生成直後の初期値のままになる）。
/// ここで既定値を書き込んでしまうと、その時点の初期値が「設定済みの値」として
/// 固定され、後からスクリプト側の初期化子を書き換えても反映されなくなる。
///
/// 戻り値のキー集合は必ず `defs` のフィールドパス集合の部分集合になる。
pub fn carry_over_script_fields(
    old:  &BTreeMap<String, String>,
    defs: &[ScriptFieldDef],
) -> BTreeMap<String, String> {
    let mut out = BTreeMap::new();
    for def in defs {
        if let Some(v) = old.get(&def.name) {
            if value_matches_type(&def.type_tag, v, &def.members) {
                out.insert(def.name.clone(), v.clone());
            }
        }
    }
    out
}

// ─── ScriptComponent ──────────────────────────────────────────────────────────

/// C# スクリプトを保持するコンポーネント。
///
/// ライフサイクル（begin_frame, update など）の呼び出しは
/// ScriptSystem が World をクエリして行う（run_phase 参照）。
pub struct ScriptComponent {
    pub(crate) host:      Arc<ScriptingHost>,
    pub(crate) handle:    isize,
    pub(crate) type_name: String,
    /// [SerializeField] フィールドの現在値（シリアライズ・再生成時の復元用）。
    pub fields: BTreeMap<String, String>,
    /// このスクリプトが乗る GameObject（所有 Entity）。
    /// スクリプトから gameObject/transform で所有オブジェクトへアクセスするために使う。
    /// Scene が毎フレーム（BeginFrame 前）に Actor ツリーから同期する。未同期時は None。
    pub(crate) owner: Option<Entity>,
    /// 実効アクティブフラグ（アクターの active 継承 × スロットの enabled）。
    /// false の間はライフサイクル関数・物理イベントが呼ばれない（Unity の enabled 相当）。
    /// Scene が毎フレーム（BeginFrame 前）に Actor ツリーから同期する。
    pub(crate) active: bool,
    /// OnStart 済みフラグ。
    ///
    /// ScriptSystem が BeginFrame フェーズで「まだ false のスクリプト」に対して
    /// OnStart を呼んでから true にする（＝初回 BeginFrame の直前に 1 回だけ）。
    /// OnDestroy はこのフラグが true のときだけ呼ばれる（OnStart と 1 対 1 で対応させ、
    /// 一度も動いていない編集モードのインスタンスでは発火させないため）。
    pub(crate) started: bool,
    /// 参照フィールド（他アクター／他コンポーネントへのハンドル）の再解決が必要か。
    ///
    /// 参照フィールドは「アクター名（＋スロット名）」の文字列として保存されており、
    /// 実体へ解決するには World と Actor ツリーが必要になる。それらはスクリプト
    /// フェーズ実行中しか公開されないため、生成時・フィールド設定時にはこのフラグを
    /// 立てるだけにしておき、ScriptSystem が BeginFrame（OnStart の直前）で
    /// 解決を発行してからフラグを下ろす。
    pub(crate) refs_dirty: bool,
}

impl ScriptComponent {
    /// CLR 上でスクリプトを生成して返す。生成に失敗した場合は None。
    pub fn new(host: Arc<ScriptingHost>, type_name: impl Into<String>) -> Option<Self> {
        let type_name = type_name.into();
        let bytes     = type_name.as_bytes();
        let handle    = unsafe { (host.create_fn)(bytes.as_ptr(), bytes.len() as i32) };
        if handle == 0 { return None; }
        Some(Self {
            host, handle, type_name,
            fields:  BTreeMap::new(),
            owner:   None,
            active:  true,
            started: false,
            // 生成直後は必ず一度解決を通す（フィールド無しでも副作用は無い）
            refs_dirty: true,
        })
    }

    /// フィールド値付きでスクリプトを生成する（シーンロード・リロード時の復元用）。
    pub fn new_with_fields(
        host:      Arc<ScriptingHost>,
        type_name: impl Into<String>,
        fields:    BTreeMap<String, String>,
    ) -> Option<Self> {
        let mut sc = Self::new(host, type_name)?;
        for (name, value) in &fields {
            sc.apply_field_ffi(name, value);
        }
        sc.fields = fields;
        Some(sc)
    }

    pub fn type_name(&self) -> &str { &self.type_name }

    /// ホットリロード用: 新アセンブリの定義に合わせて保存済みフィールド値を引き継ぎつつ生成する。
    ///
    /// 【なぜ new_with_fields と分けるか】
    /// `new_with_fields` は「保存値をそのまま全部書き込み、fields にもそのまま残す」。
    /// 再コンパイル後はフィールドが増減・改名・型変更されている可能性があるため、
    /// そのままでは
    ///   - 消えたフィールドの値が fields に残り続ける
    ///   - 型が変わったフィールドで「CLR は既定値・インスペクタ表示は旧値」と食い違う
    /// という不整合が起きる。ここでは
    ///   1. まず**既定値のまま**インスタンスを生成し
    ///   2. CLR から新しいフィールド定義（名前・型タグ・既定値）を吸い出し
    ///   3. 純粋関数 `carry_over_script_fields` で引き継ぎ後の値を決め
    ///   4. その結果だけを書き込む
    /// という手順を踏む。定義を取得できなかった場合（旧 CLR・例外など）は
    /// 従来どおり保存値をそのまま適用するフォールバックに落ちる。
    pub fn new_with_carried_fields(
        host:      Arc<ScriptingHost>,
        type_name: impl Into<String>,
        old_fields: BTreeMap<String, String>,
    ) -> Option<Self> {
        let mut sc = Self::new(host, type_name)?;

        // 生成直後（＝宣言時初期値のまま）でなければ既定値が読めないので、ここで取得する
        let merged = match sc.describe_serialize_fields() {
            Some(defs) => carry_over_script_fields(&old_fields, &defs),
            None       => old_fields,
        };

        for (name, value) in &merged {
            sc.apply_field_ffi(name, value);
        }
        sc.fields = merged;
        Some(sc)
    }

    /// CLR から `[SerializeField]` フィールド定義のスナップショットを取得する。
    ///
    /// 取得できない（CLR がエラーを返した・JSON が壊れている）場合は `None`。
    /// フィールドが 1 つも無いスクリプトでは `Some(空 Vec)` が返る
    /// （＝「定義が空」と「取得失敗」を呼び出し側で区別できる）。
    pub fn describe_serialize_fields(&self) -> Option<Vec<ScriptFieldDef>> {
        /// 最初に試すバッファ長。大半のスクリプトはこの範囲に収まる。
        const INITIAL_CAPACITY: usize = 4096;

        let mut buf = vec![0u8; INITIAL_CAPACITY];
        let mut written = unsafe {
            (self.host.describe_fields_fn)(self.handle, buf.as_mut_ptr(), buf.len() as i32)
        };

        // 負値 = バッファ不足。必要量が返るので確保し直して 1 度だけ再試行する。
        if written < 0 {
            let needed = (-written) as usize;
            buf = vec![0u8; needed];
            written = unsafe {
                (self.host.describe_fields_fn)(self.handle, buf.as_mut_ptr(), buf.len() as i32)
            };
        }
        if written <= 0 { return None; }

        let json = std::str::from_utf8(&buf[..written as usize]).ok()?;
        serde_json::from_str::<Vec<ScriptFieldDef>>(json).ok()
    }


    /// [SerializeField] フィールドの値を設定し、CLR インスタンスにも即時反映する。
    ///
    /// 参照フィールドは CLR 側で保留キューに積まれるだけなので、
    /// 再解決が必要であることを示す refs_dirty を立てておく
    /// （次の BeginFrame で ScriptSystem が解決を発行する）。
    pub fn set_field(&mut self, name: &str, value: &str) {
        self.apply_field_ffi(name, value);
        self.fields.insert(name.to_string(), value.to_string());
        self.refs_dirty = true;
    }

    /// 指定パスのフィールドが参照フィールド型（GameObject / コンポーネントハンドル）かを
    /// CLR 側のリフレクションで判定する。
    ///
    /// アクタリネーム時の参照追従（値が旧アクタ名に一致するフィールドの書き換え）で、
    /// 「たまたま同じ文字列を持つプレーンな string フィールド」を誤書き換えしないための
    /// 型ゲート。World へはアクセスしないため、スクリプトフェーズ外でも安全に呼べる。
    pub fn is_reference_field(&self, name: &str) -> bool {
        let n = name.as_bytes();
        unsafe { (self.host.is_ref_field_fn)(self.handle, n.as_ptr(), n.len() as i32) != 0 }
    }

    /// `[SerializeField, Bindable]` フィールドの**実行中の値**を読む（Phase W8.3）。
    ///
    /// `want_components` は要求する成分数（`f32` = 1、`vec3` = 3）。
    /// **CLR 側が返した成分数と厳密一致したときだけ `Some`** を返す
    /// （成分の部分取り出しをしない契約。`vec3` から X だけ取る等はしない）。
    ///
    /// ## なぜ `fields` マップを読まないのか
    /// Rust 側の `fields` は**編集時のシリアライズ値**であり、Play 中に
    /// スクリプトが書き換えた値は反映されない。バインドの目的は
    /// 「今この瞬間のスクリプトの値をシェーダへ流す」ことなので、
    /// 正典は常に CLR 側の実インスタンスである。
    ///
    /// ## `[Bindable]` の検証場所
    /// **CLR 側（`ScriptBridge.ReadFieldFloats`）が毎回検証する。**
    /// エディタでの設定時にも検証するが、設定後にスクリプトから属性が外れる／
    /// フィールドが消えることがあるため、読み取り時の検証を正典にしている
    /// （＝属性を外した瞬間からバインドは解決失敗になり、⚠ が出る）。
    ///
    /// World へは一切触れないため、スクリプトフェーズ外（描画準備中・
    /// インスペクタ更新中）でも安全に呼べる。
    pub fn read_bindable_field(
        &self,
        name:            &str,
        want_components: usize,
    ) -> Option<[f32; crate::engine::binding::catalog::BINDING_VALUE_COMPONENTS]> {
        use crate::engine::binding::catalog::BINDING_VALUE_COMPONENTS;
        let n = name.as_bytes();
        // 未使用成分は 0 のまま返す（vec4 として運ぶ規約）。
        let mut buf = [0.0f32; BINDING_VALUE_COMPONENTS];
        let written = unsafe {
            (self.host.read_field_floats_fn)(
                self.handle, n.as_ptr(), n.len() as i32,
                buf.as_mut_ptr(), BINDING_VALUE_COMPONENTS as i32,
            )
        };
        // 0 以下 = 読めなかった。成分数不一致 = 型が違う（どちらも解決失敗）。
        if written <= 0 || written as usize != want_components { return None; }
        Some(buf)
    }

    /// CLR インスタンスへフィールド値を FFI 経由で書き込む（内部用）。
    fn apply_field_ffi(&self, name: &str, value: &str) {
        let n = name.as_bytes();
        let v = value.as_bytes();
        unsafe {
            (self.host.set_field_fn)(
                self.handle,
                n.as_ptr(), n.len() as i32,
                v.as_ptr(), v.len() as i32,
            );
        }
    }

    /// 指定フェーズに対応するライフサイクルメソッドを CLR 側で実行する。
    /// 所有 Entity を ctx に載せて渡すことで、スクリプトの gameObject/transform が
    /// 自分のオブジェクトを参照できるようにする。
    pub fn run_phase(&self, phase: Phase, ctx: &FrameContext) {
        Self::run_phase_raw(&self.host, self.handle, self.owner, phase, ctx);
    }

    /// World 借用を持たずにフェーズを実行する（ScriptSystem が事前収集したハンドル用）。
    ///
    /// C# のライフサイクル内から transform 等のアクセサが呼ばれると World を可変で
    /// 触るため、呼び出し側は World への参照を一切保持せずにこれを呼ぶ必要がある。
    /// そのため必要な値（host/handle/owner）だけを受け取る形にしている。
    pub fn run_phase_raw(
        host:   &ScriptingHost,
        handle: isize,
        owner:  Option<Entity>,
        phase:  Phase,
        ctx:    &FrameContext,
    ) {
        let raw = RawFrameContext::new(ctx, owner);
        let f = match phase {
            Phase::BeginFrame     => host.begin_frame_fn,
            Phase::EarlyUpdate    => host.early_update_fn,
            Phase::Update         => host.update_fn,
            Phase::ConstantUpdate => host.constant_update_fn,
            Phase::LateUpdate     => host.late_update_fn,
            Phase::Render         => host.render_fn,
            Phase::EndFrame       => host.end_frame_fn,
        };
        unsafe { f(handle, &raw); }
    }

    /// 保留中の [SerializeField] 参照フィールドを解決して CLR インスタンスへ注入する。
    ///
    /// 参照は「アクター名（＋スロット名）」の文字列で保存されており、解決には
    /// World と Actor ツリーが必要になる。そのため **必ずスクリプトフェーズ実行中**
    /// （`with_world` / `with_actors` でポインタが公開されている間）に呼ぶこと。
    /// run_phase_raw と同じく World 借用を持たない呼び出し口である。
    ///
    /// ScriptSystem が BeginFrame で OnStart より前に発行するため、
    /// ユーザーの OnStart / Update からは常に解決済みの参照が見える。
    pub fn resolve_references_raw(host: &ScriptingHost, handle: isize) {
        unsafe { (host.resolve_refs_fn)(handle); }
    }

    /// OnStart（初回ライフサイクル直前の 1 回限りの通知）を CLR 側で実行する。
    ///
    /// run_phase_raw と同じく World 借用を持たない呼び出し口。
    /// ユーザー側 OnStart は引数を取らないが、gameObject / transform を束縛できるよう
    /// 所有エンティティだけを渡す（未束縛時は u32::MAX = C# の Entity.None）。
    pub fn run_on_start_raw(host: &ScriptingHost, handle: isize, owner: Option<Entity>) {
        let (index, generation) = entity_to_raw(owner);
        unsafe { (host.on_start_fn)(handle, index, generation); }
    }

    /// 物理イベント（衝突・トリガー）をスクリプトへ通知する。
    ///
    /// kind は RawPhysicsEvent のコメント参照（0=ColEnter .. 4=TrigExit）。
    /// run_phase_raw と同様、呼び出し側は World への参照を保持せずに呼ぶこと
    /// （C# コールバック内から transform 等のアクセサが World を可変で触るため）。
    pub fn run_physics_event_raw(
        host:       &ScriptingHost,
        handle:     isize,
        kind:       i32,
        self_owner: Entity,
        other:      Option<Entity>,
    ) {
        use crate::engine::core::scripting::RawPhysicsEvent;
        let (other_index, other_generation) = entity_to_raw(other);
        let raw = RawPhysicsEvent {
            kind,
            self_index:      self_owner.index(),
            self_generation: self_owner.generation(),
            other_index,
            other_generation,
        };
        unsafe { (host.physics_event_fn)(handle, &raw); }
    }

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> ScriptComponentData {
        ScriptComponentData {
            type_name: self.type_name.clone(),
            fields:    self.fields.clone(),
        }
    }
}

/// Option<Entity> を FFI 表現 (index, generation) へ変換する。
/// None は index = u32::MAX（C# の Entity.None）で表す。
fn entity_to_raw(entity: Option<Entity>) -> (u32, u32) {
    match entity {
        Some(e) => (e.index(), e.generation()),
        None    => (u32::MAX, 0),
    }
}

impl Drop for ScriptComponent {
    /// スクリプトインスタンスの破棄。
    ///
    /// CLR ハンドルを解放する前に OnDestroy を 1 回だけ通知する。
    /// Drop はあらゆる破棄経路（アクター破棄 / シーン遷移 / Play 終了 /
    /// スクリプトのホットリロード）の共通の出口なので、ここに置くことで
    /// 通知漏れが原理的に起きない。
    ///
    /// - OnStart 済み（started）のインスタンスにだけ通知する。編集モードで生成され
    ///   一度も動いていないインスタンスでは発火しない（OnStart と 1 対 1 対応）。
    /// - 通知中は World ポインタが公開されていない（Drop はフェーズ外で起きる）ため、
    ///   OnDestroy 内からのシーンアクセスは既定値を返す。さらに再入ガードを張り、
    ///   OnDestroy 内の Instantiate / Destroy は明示的に無視する。
    fn drop(&mut self) {
        if self.started {
            let (index, generation) = entity_to_raw(self.owner);
            crate::engine::core::scripting::with_on_destroy_guard(|| {
                unsafe { (self.host.on_destroy_fn)(self.handle, index, generation); }
            });
        }
        unsafe { (self.host.destroy_fn)(self.handle); }
    }
}

impl Component for ScriptComponent {}

// ─── PlaceholderScriptSlot ────────────────────────────────────────────────────

/// CLR 不使用のエディタモードでスクリプトスロットを保持するフォールバック。
/// スクリプトパスとフィールド値のみ保持し、CLR 起動後に ScriptComponent へ変換する。
pub struct PlaceholderScriptSlot {
    pub script_path: String,
    /// [SerializeField] フィールドの値（ラウンドトリップ保存用）。
    pub fields: BTreeMap<String, String>,
}

impl PlaceholderScriptSlot {
    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> ScriptComponentData {
        ScriptComponentData {
            type_name: self.script_path.clone(),
            fields:    self.fields.clone(),
        }
    }
}

impl Component for PlaceholderScriptSlot {}

// ============================================================
//  テスト — フィールド値の引き継ぎ（純粋関数のみ。CLR は不要）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用のフィールド定義を組み立てる小ヘルパー。
    fn def(name: &str, type_tag: &str, default_value: &str) -> ScriptFieldDef {
        ScriptFieldDef {
            name:          name.to_string(),
            type_tag:      type_tag.to_string(),
            default_value: default_value.to_string(),
            members:       Vec::new(),
        }
    }

    /// 構造体配列フィールド定義を組み立てる小ヘルパー。
    /// members は (メンバ名, 型タグ) の並びで渡す。
    fn struct_def(name: &str, struct_name: &str, members: &[(&str, &str)]) -> ScriptFieldDef {
        ScriptFieldDef {
            name:          name.to_string(),
            type_tag:      format!("array:struct:{struct_name}"),
            default_value: "[]".to_string(),
            members:       members
                .iter()
                .map(|(n, t)| ScriptStructMemberDef {
                    name:          n.to_string(),
                    label:         n.to_string(),
                    type_tag:      t.to_string(),
                    default_value: String::new(),
                })
                .collect(),
        }
    }

    /// 旧値マップを組み立てる小ヘルパー。
    fn old(pairs: &[(&str, &str)]) -> BTreeMap<String, String> {
        pairs.iter().map(|(k, v)| (k.to_string(), v.to_string())).collect()
    }

    /// 名前と型が一致するフィールドは旧値をそのまま引き継ぐ。
    #[test]
    fn keeps_value_when_name_and_type_match() {
        let defs = [
            def("speed", "float", "1"),
            def("count", "int", "0"),
            def("flag", "bool", "false"),
            def("label", "string", ""),
        ];
        let merged = carry_over_script_fields(
            &old(&[("speed", "12.5"), ("count", "7"), ("flag", "true"), ("label", "hello")]),
            &defs,
        );
        assert_eq!(merged.get("speed").map(String::as_str), Some("12.5"));
        assert_eq!(merged.get("count").map(String::as_str), Some("7"));
        assert_eq!(merged.get("flag").map(String::as_str),  Some("true"));
        assert_eq!(merged.get("label").map(String::as_str), Some("hello"));
    }

    /// 新設フィールドはマップに含めない（＝スクリプト宣言側の既定値が使われる）。
    #[test]
    fn new_field_is_left_unset() {
        let defs = [def("speed", "float", "1"), def("hp", "int", "100")];
        let merged = carry_over_script_fields(&old(&[("speed", "12.5")]), &defs);
        assert!(!merged.contains_key("hp"));
        assert_eq!(merged.get("speed").map(String::as_str), Some("12.5"));
    }

    /// 型が変わったフィールドは旧値を捨てる（＝スクリプト宣言側の既定値へ戻る）。
    #[test]
    fn type_change_drops_stale_value() {
        // float → int（"12.5" は整数として解釈できない）
        let merged = carry_over_script_fields(
            &old(&[("speed", "12.5")]),
            &[def("speed", "int", "3")],
        );
        assert!(!merged.contains_key("speed"));

        // string → bool（"hello" は真偽値ではない）
        let merged = carry_over_script_fields(
            &old(&[("flag", "hello")]),
            &[def("flag", "bool", "false")],
        );
        assert!(!merged.contains_key("flag"));
    }

    /// int → float は数値として解釈できるので引き継ぐ（値の意味が保たれるため）。
    #[test]
    fn widening_numeric_type_keeps_value() {
        let merged = carry_over_script_fields(
            &old(&[("hp", "7")]),
            &[def("hp", "float", "0")],
        );
        assert_eq!(merged.get("hp").map(String::as_str), Some("7"));
    }

    /// 新定義に無いフィールド（削除・改名）は破棄される。
    #[test]
    fn removed_field_is_dropped() {
        let merged = carry_over_script_fields(
            &old(&[("speed", "12.5"), ("gone", "42")]),
            &[def("speed", "float", "1")],
        );
        assert!(!merged.contains_key("gone"));
        assert_eq!(merged.len(), 1);
    }

    /// ネストしたフィールドパスも通常のフィールドと同じ規則で扱う。
    #[test]
    fn nested_paths_follow_the_same_rules() {
        let defs = [def("stats.hp", "int", "100"), def("stats.mp", "int", "50")];
        let merged = carry_over_script_fields(&old(&[("stats.hp", "12")]), &defs);
        assert_eq!(merged.get("stats.hp").map(String::as_str), Some("12"));
        assert!(!merged.contains_key("stats.mp"));
    }

    /// 参照フィールドは値の妥当性を検証できないので、名前が残っていれば引き継ぐ。
    #[test]
    fn reference_field_keeps_actor_name() {
        let merged = carry_over_script_fields(
            &old(&[("target", "Player")]),
            &[def("target", "reference", "")],
        );
        assert_eq!(merged.get("target").map(String::as_str), Some("Player"));
    }

    /// インスペクタが扱えない型（unsupported）は常に引き継がない。
    #[test]
    fn unsupported_type_is_dropped() {
        let merged = carry_over_script_fields(
            &old(&[("weird", "something")]),
            &[def("weird", "unsupported", "")],
        );
        assert!(merged.is_empty());
    }

    /// 配列フィールドは JSON 配列として要素型まで検証したうえで引き継ぐ。
    #[test]
    fn array_fields_carry_over_when_element_types_match() {
        let defs = [
            def("speeds",  "array:float",     "[]"),
            def("counts",  "array:int",       "[]"),
            def("flags",   "array:bool",      "[]"),
            def("names",   "array:string",    "[]"),
            def("targets", "array:reference", "[]"),
        ];
        let merged = carry_over_script_fields(
            &old(&[
                ("speeds",  "[1.0,2.5]"),
                ("counts",  "[1,2,3]"),
                ("flags",   "[true,false]"),
                ("names",   "[\"a\",\"b\"]"),
                ("targets", "[\"Player\",\"Enemy|MainCamera\"]"),
            ]),
            &defs,
        );
        assert_eq!(merged.get("speeds").map(String::as_str),  Some("[1.0,2.5]"));
        assert_eq!(merged.get("counts").map(String::as_str),  Some("[1,2,3]"));
        assert_eq!(merged.get("flags").map(String::as_str),   Some("[true,false]"));
        assert_eq!(merged.get("names").map(String::as_str),   Some("[\"a\",\"b\"]"));
        assert_eq!(merged.get("targets").map(String::as_str), Some("[\"Player\",\"Enemy|MainCamera\"]"));
    }

    /// 空配列はどの要素型でも適合する（要素が無いので食い違いようがない）。
    #[test]
    fn empty_array_is_accepted_for_any_element_type() {
        for tag in ["array:float", "array:int", "array:bool", "array:string", "array:reference"] {
            let merged = carry_over_script_fields(&old(&[("xs", "[]")]), &[def("xs", tag, "[]")]);
            assert_eq!(merged.get("xs").map(String::as_str), Some("[]"), "tag = {tag}");
        }
    }

    /// 要素型が変わった配列は旧値を捨てる（＝スクリプト宣言側の既定値へ戻る）。
    #[test]
    fn array_element_type_change_drops_stale_value() {
        // string[] → float[]
        let merged = carry_over_script_fields(
            &old(&[("xs", "[\"a\",\"b\"]")]),
            &[def("xs", "array:float", "[]")],
        );
        assert!(!merged.contains_key("xs"));

        // float[] → int[]（小数は整数として解釈できない）
        let merged = carry_over_script_fields(
            &old(&[("xs", "[1.5]")]),
            &[def("xs", "array:int", "[]")],
        );
        assert!(!merged.contains_key("xs"));

        // bool[] → string[]
        let merged = carry_over_script_fields(
            &old(&[("xs", "[true]")]),
            &[def("xs", "array:string", "[]")],
        );
        assert!(!merged.contains_key("xs"));
    }

    /// int[] → float[] は値の意味が保たれるので引き継ぐ（スカラの int → float と同じ規則）。
    #[test]
    fn array_widening_numeric_element_keeps_value() {
        let merged = carry_over_script_fields(
            &old(&[("xs", "[1,2]")]),
            &[def("xs", "array:float", "[]")],
        );
        assert_eq!(merged.get("xs").map(String::as_str), Some("[1,2]"));
    }

    /// 非配列 → 配列、配列 → 非配列のいずれも旧値を捨てる。
    #[test]
    fn array_and_scalar_are_not_interchangeable() {
        // float → float[]（"12.5" は JSON 配列ではない）
        let merged = carry_over_script_fields(
            &old(&[("x", "12.5")]),
            &[def("x", "array:float", "[]")],
        );
        assert!(!merged.contains_key("x"));

        // float[] → float（"[1.0]" は数値として解釈できない）
        let merged = carry_over_script_fields(
            &old(&[("x", "[1.0]")]),
            &[def("x", "float", "0")],
        );
        assert!(!merged.contains_key("x"));
    }

    /// 壊れた JSON・要素型タグが未対応の配列は引き継がない。
    #[test]
    fn broken_array_value_is_dropped() {
        let merged = carry_over_script_fields(
            &old(&[("xs", "[1.0,")]),
            &[def("xs", "array:float", "[]")],
        );
        assert!(merged.is_empty());

        let merged = carry_over_script_fields(
            &old(&[("xs", "[1.0]")]),
            &[def("xs", "array:unsupported", "[]")],
        );
        assert!(merged.is_empty());
    }

    /// 定義が空なら結果も空（フィールドを全部消したスクリプトのケース）。
    #[test]
    fn empty_defs_yield_empty_map() {
        let merged = carry_over_script_fields(&old(&[("a", "1"), ("b", "2")]), &[]);
        assert!(merged.is_empty());
    }

    /// CLR が返す JSON をそのままデシリアライズできる（キー名の契約テスト）。
    #[test]
    fn parses_clr_json_shape() {
        let json = r#"[{"name":"speed","type":"float","default":"1.5"}]"#;
        let defs: Vec<ScriptFieldDef> = serde_json::from_str(json).unwrap();
        assert_eq!(defs, vec![def("speed", "float", "1.5")]);
    }

    // ── 構造体配列（array:struct:Xxx）の引き継ぎ ───────────────

    /// メンバ名・型が噛み合う JSON オブジェクト配列はそのまま引き継ぐ。
    #[test]
    fn struct_array_carries_over_when_members_match() {
        let defs = [struct_def(
            "levels",
            "FishLevelEntry",
            &[("spawnDistance", "float"), ("fishPrefabs", "array:string")],
        )];
        let value = r#"[{"spawnDistance":10.0,"fishPrefabs":["a.actor"]},{"spawnDistance":25,"fishPrefabs":[]}]"#;
        let merged = carry_over_script_fields(&old(&[("levels", value)]), &defs);
        assert_eq!(merged.get("levels").map(String::as_str), Some(value));
    }

    /// 空配列は常に引き継ぐ（要素が無いので型の食い違いが起きない）。
    #[test]
    fn empty_struct_array_carries_over() {
        let defs = [struct_def("levels", "FishLevelEntry", &[("spawnDistance", "float")])];
        let merged = carry_over_script_fields(&old(&[("levels", "[]")]), &defs);
        assert_eq!(merged.get("levels").map(String::as_str), Some("[]"));
    }

    /// 新設メンバ（保存値に無いキー）は欠損として許し、値全体を引き継ぐ。
    /// 実際の値埋めは CLR 側が宣言時初期値で行う。
    #[test]
    fn struct_array_allows_missing_new_member() {
        let defs = [struct_def(
            "levels",
            "FishLevelEntry",
            &[("spawnDistance", "float"), ("rarity", "int")],   // rarity を後から追加した状況
        )];
        let value = r#"[{"spawnDistance":10.0}]"#;
        let merged = carry_over_script_fields(&old(&[("levels", value)]), &defs);
        assert_eq!(merged.get("levels").map(String::as_str), Some(value));
    }

    /// 宣言から消えたメンバの残骸キーは無視して引き継ぐ。
    #[test]
    fn struct_array_ignores_removed_member_key() {
        let defs = [struct_def("levels", "FishLevelEntry", &[("spawnDistance", "float")])];
        let value = r#"[{"spawnDistance":10.0,"legacyName":"old"}]"#;
        let merged = carry_over_script_fields(&old(&[("levels", value)]), &defs);
        assert_eq!(merged.get("levels").map(String::as_str), Some(value));
    }

    /// 同名メンバの型が変わったら引き継がない（値の意味が変わるため）。
    #[test]
    fn struct_array_drops_when_member_type_changed() {
        // float → string へ変えた
        let defs = [struct_def("levels", "FishLevelEntry", &[("spawnDistance", "string")])];
        let merged = carry_over_script_fields(
            &old(&[("levels", r#"[{"spawnDistance":10.0}]"#)]),
            &defs,
        );
        assert!(!merged.contains_key("levels"));

        // int メンバに小数が入っている（スカラの int と同じ厳しさ）
        let defs = [struct_def("levels", "FishLevelEntry", &[("rarity", "int")])];
        let merged = carry_over_script_fields(
            &old(&[("levels", r#"[{"rarity":1.5}]"#)]),
            &defs,
        );
        assert!(!merged.contains_key("levels"));

        // 入れ子配列メンバの要素型が変わった（string[] → float[]）
        let defs = [struct_def("levels", "FishLevelEntry", &[("fishPrefabs", "array:float")])];
        let merged = carry_over_script_fields(
            &old(&[("levels", r#"[{"fishPrefabs":["a.actor"]}]"#)]),
            &defs,
        );
        assert!(!merged.contains_key("levels"));
    }

    /// スカラ配列 ⇔ 構造体配列の相互変更では引き継がない。
    #[test]
    fn struct_array_and_scalar_array_do_not_mix() {
        // float[] の値 → 構造体配列へ変更
        let defs = [struct_def("levels", "FishLevelEntry", &[("spawnDistance", "float")])];
        let merged = carry_over_script_fields(&old(&[("levels", "[1.0,2.0]")]), &defs);
        assert!(!merged.contains_key("levels"));

        // 構造体配列の値 → float[] へ変更
        let merged = carry_over_script_fields(
            &old(&[("levels", r#"[{"spawnDistance":10.0}]"#)]),
            &[def("levels", "array:float", "[]")],
        );
        assert!(!merged.contains_key("levels"));
    }

    /// 壊れた JSON・配列でない値は引き継がない。
    #[test]
    fn broken_struct_array_value_is_dropped() {
        let defs = [struct_def("levels", "FishLevelEntry", &[("spawnDistance", "float")])];
        for broken in [r#"[{"spawnDistance":10.0"#, r#"{"spawnDistance":10.0}"#, "not json"] {
            let merged = carry_over_script_fields(&old(&[("levels", broken)]), &defs);
            assert!(!merged.contains_key("levels"), "引き継いではいけない値: {broken}");
        }
    }

    /// 参照メンバは未設定を null で書いても引き継げる（CLR は空文字として読む）。
    #[test]
    fn struct_array_accepts_null_reference_member() {
        let defs = [struct_def("slots", "SpawnSlot", &[("target", "reference")])];
        let value = r#"[{"target":null},{"target":"Player"}]"#;
        let merged = carry_over_script_fields(&old(&[("slots", value)]), &defs);
        assert_eq!(merged.get("slots").map(String::as_str), Some(value));
    }

    /// ScriptEvent フィールドは、同じ型タグのままなら結線 JSON をそのまま引き継ぐ。
    /// 未知キー（将来追加される "slot" など）が混じっていても落とさない。
    #[test]
    fn keeps_script_event_bindings_when_tag_matches() {
        let defs = [def("onStart", "scriptevent", "[]")];
        let value = concat!(
            r#"[{"actor":"DialogueManager","script":"QuestFlow","method":"Begin","#,
            r#""argKind":"string","arg":"intro","slot":"A"},"#,
            r#"{"actor":"","script":"","method":"","argKind":"none","arg":""}]"#
        );
        let merged = carry_over_script_fields(&old(&[("onStart", value)]), &defs);
        assert_eq!(merged.get("onStart").map(String::as_str), Some(value));

        // 空配列（結線なし）も適合する
        let merged_empty = carry_over_script_fields(&old(&[("onStart", "[]")]), &defs);
        assert_eq!(merged_empty.get("onStart").map(String::as_str), Some("[]"));

        // 構造体配列のメンバとしての ScriptEvent（入れ子の JSON 配列）も引き継げる
        let struct_defs = [struct_def("triggers", "TriggerEntry",
                                      &[("delay", "float"), ("onFire", "scriptevent")])];
        let nested = r#"[{"delay":2.5,"onFire":[{"actor":"Boss","method":"Begin"}]},{"delay":0.0,"onFire":[]}]"#;
        let merged_nested = carry_over_script_fields(&old(&[("triggers", nested)]), &struct_defs);
        assert_eq!(merged_nested.get("triggers").map(String::as_str), Some(nested));

        // メンバの値が配列でなければ（型変更）その構造体配列は引き継がない
        let broken = r#"[{"delay":2.5,"onFire":"Begin"}]"#;
        assert!(carry_over_script_fields(&old(&[("triggers", broken)]), &struct_defs).is_empty());
    }

    /// 型が変わった場合は引き継がない（float の値 → ScriptEvent、および逆方向）。
    /// オブジェクト以外の要素を含む配列も ScriptEvent としては受け付けない。
    #[test]
    fn drops_script_event_when_type_changed() {
        // float で保存されていた値を scriptevent フィールドへは引き継がない
        let to_event = [def("onStart", "scriptevent", "[]")];
        assert!(carry_over_script_fields(&old(&[("onStart", "12.5")]), &to_event).is_empty());
        // 配列だが要素がオブジェクトでないものも不適合
        assert!(carry_over_script_fields(&old(&[("onStart", r#"["Begin"]"#)]), &to_event).is_empty());

        // 逆方向: 結線 JSON を float フィールドへは引き継がない
        let to_float = [def("onStart", "float", "0")];
        let bindings = r#"[{"actor":"A","script":"B","method":"C","argKind":"none","arg":""}]"#;
        assert!(carry_over_script_fields(&old(&[("onStart", bindings)]), &to_float).is_empty());
    }

    /// CLR が返す構造体配列 JSON（members 付き）をそのままデシリアライズできる。
    #[test]
    fn parses_clr_struct_json_shape() {
        let json = r#"[{"name":"levels","type":"array:struct:FishLevelEntry","default":"[]",
            "members":[{"name":"spawnDistance","label":"出現距離","type":"float","default":"0"},
                       {"name":"fishPrefabs","label":"魚prefab","type":"array:string","default":"[]"}]}]"#;
        let defs: Vec<ScriptFieldDef> = serde_json::from_str(json).unwrap();
        assert_eq!(defs.len(), 1);
        assert_eq!(defs[0].type_tag, "array:struct:FishLevelEntry");
        assert_eq!(defs[0].members.len(), 2);
        assert_eq!(defs[0].members[0].name, "spawnDistance");
        assert_eq!(defs[0].members[1].type_tag, "array:string");
    }
}
