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
///               `reference`/`unsupported`
/// - `default_value`: 宣言時初期値の文字列化（`reference`/`unsupported` は空文字）
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ScriptFieldDef {
    pub name: String,
    #[serde(rename = "type")]
    pub type_tag: String,
    #[serde(rename = "default")]
    pub default_value: String,
}

/// 保存済みの値が、新しいフィールド定義の型で解釈できるかを判定する。
///
/// 型タグは CLR 側 `ConvertValue` の対応型と 1 対 1 に対応させている。
/// `string` / `reference` は任意の文字列を受け付ける（前者は素の文字列、
/// 後者はアクター名の文字列として保存されるため）。
/// `unsupported`（インスペクタが扱えない型）は常に既定値へ落とす。
fn value_matches_type(type_tag: &str, value: &str) -> bool {
    match type_tag {
        "float" | "double"        => value.trim().parse::<f64>().is_ok(),
        "int" | "long" | "short"  => value.trim().parse::<i64>().is_ok(),
        "bool"                    => value == "true" || value == "false",
        "string" | "reference"    => true,
        _                         => false,
    }
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
            if value_matches_type(&def.type_tag, v) {
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
}
