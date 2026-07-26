# SEED エンジン — クラス図集（Mermaid）

[architecture_showcase.md](architecture_showcase.md) の補足資料。
主要サブシステムの型関係を Mermaid classDiagram で図示する。
GitHub / VSCode（Mermaid 拡張）でそのままレンダリング可能。

---

## 1. ECS コア（runtime/src/engine/ecs/）

```mermaid
classDiagram
    class Entity {
        +index: u32
        +generation: u32
    }
    class Entities {
        -meta: Vec~EntityMeta~
        -free_ids: Vec~u32~
        +spawn() Entity
        +despawn(Entity)
        +is_alive(Entity) bool
    }
    class World {
        -entities: Entities
        -storages: HashMap~TypeId, Box_dyn_AnyStorage~
        -resources: HashMap~TypeId, Box_dyn_Any~
        +insert~T~(Entity, T)
        +query~T~()
        +query2~A,B~()
    }
    class AnyStorage {
        <<trait>>
        +as_any()
        +remove(Entity)
    }
    class SparseSet~T~ {
        -sparse: Vec~u32~  %% ABSENT=u32::MAX センチネル
        -dense: Vec~(Entity, T)~
        +insert/get/remove O(1)
    }
    class Component {
        <<trait>>
        %% Any + Send + Sync のマーカーのみ
    }
    class System {
        <<trait>>
        +run(world, ctx)
    }
    class FnSystem {
        -f: Box~closure~
    }
    class Schedule {
        -phases: Vec~(Phase, Vec~Box_dyn_System~)~
        +run_phase(Phase, world, ctx)
    }
    class Phase {
        <<enum>>
        BeginFrame
        EarlyUpdate
        Update
        ConstantUpdate
        LateUpdate
        Render
        EndFrame
    }

    World *-- Entities
    World *-- "N" AnyStorage : HashMap~TypeId~
    Entities *-- "N" Entity : 管理
    SparseSet~T~ ..|> AnyStorage : impl
    SparseSet~T~ o-- "N" Entity : dense キー
    SparseSet~T~ *-- "N" Component : T の実データ
    FnSystem ..|> System : impl
    Schedule *-- "N" System : Phase 別
    Schedule --> Phase : 7 段階
```

---

## 2. Actor / Scene レイヤ（structs/objects/actor, app_base/scene.rs）

```mermaid
classDiagram
    class Scene {
        +name: String
        +world: World
        +actors: Vec~Actor~
        +schedule: Schedule
    }
    class Actor {
        +entity: Entity
        +name: String
        +world_line: u32  %% タブ別編集空間
        +prefab_source: Option~String~
        +children: Vec~Actor~
        +slots: Vec~ComponentSlot~
        +to_data(world) ActorData
    }
    class ComponentSlot {
        +name: String
        +kind: ComponentKind
        +type_id: TypeId
        +entity: Entity  %% スロット専用エンティティ
        +enabled: bool
    }
    class ActorKind {
        <<enum>>
        Actor3D
        Actor2D
    }
    class ComponentKind {
        <<enum>>
        Model / Script / Camera / Light
        Collider / Audio / Animator / ...
    }
    class ActorData {
        <<serde>>
        +components: Vec~ComponentSlotData~
        +children: Vec~ActorData~
        +prefab_source: Option~String~
    }
    class SceneData {
        <<serde>>
        +name: String
        +actors: Vec~ActorData~
    }

    Scene *-- World
    Scene *-- Schedule
    Scene *-- "N" Actor : ルート DFS 順
    Actor *-- "N" Actor : children（再帰木）
    Actor *-- "N" ComponentSlot
    Actor --> ActorKind
    ComponentSlot --> ComponentKind
    ComponentSlot --> Entity : 実データのキー
    Actor ..> ActorData : to_data / build_actor
    ActorData *-- "N" ActorData : 再帰
    SceneData *-- "N" ActorData
    note for ComponentSlot "スロットごとに専用 Entity を spawn。\n同型コンポーネントの多重アタッチを実現"
```

---

## 3. レンダリング（core/renderer/）

```mermaid
classDiagram
    class RenderPipelineBuilder {
        %% naga で WGSL を反射し
        %% BindGroupLayout を自動導出
        +build_from(toml, wgsl)
    }
    class RenderFeatures {
        +ao_mode: AoMode
        +gi_mode: GiMode
        +reflection_mode: ReflectionMode
        +shadow_mode: ShadowMode
    }
    class ResolvedFeatures {
        %% GPU 能力で解決済み
        %% RT → SSR → OFF デグレード
    }
    class GBuffer {
        %% MRT 書き込み
    }
    class DeferredLightingPipelines {
        +pipeline: RenderPipeline      %% 非RT
        +rt: RenderPipeline            %% RT対応
        +rt_bindless: RenderPipeline   %% RT+色付き影
    }
    class ClusterResources {
        %% 16x9x24 = 3456 フロクセル
        %% cluster_build.wgsl (compute)
        +params_buffer
        +cell_buffer
    }
    class LightBuffer {
        +lights: GpuLight[1024]
        +bind_groups: per LightingPass
    }
    class GpuLight {
        %% 112 byte, std430 手動パディング
        +kind: Directional|Point|Spot|Rect
    }
    class LightingPass {
        <<enum>>
        MainCamera
        CameraPreview
    }
    class LightComponent {
        <<ECS Component>>
    }

    RenderFeatures ..> ResolvedFeatures : GPU 能力で解決
    DeferredLightingPipelines ..> GBuffer : 読み取り
    DeferredLightingPipelines ..> ClusterResources : ライト索引
    ClusterResources ..> LightBuffer : クラスタ毎に参照
    LightBuffer *-- "1024" GpuLight
    LightBuffer --> LightingPass : カメラ別 BindGroup
    LightComponent ..> GpuLight : 毎フレーム収集
    RenderPipelineBuilder ..> DeferredLightingPipelines : 構築
```

---

## 4. 地形システム（engine/terrain/ — エンジン非依存層）

```mermaid
classDiagram
    class TerrainChunkData {
        +density: Vec~f32~        %% 33^3 SDF
        +paint_index: Vec~[u8_4]~
        +paint_weight: Vec~[u8_4]~
        +paint_amount: Vec~u8~
    }
    class ChunkCoord {
        +x/y/z: i32
    }
    class TerrainMesh {
        %% marching_cubes::generate
    }
    class SphereBrush {
        +op: BrushOp
        +apply(SampleField)
    }
    class BrushOp {
        <<enum>>
        Add / Subtract / Smooth / Flatten
    }
    class SampleField {
        <<trait>>
    }
    class TerrainLayerSet {
        +layers: Vec~TerrainLayer~
        +select_top_slots() BlendSlots
    }
    class ScatterField {
        +instances: Vec~ScatterInstance~
        %% ルール自動散布 + ブラシ手描き
    }
    class TvoxIO {
        %% "TVOX" マジック
        %% v1→v2→v3 後方互換読込
    }
    class TerrainChunkComponent {
        <<ECS Component>>
        %% .tvox パス経由で参照
    }

    TerrainChunkData --> ChunkCoord
    TerrainChunkData ..> TerrainMesh : marching cubes
    SphereBrush --> BrushOp
    SphereBrush ..> SampleField : 適用先
    TerrainChunkData ..|> SampleField : 実装相当
    TerrainLayerSet ..> TerrainChunkData : ペイント解釈
    TvoxIO ..> TerrainChunkData : 永続化
    TerrainChunkComponent ..> TvoxIO : ロード
    ScatterField ..> TerrainChunkData : 表面サンプル
```

---

## 5. アニメーション（engine/animation/）

```mermaid
classDiagram
    class AnimationClip {
        +name: String
        +duration: f32
        +loop_mode: AnimLoopMode
        +tracks: Vec~Track~
    }
    class Track {
        +target: TrackTarget
        +value_type: AnimValueType
        +keys: Vec~Keyframe~
    }
    class TrackTarget {
        +actor_path: String  %% "/" 区切り相対パス
        +component: String
        +property: String
    }
    class Keyframe {
        +time: f32
        +value: AnimValue
        +interp: Step|Linear|Bezier
    }
    class AnimValue {
        <<enum>>
        Float / Vec2 / Vec3 / Color / Bool
        +to_components() Vec~f32~
        +from_components(...)
    }
    class PropBinding {
        %% (component, property) 文字列を
        %% ECS getter/setter へ解決
    }
    class AnimatorComponent {
        <<ECS Component>>
        +clips: Vec~AnimClipRef~
    }
    class JointAttachComponent {
        <<ECS Component>>
        %% ボーン=子アクタ ソケット機構
        %% モデル行列 x ジョイント行列 x オフセット
    }

    AnimationClip *-- "N" Track
    Track *-- TrackTarget
    Track *-- "N" Keyframe
    Keyframe --> AnimValue
    AnimatorComponent o-- "N" AnimationClip : 参照
    AnimatorComponent ..> PropBinding : resolve_binding
    PropBinding ..> TrackTarget : 文字列から解決
    JointAttachComponent ..> AnimatorComponent : モデルのジョイント行列を参照
```

---

## 6. C# スクリプトホスティング（core/scripting/ ⇔ scripting/）

```mermaid
classDiagram
    class ScriptingHost {
        <<Rust>>
        -context: HostfxrContext
        -create_fn / destroy_fn
        -lifecycle_fns[7]  %% 7フェーズ
        -compile_fn / set_field_fn
        +compile_scripts()  %% ホットリロード
    }
    class ScriptHostApi {
        <<Rust #[repr(C)]>>
        +read_floats / write_floats
        +has_component
        %% f32配列 1〜4 要素に統一
    }
    class RawFrameContext {
        <<#[repr(C)] 共有>>
        %% C# 側と レイアウト完全一致必須
    }
    class ScriptComponent {
        <<ECS Component>>
        %% ScriptingHost を Arc 共有
    }
    class ScriptSceneCommand {
        <<enum>>
        Instantiate / Destroy / ...
        %% フェーズ後に遅延適用
    }
    class SEEDScript {
        <<C# 基底クラス>>
        +BeginFrame/Update/.../EndFrame(ref ctx)
        +[SerializeField] でインスペクタ公開
    }
    class GameObject {
        <<C# API>>
        +GetComponent~T~() T?
    }
    class SEED_Api {
        <<C# 名前空間 SEED>>
        Mathf / Vector3 / Time / Input
        Physics / Audio / Scene / Debug
    }

    ScriptingHost --> ScriptHostApi : RegisterHostApi で C# へ登録
    ScriptingHost ..> SEEDScript : 関数ポインタで呼出
    ScriptComponent o-- ScriptingHost : Arc
    ScriptingHost ..> ScriptSceneCommand : キューに蓄積
    SEEDScript --> GameObject
    SEEDScript --> SEED_Api
    GameObject ..> ScriptHostApi : transform.Position 等が経由
    SEEDScript ..> RawFrameContext : ref ctx
```

---

## 7. プラグインシステム（plugin_api / engine/plugin/）

```mermaid
classDiagram
    class Plugin {
        <<trait — plugin_api クレート>>
        +name() / version() / description()
        +field_defs() Vec~PluginFieldDef~
        +on_field_changed(key, value)
    }
    class PluginFieldDef {
        +key: String
        +kind: PluginFieldKind
    }
    class PluginFieldKind {
        <<enum>>
        Float / Int / String / Bool
        Color / FilePath / Enum
    }
    class PluginRegistry {
        +load_from_dir(plugins_dir)
        -loaded: Vec~LoadedPlugin~
    }
    class LoadedPlugin {
        +plugin: Box~dyn Plugin~
        +field_defs_cache
        -_lib: libloading_Library
        %% _lib を最後に宣言し
        %% Drop 順序で use-after-free 防止
    }
    class PluginManifest {
        <<plugin.json>>
        +name / version / description
    }
    class PluginComponent {
        <<ECS Component>>
        +plugin_name: String
        +fields: HashMap~String,String~
    }
    class SamplePluginDll {
        <<DLL>>
        +seed_create_plugin() extern C
    }

    PluginRegistry *-- "N" LoadedPlugin
    LoadedPlugin *-- Plugin : Box~dyn~
    SamplePluginDll ..|> Plugin : impl + エクスポート
    Plugin ..> PluginFieldDef : 宣言的 UI 定義
    PluginFieldDef --> PluginFieldKind
    PluginRegistry ..> PluginManifest : 突合してロード
    PluginComponent ..> Plugin : SetPluginField → on_field_changed
```

---

## 8. WPF エディタ全体（editor/src/）

```mermaid
classDiagram
    class MainWindow {
        %% partial class 10 分割:
        %% Camera/CanvasEdit/FileOps/Input/
        %% Physics/Scene/SceneTabs/Terrain/Viewport
    }
    class DockingManager {
        <<AvalonDock>>
        %% ContentId をレイアウト永続化キーに
    }
    class RuntimeManager {
        +state: EditorState
        %% Edit常駐 / Play常駐再利用 /
        %% インプレースPlay の状態機械
    }
    class EditorState {
        <<enum>>
        Idle / Building / Edit / Play / Pause
    }
    class ViewportHost {
        <<HwndHost>>
        %% CreateWindowEx + SetParent
        %% WM_PARENTNOTIFY でペイン活性化
    }
    class PipeServer {
        %% Named Pipe \\.\pipe\SEED_{guid}
        %% 改行区切りテキスト IPC
    }
    class HierarchyPanel
    class InspectorPanel {
        %% 条件付き表示を多用
        %% NumericDragBehavior (ImGui風ドラッグ)
    }
    class ProjectPanel
    class ScriptEditorPanel {
        %% AvalonEdit ベース
    }
    class AnimationTimelinePanel
    class DopeSheetPanel
    class TerrainSettingsWindow
    class ProjectSettingsWindow {
        +BuildPluginManagePanel()
    }

    MainWindow *-- DockingManager
    MainWindow *-- RuntimeManager
    MainWindow *-- ViewportHost
    MainWindow o-- HierarchyPanel
    MainWindow o-- InspectorPanel
    MainWindow o-- ProjectPanel
    MainWindow o-- ScriptEditorPanel
    MainWindow o-- AnimationTimelinePanel
    RuntimeManager *-- PipeServer
    RuntimeManager --> EditorState
    RuntimeManager ..> ViewportHost : SetParent(HWND)
    AnimationTimelinePanel *-- DopeSheetPanel
    MainWindow ..> TerrainSettingsWindow
    MainWindow ..> ProjectSettingsWindow
```

---

## 9. AI 補完・デバッガ（editor/src/Panels/ScriptEditor/, Debugger/, AI/）

```mermaid
classDiagram
    class ScriptEditorPanel {
        %% ドキュメントごとに補完 Controller
    }
    class InlineCompletionController {
        %% デバウンス 250ms/600ms
        %% コメント直後は強トリガ
    }
    class GhostTextRenderer {
        <<AvalonEdit BackgroundRenderer>>
    }
    class IInlineCompletionProvider {
        <<interface>>
    }
    class GroqInlineCompletionProvider {
        %% OpenAI互換 ストリーミング
        %% LooksLikeWrongApi / StripReasoning
        %% CapToAvailableLines / 429クールダウン
    }
    class ScriptApiReference {
        +Load()
        %% docs/scripting_api.md を
        %% システムプロンプトへ注入
    }
    class ScriptDebugSession {
        %% netcoredbg --interpreter=vscode
        %% attach → setBreakpoints → configDone
        %% justMyCode=false 必須
    }
    class DapClient {
        %% Content-Length フレーミング
        %% seq 相関 / イベント配信
    }
    class BreakpointStore {
        %% breakpoints.json 永続化
    }
    class AIAssistantPanel
    class IAIProvider {
        <<interface>>
    }
    class AnthropicProvider
    class GeminiProvider
    class OpenAICompatibleProvider
    class LocalLlmManager {
        %% llama-server + Qwen2.5-Coder-7B
    }
    class EditorCommandExecutor {
        %% AiAddActor / AiSetValue 等を IPC 送信
    }

    ScriptEditorPanel *-- InlineCompletionController
    ScriptEditorPanel *-- BreakpointStore
    ScriptEditorPanel ..> ScriptDebugSession
    InlineCompletionController *-- GhostTextRenderer
    InlineCompletionController --> IInlineCompletionProvider
    GroqInlineCompletionProvider ..|> IInlineCompletionProvider
    GroqInlineCompletionProvider ..> ScriptApiReference
    ScriptDebugSession *-- DapClient
    AIAssistantPanel --> IAIProvider
    AnthropicProvider ..|> IAIProvider
    GeminiProvider ..|> IAIProvider
    OpenAICompatibleProvider ..|> IAIProvider
    LocalLlmManager ..> OpenAICompatibleProvider : エンドポイント供給
    AIAssistantPanel --> EditorCommandExecutor
```

---

## 10. インプレース Play（play_mode_ops.rs）

```mermaid
classDiagram
    class App {
        +mode: RuntimeMode
        +play_snapshot: Option~PlaySnapshot~
        +enter_play()
        +exit_play()
    }
    class RuntimeMode {
        <<enum>>
        Edit
        Play
    }
    class PlaySnapshot {
        +entries: Vec~PlaySnapshotEntry~
    }
    class PlaySnapshotEntry {
        <<enum>>
        Keep(Entity)        %% 地形・編集タブ: 現物保持
        Restore(ActorData)  %% 通常アクター: 退避→再構築
    }

    App --> RuntimeMode
    App *-- PlaySnapshot : Option
    PlaySnapshot *-- "N" PlaySnapshotEntry
    note for PlaySnapshotEntry "非対称戦略:\n地形+GPU資源は Keep(触らない)\nシーンアクターのみ Restore\n→ 地形再構築 約17秒 + BLAS/草 約17秒 をスキップ"
```
