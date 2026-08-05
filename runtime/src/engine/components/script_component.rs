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
