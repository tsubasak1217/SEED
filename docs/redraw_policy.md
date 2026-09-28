# 描く理由と描画の止め方（render_policy の正典。W2-10a・2026-09-28）

アプリの画面はほとんど止まっている。毎フレーム描き続けると Pixel 6a で CPU 約 83%・GPU の仕事 約 290 ms/秒を使う
（W2-0 の実機 R-10。[app_platform_roadmap.md](app_platform_roadmap.md) §3.8.7）。**描く理由の無いフレームが続いたら次のフレームを要求せず、
イベントループを眠らせる**のがこの仕組み（止めている間は 0 fps・GPU の仕事なし・CPU 6%。R-11）。

**既定は今までどおり毎フレーム描く**（`render_policy` の既定は `continuous`）。`on_demand` にしたプロジェクトだけが止まるので、
既存のゲーム（WarashibeFishing）の動きは変わらない（§8 で確かめた）。

| 置き場 | 役割 |
|---|---|
| `runtime/src/engine/core/redraw/` | 判定の本体（wgpu・winit に触れない純ロジック。各ファイルの冒頭に規則） |
| `redraw/reason.rs` | 描く理由の種類 `RedrawReason` と集合 `RedrawReasons`（u32 のビット） |
| `redraw/policy.rs` | 方針 `RenderPolicy`（continuous / on_demand）と `project_settings.json` の読み取り |
| `redraw/gate.rs` | フレームの末尾の判定 `RedrawGate`（止める・起こす・予定の時刻） |
| `redraw/schedule.rs` | 次に起きる時刻の合成（ジェスチャー・模擬の目覚まし・スクリプトの予定） |
| `redraw/script_requests.rs` | スクリプトの要求（`SEED.Redraw`）の状態 |
| `redraw/wake.rs` | 他のスレッド（IPC・JNI）から理由を積み、眠っているイベントループを起こす口 `EVENT_LOOP_WAKE` |
| `runtime/src/engine/core/app_base/app/redraw_hooks.rs` | App・イベントループへのつなぎ（フレームの末尾・WindowEvent・user_event・about_to_wait） |
| `runtime/src/engine/core/scripting/redraw_bridge.rs`・`scripting/src/Api/Redraw.cs` | スクリプトの API（`SEED.Redraw`・`SEED.RedrawPolicy`） |
| `runtime/android/native/src/redraw_waker.rs`・`runtime/android/app/.../redraw/RedrawWaker.java` | Android の文字入力の受け口から起こす JNI |
| `runtime/src/engine/physics/result_backlog.rs` | 止めている間に物理の結果の待ち行列が伸び続けないための上限 |

W2-0 の試作（起動の指定 `ui_spike` の `idle=`・`wake_ms=`）はこの仕組みに置き換えて外した（`ime` は W2-6a で使うので残した）。

---

## 1. 判定（フレームの末尾で 1 回）

フレームの末尾（`frame_renderer.rs` の `handle_redraw_requested` の最後の `request_next_frame`）で、次の順に決める。

1. 方針が `continuous`、Edit、エディタの PAUSE（デバッグカメラの見た目）のとき → **今までどおり毎フレーム描く**（数えもしない）
2. このフレームに**描く理由**（§3）がある → 描く（理由の無いフレームの数を 0 に戻す）
3. 次の予定の時刻を過ぎている → 描く（時刻の出来事は次のフレームで処理する）
4. 理由の無いフレームが `render_idle_frames` 回（既定 10）続いた → **止める**。次のフレームを要求せず、`ControlFlow::Wait`
   （次の予定があれば `ControlFlow::WaitUntil(いちばん早い予定)`）にする

止めている間に理由が来たら（§6 の起こす口）、`request_redraw`・`ControlFlow::Poll` へ戻し、**起きた最初のフレームの dt を切り詰める**（§5）。
起きた後はまた `render_idle_frames` 回の理由の無いフレームで止まる（1 回のタップで 11 フレーム描いて止まる）。

`render_idle_frames` を 1 にしないのは、理由の申告の漏れ（1〜2 フレーム遅れて見た目に出る変化・生成したスクリプトの `OnStart` が次のフレームで走る等）で、
最後の見た目が古いまま止まるのを避けるため。数は W2-10 で詰める（W2-0 の試作は 30 で試した）。

## 2. 有効にする

`project_settings.json`（エディタのプロジェクト設定の画面には欄が無い。JSON を直接書く。保存しても消えない＝`ProjectSettingsData` の `ExtraData`）:

```json
{
  "render_policy": "on_demand",
  "render_idle_frames": 10
}
```

| キー | 既定 | 意味 |
|---|---|---|
| `render_policy` | `"continuous"` | `"continuous"`（毎フレーム描く）/ `"on_demand"`（描く理由があるときだけ描く）。大文字小文字・前後の空白は問わない。知らない値は `continuous` として動き、起動ログに `[SEED REDRAW][WARN]` |
| `render_idle_frames` | `10` | on_demand で、描く理由の無いフレームがこの回数だけ続いたら止める（1〜600 の整数。範囲外・整数でなければ既定と警告） |

起動ログに `[SEED INIT] render_policy=… render_idle_frames=…` が出る。スクリプトからは `SEED.Redraw.Policy` で実行中だけ上書きできる（§4）。

**使える実行**: Play（PC の単体の SEED.exe・エディタの Play・Android）。Edit とエディタの PAUSE（デバッグカメラ）は常に毎フレーム描く。
エディタの Play でも IPC で起きるので使えるが、**エディタに埋め込んだ Play での動きは未確認**（§9）。

## 3. 描く理由の一覧

「理由」はどれか 1 つあれば次のフレームを描く。種類はログ（`[SEED REDRAW]`）と計測のためで、振る舞いは同じ。

| 理由（ログの名前） | いつ積むか | どこで |
|---|---|---|
| 入力 `input` | キー・マウスのボタン・カーソル・ホイール・タッチ・PC の IME の WindowEvent、マウスの生の移動（DeviceEvent） | `render.rs` の `window_event`・`device_event` |
| 押している `input_held` | マウスのボタン・指が触れている・注入でキーやボタンを押している間（**実キーボードのキーは数えない**。下の注） | `Input::is_any_input_held` |
| 注入の再生 `injected_input` | IPC の `INPUT_SEQUENCE` の再生中 | `Input::is_injected_sequence_playing` |
| ジェスチャー `gesture` | アリーナに参加している指がある（長押し・押下の待ちの期限は次の予定へ） | `GestureArenaSet::activity`（[input_gestures.md](input_gestures.md) §9） |
| 文字入力 `text_input` | Android の IME の本文の変化・完了などのアクション・キーボードの表示と高さ | `MainActivity` の上書き → `RedrawWaker`（JNI） |
| 動いている `motion` | Animator のクリップの再生中・パーティクル（これから放出する・寿命の残る粒子・孤児）・読み込み中のモデル・物理のボディが動いている（最大速度が静止の閾値 0.03 以上）・スクロールのドラッグ・慣性・跳ね返り・ScrollTo・スクリプトの位置の要求の処理待ち（W2-3） | `animation_ops.rs`・`ParticleSystem::is_animating`・`model_streaming::pending_count`・`play_physics_bodies_moving`・`scroll_motion_active`（`CanvasScrollState::is_active`） |
| スクリプト `script_request` | `SEED.Redraw.Request()`、`RequestAfter` の時刻が来た | `script_requests.rs`（`Request` はどのスレッドからでも起こす） |
| `script_keep_alive` | `SEED.Redraw.KeepAlive(秒)` の期限の内 | 同上 |
| `script_continuous` | `SEED.Redraw.SetContinuous(true)` の間 | 同上 |
| IPC `ipc` | 命令の行が届いた（名前付きパイプ・TCP とも。TCP の切断も） | `ipc.rs` の `read_loop`・`ipc_transport/tcp.rs` |
| プラットフォーム `platform_event` | `SEED.Platform` のイベントが届いた（Android の `nativeOnPlatformEvent`・デスクトップの模擬のイベント） | `platform_bridge/inbox.rs`（Android）・`desktop_sim` の `queue_event` |
| 画面 `screen` | 窓の大きさ・倍率・フォーカス・遮蔽・移動・テーマ、Android の安全領域・回転の報告、要求していない再描画（OS の WM_PAINT） | `render.rs`・`jni_exports.rs` の `nativeOnScreenChanged`・`on_redraw_frame_start` |
| アプリの状態 `system_event` | Android の音声フォーカスの変化（出力の一時停止・再開はイベントループの 1 周で反映するので起こす）、窓を閉じる等 | `jni_exports.rs` の `nativeOnAudioFocusChanged` |
| 撮影 `capture` | 撮影の要求（`SCREENSHOT:`）・図鑑のサムネイルの生成・プロファイラの一発計測（`PROFILE_DUMP`）が進行中 | `redraw_hooks.rs` |
| 区切り `lifecycle` | Play の開始・停止、前面へ戻った、スクリプトのシーンの命令（生成・破棄・遷移）を適用した | `play_mode_ops.rs`・`render.rs` の `resumed`・`script_scene_ops.rs` |
| 予定の時刻 `timer` | WaitUntil の時刻が来た（下の「次の予定」） | `pump_redraw_deadline`（about_to_wait） |
| （描かない）`reschedule` | 止めている間に他のスレッドのスクリプトが予定（`RequestAfter`・`KeepAlive`・方針）を変えた。描かずに起きる時刻を決め直す | `script_requests.rs` |

**次の予定**（止めている間の `WaitUntil`）: `SEED.Redraw.RequestAfter` の時刻・ジェスチャーの期限（長押し 500ms・スクロールの中の押下の待ち 100ms）・
デスクトップの模擬の目覚ましの予定と鳴動の安全弁（`PlatformBridge::next_timed_event_delay_ms`）のいちばん早いもの。
Android の目覚ましは `:seed_platform` が鳴らし、イベントが JNI で届いて起こすので予定には入らない。

注: **実キーボードのキーの押しっぱなしは理由にしない**。Windows の日本語キーボードの「半角/全角」キーは押下だけが届き離しが届かないので、winit の
`Backquote` が押されたまま残る（2026-09-28 に PC で確かめた。触っていないのに `keys={Backquote}`。[backlog.md](backlog.md)）。数えると止まらなくなる。
キーを押し続けている間は OS の自動の繰り返しが `input` として起こすので、止まるのは最初の繰り返しまでの約 0.5 秒だけ。

## 4. スクリプトの API（`SEED.Redraw`）

正典の説明は [scripting_api.md](scripting_api.md) §7.14。時間はすべて**実時間**（`Time.Scale` の影響を受けない）。

| API | 意味 |
|---|---|
| `Redraw.Request()` | 次の 1 フレームを描く。止めていれば起こす。**どのスレッドから呼んでもよい**（`async` の続き＝通信の完了から画面を更新させる） |
| `Redraw.RequestAfter(秒)` → bool | その秒数の後に 1 フレームを描く（止めている間は WaitUntil）。いちばん早い予定が効く。NaN・負は false |
| `Redraw.KeepAlive(秒)` → bool | その秒数の間は描き続ける（延ばすだけで縮めない）。部品のアニメーション・演出の間に使う |
| `Redraw.SetContinuous(bool)` / `IsContinuous` | true の間は常に描く（ゲームの画面・センサーを読む画面・鳴動の画面） |
| `Redraw.Policy`（get/set）/ `ResetPolicy()` | 方針の読み書き（`SEED.RedrawPolicy.Continuous` / `OnDemand`）。書くと実行中だけプロジェクト設定を上書き |

Play の開始・停止（エディタ）で要求（`KeepAlive`・`SetContinuous`・方針の上書き・予定）はすべて外れる。

**W2-3 以降の部品（`SEED.UI`）の約束**: スクロール（CanvasScroll）の慣性・跳ね返り・ScrollTo はエンジンが自分で申告する（W2-3。部品は何もしなくてよい）。それ以外の動いている間（スワイプの開閉・トランジション・トースト）は毎フレーム `Redraw.Request()`
を呼ぶか、始めに長さの分かる演出なら `KeepAlive(長さ)` を 1 回呼ぶ。止まったら何もしない（呼ばなければ `render_idle_frames` の後に止まる）。
時計の表示のように時刻で変わるものは、実時間（`DateTime`）で描き、次の変わり目を `RequestAfter` で申告する。

## 5. 止めている間の約束

- **スクリプトの `Update` などのフェーズは呼ばれない**（フレームを回さない）。`BeginFrame` で配られる `SEED.Platform` のイベント・`SCRIPT_DEBUG` の命令は、
  届いた口がイベントループを起こし、起きた最初のフレームで配られる（落とさない）。
- **止めていた時間はゲームの時間に入らない**: 起きた最初のフレームの dt を `RESUME_MAX_DELTA_SECS`（1/60 秒 = `FIXED_DELTA`）で切り詰める
  （`Clock::limit_next_delta`。1 回だけ効く）。`Time.DeltaTime`・`ElapsedTime`・`UnscaledDeltaTime`・`UnscaledElapsedTime`・シェーダーの時間とも。
  背面から戻ったときの `Clock::forget_elapsed` と同じ考えで、固定ステップ（`ConstantUpdate`）が取り戻しで連続して回らない。
  **時刻で何かをするスクリプトは実時間で判定する**（`DeltaTime` を足し上げた時計は、止めている間の分だけ遅れる）。
- 予定の時刻（`RequestAfter`・模擬の目覚まし）で起きたときも同じく切り詰める（ゲームの時間は止めていた分だけ実時間より遅れる）。
- **物理のスレッドは止めない**（背面の `background_pause` と違い、自分の周期で進み続ける。W2-0 の実機で止めている間の CPU 6% の大半がこの 2 本と見られた）。
  止めるかは **W2-10 で決める**。止めている間に結果の待ち行列が際限なく伸びないよう、送る側で 120 件（60 Hz で 2 秒）を上限に古いものから捨てる
  （受け手の `recv_latest` は最新の 1 件しか使わないので結果は変わらない。捨て始めると 1 度だけ `[SEED PHYSICS] 3D: … 捨て始めました` が出る）。
- フレームの凍結の見張り（`[PLAY_WD]`）は意図して止めている間は黙る。生存確認のログ（`[PLAY_HB]`・Android の `[SEED HEARTBEAT]`）は止まる（0 fps）。
- IPC の命令は、起こされた周回の `about_to_wait`（`pump_ipc_while_frames_stalled`。フレームが 100 ms 以上途絶えているので動く）か、起きたフレームの `process_ipc` で処理される。
- 背面（Android の suspended）では描けないので起こしても `Wait` のまま。前面へ戻る `resumed` が再開し、止めていた状態を捨てる。
- ヘッドレス（`SEED_HEADLESS=1`）のフレームの駆動（`pump_frame_when_redraw_stalled`）も、止めている間は撮影が残っているときだけ回す。

## 6. 仕組み（起こす口）

- **イベントループのスレッド**: WindowEvent・DeviceEvent は `note_window_event_for_redraw` がフレームの理由に積み、止めていれば再開する。
  予定の時刻は `about_to_wait` の `pump_redraw_deadline`。
- **他のスレッド**: `engine::core::redraw::wake::raise(理由)` が理由のビットを `AtomicU32` に積み、イベントループが眠っていれば起こし手を 1 回呼ぶ。
  起こし手は App が `run_with_event_loop` で登録する `EventLoopProxy::send_event(())`（winit 0.30 は Windows で `PostMessageW`、Android で
  android-activity の `AndroidAppWaker`＝`ALooper_wake`）。届いた `user_event` が理由を読み、描く理由なら再開、`reschedule` だけなら起きる時刻を決め直す。
- **取りこぼさない理由**: 眠る側は「眠るの印を立てる → 積まれた理由を読む」、積む側は「理由を積む → 印を読む」（どちらも SeqCst）。
  どちらかが必ず相手の書き込みを見るので、眠る側が理由に気付いて眠らないか、積む側が起こす（`wake.rs` の並行の試験）。起こしの知らせは受け取られるまで 1 通。
- **Android の文字入力**: GameActivity の glue はルーパーを起こすが winit は WindowEvent にしない（W2-0 の I-1・I-10）ので、`MainActivity` の
  `stateChanged`・`onEditorAction`・`onSoftwareKeyboardVisibilityChanged`・`onImeInsetsChanged` が `RedrawWaker.requestRedraw(REASON_TEXT_INPUT)` を呼ぶ。
  W2-6a で本文を受け取る JNI を足したら、その受け口も同じく起こす。
- **Android の画面・音声フォーカス・プラットフォームのイベント**: 既存の JNI（`nativeOnScreenChanged`・`nativeOnAudioFocusChanged`・`nativeOnPlatformEvent`）が
  値の変化・受け取りのときに `raise` する。

## 7. 申告しないもの・制限（W2-10・後の段階へ）

- **シェーダーの時間で動くもの**（水面の波・草の風・L3 シェーディングアセットの `time`・コースティクス）は申告しない。止めている間は止まって見える。
  使う画面は `SetContinuous(true)`。インタラクション場・地表カバー・水位のシミュレーション・DDGI などの時間の積み上げ・地形の LOD の再メッシュの積み残しも同じ
  （3D のゲームの機能。ゲームは continuous のまま使う）。
- **ファイルの保存の検知**（Play 中のシェーダーのホットリロード・水面のシェーダー・アイコン・InputMap の 1 秒ごとの更新時刻の確認）はフレームの中なので、
  止めている間は止まり、次に起きたフレームで反映する。IPC の `RELOAD_*` は起こすので遅れない。
- **ゲームパッド**はフレームの頭で読むので、止めている間の操作では起きない（アプリでは使わない想定）。
- `SEED.Draw` の図形はスクリプトが毎フレーム積むので、止めている間は最後の絵のまま（動く図形は `Request`・`KeepAlive` で申告する）。
- **止めていた後の最初のフレームは重い**（W2-0 の実機 R-13: 1 秒に 1 回のフレームは CPU 3〜6 倍・GPU 1.4〜2.5 倍。クロックが下がるためと見られる。推論）。
  入力の直後の最初のフレームも同じく重くなりうる。W2-10 で計る（UC-10）。
- スクリプトの `Invoke`（遅れて呼ぶ）の仕組みは SEED に無い（`RequestAfter` がその代わりの申告）。

## 8. 検証（2026-09-28・PC）

PC は Windows 11・RTX 3060・`runtime/target/debug/SEED.exe`（debug）。SEED.exe はフォーカスを奪わない起動（`SW_SHOWNOACTIVATE`＋最背面）、
IPC は TCP。試験のプロジェクトは作業フォルダの使い捨ての UiSpike（W2-2 の写し。540×1200・`render_policy: "on_demand"`・
確かめ用のスクリプト `RedrawProbe`〈`SCRIPT_DEBUG:probe,…` で `SEED.Redraw` を呼び、Update の回数と時刻をログへ〉。リポジトリには入れていない）。
数値は 3 回（run2〜run4。run4 が最終のビルド）の範囲。

| 確かめたこと | 結果 |
|---|---|
| 何もしないと止まる | 理由の無いフレーム 10 回で `描画を止めます`。命令を送らない 12 秒の間の Update は **10 回**（直前の命令のフレームの後の 10 フレームだけ）。`[PLAY_HB]` も止まる |
| CPU（1 コア = 100%） | on_demand で止めている間 **2.3〜5.3%**、同じプロセスを continuous にすると **18.6〜23.6%**（60 fps） |
| 止めている間の IPC | `SCRIPT_DEBUG` → `SCRIPT_DEBUG_OK` **0.0〜0.6 ms**（起こされた周回の about_to_wait で処理）。`SCREENSHOT` → `SCREENSHOT_DONE` **68.7〜79.7 ms**（continuous のとき 70.1〜77.7 ms。PNG の書き出しが大半）。起こしてから最初のフレームの頭まで **0.00〜0.46 ms**。W2-0 の試作では最大 1 秒遅れた |
| `Request()` | 止めた後の 1 回で描くフレームが 1 つ増える（10 → 11） |
| `KeepAlive(2)` | 3 秒の間に **129 回**の Update（2 秒 × 60 fps＋止めるまでの 9〜10） |
| `RequestAfter(1.5)` | 止めた後 `次の予定: 1333 ms 後` → 時刻で `描画を再開します（理由: timer）`。予定から **0.7〜1.2 ms** 遅れで Update、その DeltaTime は **16.67 ms**（切り詰め） |
| ジェスチャーの進行中 | LongBtn を注入のマウスで 2.0 秒押し続けた間は描き続けた（理由 `input_held+gesture`・次の予定 483 ms 後＝長押しの期限）。LongPress は dur=0.500、PressUp は dur=2.010 で届いた |
| `SEED.Platform` の模擬のイベント | `EmitTestEvent` は命令の 18〜23 ms 後に届いた（理由 `platform_event` で 1 フレーム多く描く）。模擬の目覚まし（2 秒後）は止めている間に予定の時刻で起き、`platform.alarm.fired` が予定の **1〜2 ms** 後に届いた（次の予定に鳴動の安全弁 60 秒が入る） |
| 物理の結果の上限 | 止めている間に 3D・2D とも 1 度だけ `[SEED PHYSICS] … 古い結果から捨て始めました（上限 120 件）` |

回帰（既定の continuous）: WarashibeFishing の複製で、図鑑のボタンの縁 56 点のクリックが変更前（HEAD＝W2-2 の最終の結果）と同じ当たり（34 当たり・22 外れ）で、
56 枚の画面が**画素一致**。図鑑の 90・180・300 フレーム目の画面も変更前と**差 0 画素**。

単体テスト: `cargo test -p SEED --lib -- redraw gesture ui_spike clock particle_system result_backlog desktop_sim platform_bridge input::`
（判定の表〈continuous は常に描く・理由ごとに描く／描かない・N 回・予定の WaitUntil・過ぎた予定・起こす・予定の差し替え〉、dt の切り詰め〈1 回だけ・小さい方〉、
起こす口の取りこぼし〈4 本のスレッドから 8,000 回積む間に眠る・起きるを繰り返す〉、設定の読み取り、スクリプトの要求、予定の換算、パーティクルの「動いている」、
物理の結果の上限、模擬の目覚ましの次の予定、FFI の値の検査。2026-09-28 に 277 件が通った）。

## 9. 実機での確かめ方（Pixel 6a。W2-10a の時点で未実施）

試験のプロジェクト（UiSpike の写しに `render_policy: "on_demand"` と `RedrawProbe`）の APK で、前面が試験のアプリのときだけ操作する（Git Bash。`MSYS_NO_PATHCONV=1`）。
`logcat -c` はしない（起動の直前の端末の時刻を控えて `-T`）。

```bash
S=<シリアル>; APP=com.seedengine.uispike; ACT=$APP/com.seedengine.runtime.MainActivity
R=$APP/com.seedengine.runtime.platform.DebugPlatformReceiver
dotnet run --project editor/tools/SeedAndroid -- install --project <試験のプロジェクト> --serial $S
T0=$(adb -s $S shell 'date +%s.%3N')            # logcat -T へ渡す端末の時刻（platform_device_tests/common.sh の dev_since と同じ形）
adb -s $S shell am start -W -n $ACT --es seed.gpu_timing 1
# 1. 止まる: [SEED REDRAW] 描画を止めます … と、[SEED HEARTBEAT] の fps が 0・[SEED GPU] が出なくなること
adb -s $S logcat -d -v epoch -T "$T0" SEED:V SEEDPlatform:V '*:S' | grep -E "SEED REDRAW|SEED HEARTBEAT|SEED GPU|render_policy|PROBE"
#    CPU: run-as で /proc/<pid>/task/*/stat の utime+stime を 15 秒あけて 2 回読む（android.md §14.6）。W2-0 の R-11（6.0%）と比べる
# 2. タップで起きる: 描画を再開します（理由: input）と「起こしてから最初のフレームの頭まで N ms」。11 フレームでまた止まる
adb -s $S shell input tap 540 1900
# 3. プラットフォームのイベントで起きる（JNI の nativeOnPlatformEvent）: 止まってからデバッグの受信機で試験イベントを流す
#    → 描画を再開します（理由: platform_event）とスクリプトの [PROBE] platform_event name=platform.test_event
MSYS_NO_PATHCONV=1 adb -s $S shell am broadcast -n $R -a com.seedengine.runtime.platform.EMIT_TEST_EVENT --es message redraw
# 4. 文字入力で起きる（JNI の RedrawWaker）: ime の試作でキーボードを出し、止まってから文字を送る → 理由: text_input
#    （起動の前に clear を積む。W2-0 の I-12。roadmap §3.8.6）
adb -s $S shell am force-stop $APP
adb -s $S shell "run-as $APP sh -c 'mkdir -p files/ui_spike && echo clear > files/ui_spike/ime_cmd'"
adb -s $S shell am start -W -n $ACT --es seed.ui_spike ime
adb -s $S shell "run-as $APP sh -c 'echo show_text > files/ui_spike/ime_cmd'"; sleep 3; adb -s $S shell input text abc
# 5. IPC で起きる: adb forward で TCP の IPC へつなぎ（android.md §21）、止まってから SCRIPT_DEBUG:probe,mark → SCRIPT_DEBUG_OK の往復の時間
# 6. 画面の変化で起きる: 回転など（端末の設定を変えるので利用者の了承を得てから）→ 理由: screen（[SEED SCREEN] の報告の直後）
# 7. 音声フォーカスで起きる: 他のアプリで音を鳴らす → 理由: system_event（[SEED AUDIO] の報告の直後）
adb -s $S shell am force-stop $APP
```

見るもの: 止めている間の CPU・GPU（W2-0 の R-11 と同じ 0 fps・6% 前後。物理のスレッドは残る）、起きるまでの遅れ（R-12 の 0.07〜4.78 ms と同程度）、
起きた最初のフレームの重さ（R-13）、文字入力・プラットフォームのイベント・音声フォーカスで取りこぼさないこと。

**2026-09-28 の実機の回（W2 の見本 3 シーン・dev / develop / release の .so。roadmap §3.9）**: 1（止まる）と 2（タップで起きる）と 5（IPC で起きる）を確かめた。
どのシーンも理由の無いフレーム 10 回で `描画を止めます` → `[SEED HEARTBEAT]` 0.0 fps（約 35 分で 165 回止めた）。起こしてから最初のフレームの頭まで
入力 0.03〜0.48 ms（117 回）・IPC 0.03〜1.10 ms（33 回）。止めている間の CPU・3・4・6・7 は未実施。
