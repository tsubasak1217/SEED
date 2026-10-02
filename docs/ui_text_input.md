# 文字入力（W2-6a 受け口・W2-6b 入力欄の部品）

アプリ基盤 W2（UI 部品群）の文字入力の正典。受け口（エンジンの入力欄の状態・PC の IME とキー・Android の GameTextInput・スクリプトの API）と、
入力欄の部品 `SEED.UI.TextField`（プレハブ・見た目・フォーカス・キーボードを避ける）を扱う。
経路の決定は `docs/app_platform_roadmap.md` §3.8.4 の **E-06**（GameActivity の文字入力。操作は android-activity の API、知らせは MainActivity の上書き）。
スクリプトの API の一覧は `docs/scripting_api.md` §7.20。文字の寸法（`Text.Measure`・グリフの追い出し）は W2-6c。

## 1. 使う場所（Wake or Pay の要求）

| 画面 | 部品の使い方 | 要る振る舞い |
|---|---|---|
| アラームの編集のサブ画面の数値の欄（猶予・スヌーズ・ペナルティ・上限金額） | `number_input.actor`（幅 112・数字は中央）＋スライダ | 数字だけ・非数値は無視・範囲外はクランプ・**欄にフォーカスが無いときだけ外の値で上書き**（`SetTextUnlessFocused`）・数字のキーボード |
| 鳴動画面の起床確認: 計算 | `number_input.actor`＋「答える」 | 数字だけ・完了で答える（`UnfocusOnDone = false`・正解で `Unfocus`） |
| 鳴動画面の起床確認: 文字入力 | `text_field.actor`（`AllowPaste = false`・`AllowCopy = false`）＋手本の Text | 1 文字ごとの知らせ（`TextChanged`。変換中の文字も）で全文一致・日本語の変換・貼り付け不可（手本は Text なので選べない・コピーできない） |
| プロフィールの名前の変更 | `Dialog.Show(new DialogOptions { Input = … })` | 1 行・例の文「例：田中太郎」・前後の空白を落とす（`TrimResult`） |
| オプションの上限金額 | `number_input.actor`＋「決定」 | 数字だけ（`MaxLength` で桁の上限） |
| 鳴動画面 | 画面の中身を縦の `CanvasScroll` に入れる | キーボードが出てもスクロールできる（`CanvasScroll.EndInset`。§10） |

## 2. 全体の形

```
            ┌──────────── PC（Windows）──────────────┐        ┌──────────── Android ─────────────────────────────┐
  入力      │ winit WindowEvent::Ime（Preedit/Commit） │        │ GameTextInput（IME）→ MainActivity の上書き        │
            │ WindowEvent::KeyboardInput（文字・編集） │        │ stateChanged / onEditorAction /                  │
            │ IPC INPUT_TEXT: / INPUT_KEY（確かめ用）  │        │ onSoftwareKeyboardVisibilityChanged /             │
            └───────────────┬─────────────────────────┘        │ onApplyWindowInsets（IME の高さ）                   │
                            │ app/text_input_hooks.rs            │ → input/TextInputBridge.java → JNI               │
                            │                                    │ → native text_input/jni_receivers.rs → inbox     │
                            ▼                                    └───────────────────────┬────────────────────────┘
          engine/core/text_input/hub.rs（プロセスに 1 つ）◀── フレームの頭 pump_text_input_messages ◀──┘
            場 TextSession（本文・選択・変換中の区間・決まり・知らせ）・キーボードの状態
                            │ フレームの末尾 flush_text_input_platform: 命令の並び（畳んで 1 度だけ）
                            ▼
            PC: window.set_ime_allowed / set_ime_cursor_area（＋キーボードの模擬）
            Android: 登録された AndroidTextInput → 複製した AndroidApp の set_ime_editor_info / set_text_input_state /
                     show_soft_input / hide_soft_input（GameActivity の UI スレッドの作業の列に順に積まれる）
                            ▲
          スクリプト SEED.TextInput（scripting/text_input_bridge.rs）… SEED.UI.TextField が使う
```

- 入力欄の場（session）は**同時に 1 つ**（フォーカスは 1 つ）。`TextInput.Begin` で前の場は終わる。
- 入力欄の場が無いとき（ゲーム・入力欄の無い画面）はどの受け口も何もしない（**従来どおり**。PC の窓は IME を許可しないまま・Android はキーボードを出さない）。

## 3. エンジンの入力欄の状態（1 つの形）

`engine/core/text_input/edit_state.rs` の `TextEditState`: 本文（変換中の文字も含む）・選択の起点（anchor）と動く端（focus＝カーソル）・変換中の区間。
添字はすべて本文の **UTF-8 のバイト位置**（文字の境界）。Android の Editable・Flutter の TextEditingValue と同じ考え方（未確定の文字も本文に入り、区間の印だけが確定で消える）。

| 世界 | 添字の単位 | 変換（`indices.rs`） |
|---|---|---|
| エンジン（Rust） | UTF-8 のバイト | — |
| Android の IME（GameTextInput の State） | UTF-16（Java の String の添字。I-6） | `utf16_to_byte`・`byte_to_utf16`（サロゲートの途中は前の境界） |
| スクリプト（C# の string） | UTF-16 | FFI の出入りで変換 |
| Windows の IME（winit の Ime::Preedit のカーソル） | 変換中の文字列の中の UTF-8 のバイト | そのまま（境界へ丸める） |

削除（Backspace・Delete）とカーソルの移動は**書記素**（見た目の 1 文字。絵文字の合字・結合文字の濁点を割らない。unicode-segmentation）ごと。
最大の長さも書記素で数える（Flutter の `characters` と同じ）。

**PC の操作の意味**（`edit_state.rs`）: `set_preedit`（変換中の区間を差し替え。無ければ選択を消してそこへ。空 = 取り消し）→ winit は確定の直前に空の Preedit を
送ってから `Commit` を送る → `commit`（変換中の区間か選択を確定の文字で置き換え）。`Ime::Disabled` は区間の印だけを外す（文字は残す）。

## 4. 決まり（`filter.rs`）

どのプラットフォームからの変化も「変化の後の状態の案」にしてから当てる。案を変えたら Android の IME へ送り返す（`set_text_input_state`）。

| 決まり | 振る舞い |
|---|---|
| 数字だけ（`TextInputKind.Number`） | 全角の数字（０〜９）は半角へ、ほかの文字は捨てる。**変換中の区間は持たない**（Android の数字のキーボードでも IME の側でかなへ切り替えられる。§3.8.4 の注意）。カーソルは残った文字へ付け替える |
| 最大の長さ（書記素） | 変換中は超えてよい。確定した案が超えたら、前の状態が既に最大なら変化を取り消し、そうでなければ先頭から最大の長さまでに切り詰める（Flutter の LengthLimitingTextInputFormatter・truncateAfterCompositionEnds） |
| 貼り付けの禁止（PC） | Ctrl+V・Shift+Insert を止めて `PasteBlocked` を知らせる（**クリップボードを読まない**） |
| 貼り付けの禁止（Android） | GameActivity には長押しの貼り付けのメニューが無く、IME のクリップボードの候補・音声入力は `commitText` で来る。**変換中の区間に触れない一度の挿入が 2 書記素以上**なら貼り付けとみなして前の状態へ戻す（`PASTE_MIN_GRAPHEMES`）。1 文字ずつの確定・変換の確定・変換中は通す。次の単語の予測の候補（変換なしで単語を一度に入れる）もこれに当たる（§14） |
| コピー・切り取りの禁止（PC） | Ctrl+C・Ctrl+X・Ctrl+Insert・Shift+Delete を捨てる。Android の IME からのコピーは GameTextInput の InputConnection が受けない |

## 5. 受け口の流れ（フレームの中）

| 時 | 何をするか |
|---|---|
| winit の WindowEvent（いつでも） | `WindowEvent::Ime` → 今の場へ（Play 中で一時停止でないときだけ）。`KeyboardInput` の押下 → `route_key_to_text_input`: 変換中のキー（論理キーが `NamedKey::Process`＝VK_PROCESSKEY）は IME が受けているので受けたことにする・編集のキー（物理キーの位置）・入れる文字（`KeyEvent.text`。制御文字は入れない。Ctrl を押していれば入れない）。**受けたらエディタのショートカット（Play 中の Ctrl+Z・Q/W/E/T など）へ回さない**。キーの状態（スクリプトの `Input.GetKey`）は従来どおり記録する |
| IPC（読み取りのスレッド → フレーム） | `INPUT_TEXT:`（§11）・`INPUT_KEY` の押下（編集のキーとして） |
| フレームの頭（スクリプトの前） | Android の JNI が積んだ知らせ（inbox）をハブへ当てる（`pump_text_input_messages`） |
| スクリプト | `SEED.TextInput` の Begin・End・出来事の取り出し・状態の読み書き・キーボードの出し入れ・候補窓の矩形 |
| フレームの末尾 | ハブが「要求」と「最後に当てた状態」の食い違いから命令の並びを 1 度だけ作り、プラットフォームで実行する（`flush_text_input_platform`） |

**命令の畳み方**（`hub.rs` の `take_platform_commands`）: 新しい場（か入力の種類・アクションが変わった）→ `SetEditorInfo` → `SetState`（IME の本文を新しい場のものに）、
PC の IME の許可（文字の欄だけ。**数字の欄は許可しない＝直接の文字で入る**）、候補窓の矩形（変わったときだけ）、出す・隠すの要求。
同じフレームの「場を終える（隠す）→ 次の場を始める（出す）」は畳むので、**欄から欄へ移ってもキーボードがちらつかない**。
場が無くなれば `HideKeyboard`・`AllowIme(false)`。

**Android の古い写しの見張り（フォーカスの直後の写しの扱い）**: 新しい場で IME の本文を差し替えた直後の 500 ms（`ECHO_BARRIER_TIMEOUT`）は、
**差し替える前に IME が持っていた本文（前の欄の最後の本文）と同じ本文の写しだけ**を捨てる（前の欄の古い InputConnection の遅れた写しで、新しい欄の本文を
上書きしない）。それ以外の写し（新しい欄での打鍵）はすぐ受けて見張りを外し、差し替えた状態と同じ写し（返り）が来ても見張りを外す。新しい欄の本文と
同じ本文の写しは区別できないが、当てても本文は変わらないので受ける。捨てたときは `[SEED TEXT INPUT] 前の欄の遅れた写しを捨てました（長さ N）` を出す。
PC は返りも遅れた写しも無いので張らない。
（2026-09-30 の実機〈Simeji〉では、キーボードが隠れている間の差し替えには返りが来なかった〈1 回目の欄の開始 12 回・2 回目の 7 回のすべて。時間切れのログは 6 回〉。
返りが来たのは、キーボードを出したまま欄を移ったとき〈2 回目の 1 回。+1 ms〉だけ。以前の「返りが来るまで全部捨てる」作りは、返りが来ないので 500 ms 以内の
最初の打鍵を捨てる作りだった〈実機で抜けた記録は無い。コードからの推論〉ので、この形に直した。§13.1）

**描く理由**: Android の JNI は知らせを積むときにイベントループを起こす（render_policy の on_demand で止めていても遅れない。W2-10a）。
PC の IME・キーは WindowEvent なので winit が起こす。カーソルの点滅は部品が `Redraw.RequestAfter` で次の切り替えの時刻を申告する。

## 6. Android（GameTextInput。E-06）

| 部分 | 中身 |
|---|---|
| Java `MainActivity` | `stateChanged`・`onEditorAction`・`onSoftwareKeyboardVisibilityChanged` を上書き（super を先に呼ぶ）し、`onApplyWindowInsets` で `WindowInsetsCompat.Type.ime()` の bottom を読む。中身は `input/TextInputBridge`（W2-0 の `spike/ImeSpikeLog` と `onImeInsetsChanged` の上書きは外した） |
| Java `input/TextInputBridge` | JNI 4 本: `nativeOnTextState(byte[] 本文の UTF-8, int 選択の起点, int 動く端, int 変換の始め, int 終わり)`・`nativeOnEditorAction(int)`・`nativeOnKeyboardVisibility(boolean)`・`nativeOnImeHeight(int)`（IME の高さは前と同じなら送らない）。古い libSEED.so（関数が無い）では 1 度だけログを出して続ける |
| native `text_input/jni_receivers.rs` | 受けた知らせをエンジンの箱（`engine::core::text_input::inbox`）へ積む（積むとイベントループを起こす）。**本文はログへ出さない**。確かめ用に種類と長さ・添字だけを logcat（タグ `SEED` の `[SEED TEXT INPUT]`）へ出す |
| native `text_input/platform.rs` | `android_main` で複製した `AndroidApp` を持ち、エンジンの命令を実行する: `SetEditorInfo` → `set_ime_editor_info`（Text = `TYPE_CLASS_TEXT`、Number = `TYPE_CLASS_NUMBER`、アクション、`IME_FLAG_NO_FULLSCREEN`・`IMG_FLAG_NO_EXTRACT_UI`）、`SetState` → `set_text_input_state`（添字は UTF-16 のまま）、`ShowKeyboard` → `show_soft_input(false)`、`HideKeyboard` → `hide_soft_input(false)`。実行した命令も logcat へ（本文は伏せる） |
| しないこと | winit の `TextEvent`・`TextAction`（読み捨てられる。I-1）に頼らない。ネイティブから `text_input_state()` を読まない（I-7・I-12） |

キーボードは描画面の上に重なる（GameTextInput は窓を `setDecorFitsSystemWindows(false)` にする。I-9）。欄をキーボードの上へ出すのは部品（§10）。
ソフトキーボードが出ている間の戻るの 1 回目は IME が受けて閉じ、アプリには届かない（R-8）。

## 7. スクリプトの API

`docs/scripting_api.md` §7.20。要点:

- `SEED.TextInput`（`scripting/src/Api/TextInput.cs`）: `Begin(options, text, selStart, selEnd)`・`End`・`ActiveSession`・`IsActive`・`TryGetState`・`SetText`・
  `SetSelection`・`TakeEvents`（TextChanged・SelectionChanged・Action・PasteBlocked・KeyboardShown・KeyboardHidden）・`ShowKeyboard`・`HideKeyboard`・
  `SetCaretRect`・`KeyboardHeight`・`KeyboardVisible`。FFI は `text_input_bridge.rs` の 1 本（op で分ける）。
- `SEED.TextMeasure`（`TextMeasure.cs`）: `LineWidth`・`CaretOffsets`・`Metrics`（描画と同じ送り幅 = `advance_em × 大きさ`・カーニングなし・記法は解かない）。
  FFI は `text_measure_bridge.rs`。複数行・枠の大きさは W2-6c の `Text.Measure`。
- `SEED.CanvasScroll.EndInset`（中身の末尾の余白。実行中だけ）。

## 8. 入力欄の部品 `SEED.UI.TextField`

プレハブ `templates/ui/prefabs/text_field.actor`（幅 280・高さ 52・`TextSize = text.field`）と `number_input.actor`（幅 112・`Kind = Number`・`Align = Center`・
`TextSize = text.field_number`・`MaxLength = 7`）。作りは `TextField.cs` の冒頭のとおり（枠の Sprite・CanvasGesture〈タップ・長押し〉、子の Viewport〈透明の Sprite・
CanvasClip〉の下に Selection・Content〈Text〉・Composition・Caret・Placeholder）。

- **大きさ**: レイアウトの大きさ（`CanvasTransform.LayoutSize`）を読むので、縦の CanvasStack の cross_align stretch で親の幅に伸ばせる。無ければ枠の Sprite の大きさ。
  2026-10-02 から欄は毎フレーム自分のレイアウトの大きさを見て（`LayoutSizeWatch`）、変わったら中身を置き直す（以前は次に描き直す出来事まで古い幅のままだった）。
- **左右の余白**（2026-10-02）: `Padding`（`SetPadding`）で欄ごとに決める。負（既定 −1 = `TextFieldLayout.ThemePadding`）ならテーマの `size.field_padding`、0 以上ならその値
  （0 = 余白なし。`TextFieldLayout.ResolvePadding`）。幅の狭い数値の欄で桁が欠けないように詰める（Wake or Pay の W3-2b (5)。幅 112・余白 16 で「250000」の最後の桁が欠けた）。
- **選択の禁止**（2026-10-02）: `AllowSelection = false`（`SetAllowSelection`。Flutter の `enableInteractiveSelection: false`。W3-2b (6)）で、長押しは全選択にせず
  タップと同じ（押した位置へカーソル）、`SelectAllOnFocus` と `SelectAll()` も選ばない、キーボード（PC の Shift ＋ 矢印・Ctrl＋A）・IME が作った選択は次の出来事で
  カーソルの位置（選択の動いた端）へ畳む（`TextFieldSelectionPolicy`）。選択が無いのでコピー・切り取りも起きない（貼り付けは別の `AllowPaste`）。
- **文字の置き場**: Content は枠つきの Text（枠の高さの中で縦の中央・左寄せ・折り返さない）。左端 `TextFieldLayout.TextStartX`: 収まれば揃え（左・中央）、
  はみ出すなら横のスクロール（`ScrollToReveal`: カーソルが枠の外へ出たときだけ、枠の端へ来るまで動かす）。本文は記法を逃がして描く（`TextMarkupEscape`。
  `[` と `{` の前にバックスラッシュ）ので、利用者の打った `[icon:x]`・`{0}` もそのまま出る。カーソルの位置は元の文字で測る（`SEED.TextMeasure.CaretOffsets`）。
- **カーソル・選択・変換中**: カーソルは 2（`size.caret`）× 文字の大きさの 1.25 倍の棒で、`motion.caret_blink`（0.5 秒）ごとに点滅（打鍵・移動のたびに見える側から数え直す）。
  選択は `color.selection` の帯、変換中の区間は下線（`size.composition_underline`）。選択があるときはカーソルを出さない。
- **フォーカス**（`UiFocus`・`IFocusable`）: 欄のタップでフォーカス（タップの位置 `GestureEvent.LocalPosition` にいちばん近いカーソルの位置。サロゲートの組を割らない）。
  フォーカスの間のタップはカーソルを移し、閉じられていたキーボードを出し直す。長押しで全選択（選択を許さない欄はタップと同じ）。**欄の外のタップ**（押して 8 dp 以内で離す）でフォーカスを外す
  （スクロールのドラッグでは外さない。別の欄・ボタンのタップはジェスチャーの段でそちらが先に受ける＝キーボードを隠して出し直さない）。
  **OS に取り消された指（`TouchPhase.Canceled`）はタップと数えない**（Android の戻るのジェスチャー・通知の引き下ろしが画面の端の指を奪ったとき。
  `OutsideTapTracker`。2026-09-30 の実機で、戻るのジェスチャーの指が欄の外のタップになり、戻るキーより先にフォーカスが外れていた。§13.1）。
  完了（Done）でフォーカスを外す（`UnfocusOnDone`）。別の場が始まった・Play の区切りで場を失ったらフォーカスを手放す。
- **戻る**（`IBackConsumer`。BackDispatcher の Focus の層。`TextFieldBackPolicy`）: 戻るが届いたら**必ずフォーカスを外し**、次の規則で受けるかを決める。

  | 場面 | 1 回目の戻る | 2 回目 | 3 回目 |
  |---|---|---|---|
  | Android・根の画面の欄（後ろに戻るを受ける層が無い） | IME がキーボードを閉じる（アプリへは届かない） | フォーカスを外して**受ける**（アプリは背面へ回らない） | 背面へ（`App.MoveTaskToBack`） |
  | Android・ダイアログ・シート・覆い・画面のスタックの中の欄 | IME がキーボードを閉じる | フォーカスを外して**後ろの層へ回す**（ダイアログが閉じる・画面が戻る。Flutter・EditText と同じ） | — |
  | PC（模擬を含む） | フォーカスを外して受ける（閉じるソフトキーボードが無い） | 後ろの層（無ければ背面へ〈PC はログだけ〉） | — |

  後ろの層が受けるかは `BackDispatcher.WouldHandleAfterFocus()`（`BackChain.WouldHandleAfter(BackOrder.Focus)`。副作用なしの問い）で決める。
  問いを持たない層（問いの無い `AddLayer`・`WouldConsumeBack` を上書きせずに `OnBackPressed` を上書きした画面）は受けるとみなすので回す
  （その層が受けなければ背面へ回る。根の画面で戻るを自分で扱うなら `WouldConsumeBack` も上書きする）。
  キーボードが出ているのに戻るがアプリへ届いたとき（ふつうは IME が先に受ける）はフォーカスを外して受ける。
- **本文の同期**: フォーカスの間の本文はエンジンが持ち、`TextChanged` は変わるたびに（変換中の文字の変化も）。フォーカスを外すと最後の本文を `Text` に残す。
  `SetText` はフォーカスの間は入力中の本文も差し替える。`SetTextUnlessFocused` はフォーカスが無いときだけ（外の値との双方向）。
- **ダイアログの 1 行の入力**: `DialogOptions.Input`（`DialogInputOptions`）。`dialog.actor` の札の `Input` の枠（既定は隠す）へ `text_field.actor` を作って本文とボタンの行の間に並べ
  （札の高さは `DialogMetrics.Sections` の入力の区画。題・本文との間 `size.dialog_title_gap`・ボタンとの間 `size.dialog_actions_gap`）、開いたらフォーカス。
  Positive を選ぶと入力欄の文字（`TrimResult` なら前後の空白〈全角を含む〉を落とす）を `DialogHandle.InputText` へ置いてから閉じる。完了でも Positive（`SubmitOnDone`）。
  入力欄の幅は札の中の幅（264）にそろえる（2026-10-02 の直し。以前は作ったばかりの入力欄へ書いていたので効かず、text_field.actor の 280 のまま右へ 16 はみ出した。
  W3-3 (1)。今は入力欄のスクリプトが始まったときに当てる。[ui_navigation.md](ui_navigation.md) §3.2）。

## 9. 見た目とテーマのトークン

状態は 通常・フォーカス・無効・エラー（`TextFieldLooks.Resolve`。Material Design 3 の Outlined text field にそろえた）。トークンの表は `docs/ui_theme.md` §8（`TextFieldTokens`）。

| 状態 | 枠 | 文字 | カーソル |
|---|---|---|---|
| 通常 | `color.outline`・`size.border` | `color.on_surface`（例の文は `color.on_surface_muted`） | — |
| フォーカス | `color.primary`・`size.field_focus_border`（2） | 同上 | `color.primary` |
| エラー（`SetError(true)`） | `color.error`・太い枠 | 同上 | `color.error` |
| 無効（`Interactable = false`） | `color.disabled`・細い枠 | `color.on_disabled` | — |

塗りは既定で無し（`Filled = true` で `color.surface_variant`）。角丸 `radius.field`、左右の内側の余白 `size.field_padding`（16。欄ごとの `Padding` で上書き。2026-10-02）、高さの目安 `size.field_height`（52）、
文字 `text.field`（16）・数値の欄 `text.field_number`（24）、選択 `color.selection`（主の色の 40%）。Wake or Pay の数値の欄（枠線の角丸・約 112 dp・数字は中央で大きめ・
フォーカスで枠が主の色）に合わせた。Android のしずく形のカーソルのつまみは作っていない（§14）。

## 10. キーボードを避ける（`KeyboardAvoidance.cs`・`KeyboardInsetMath.cs`）

フォーカスのある欄（`AvoidKeyboard`）について、キーボードの高さが変わったとき・フォーカスを得たときだけ、欄の祖先を近い順に見て 1 つで避ける:

1. **縦にスクロールする窓**（`CanvasScroll`。有効で縦か両方）: 窓のうちキーボードに隠れる高さ（画面の画素 → 窓の単位）を `EndInset` にし（中身の最後までキーボードの上へ
   スクロールできる）、欄の下端 ＋ `size.keyboard_gap`（16）がキーボードの上端を越えていれば、その分だけ `ScrollTo`（0.25 秒）で上へ送る。
2. **キーボードを避ける入れ物**（`IKeyboardInsetTarget`。今は `Dialog`）: 札の下端 ＋ 余白がキーボードの上端を越えた分だけ札を上へずらす（`CanvasLayoutItem.Translate`）。
   札の上端は安全領域の上端 ＋ 余白より上へは行かない。

フォーカスが外れた・キーボードが隠れたら余白と持ち上げを戻す。座標はどれも画面の画素（`LayoutRect`・`Screen.Height`・`TextInput.KeyboardHeight`）。
ScreenStack の画面の中身でも、中身を縦の `CanvasScroll` に入れておけば 1 で避けられる（鳴動画面）。

## 11. 確かめ方（PC）

- **見本**: `templates/ui/scenes/ui_text_input.scene`（`UiTextInputDemo.cs`）。文字の欄・数値の欄＋スライダ・計算・文字入力の起床確認（貼り付け禁止）・
  名前の変更のダイアログ・スクロールの下の欄。デバッグの命令 `SCRIPT_DEBUG:textinput,<state|rects|focus,<欄>|unfocus|dialog|scroll,<位置>|slider,<値>|error,<欄>,<0|1>>`。
- **IPC の注入**（Play 中だけ。`input/inject/text_command.rs`。PC で日本語の IME を自動では打てず、Android の知らせも PC に無いので、受け口へ直接入れる）:

| 命令 | 意味 |
|---|---|
| `INPUT_TEXT:commit,{文字列}` | 確定（Ime::Commit と同じ受け口） |
| `INPUT_TEXT:preedit,{カーソル},{文字列}` | 変換中の文字列（カーソルは文字の番号・-1 = 末尾。空の文字列 = 取り消し） |
| `INPUT_TEXT:key,{キー名}[,ctrl][,shift]` | 編集のキー（名前は INPUT_KEY と同じ表。Backspace・LeftArrow・Home・End・A,ctrl など） |
| `INPUT_TEXT:action,{done\|next\|go\|search\|send\|previous\|none}` | IME のアクションのボタン |
| `INPUT_TEXT:platform_state,{起点},{動く端},{変換の始め},{終わり},{文字列}` | Android の IME の状態の写し（UTF-16。変換なしは -1,-1）。貼り付けの見分け・数字の欄の捨て方を PC で確かめる |
| `INPUT_TEXT:keyboard,{高さの画素}` | PC のキーボードの模擬（0 = やめる。環境変数 `SEED_SIM_KEYBOARD_HEIGHT` と同じ） |
| `INPUT_TEXT:dump` | 今の場の状態を `INPUT_TEXT_STATE:{JSON}` で返す（添字は UTF-16） |

  `{文字列}` は `"` で始めると JSON の文字列（IPC の読み取りは行の前後の空白を落とすので、前後の空白は `"  田中  "` のように書く）。
  `INPUT_KEY:{名前},down` も入力欄の編集のキーになる（文字は入らない）。
- **診断ログ**: 環境変数 `SEED_TEXT_INPUT_LOG=1` でプラットフォームへの命令の種類を `[SEED TEXT INPUT] 命令: …` へ出す（本文は伏せる。長さ・選択・変換中の区間だけ）。
- **単体テスト**: Rust `cargo test --lib -- text_input text_command text_measure canvas_scroll`、C# `editor/tests/UiComponentsTests`（`TextFieldTests.cs`）。

2026-09-30 の PC の確かめ（411 dp の端末の模擬・`render_policy: on_demand`・`verify_all.py`）は 29 項目すべて期待どおり（`docs/app_platform_roadmap.md` §3.10）。
同じ日の実機の手順 11 の直し（§13.1）の後も 29 項目すべて期待どおり。OS に取り消された指（`TouchPhase.Canceled`）は IPC で注入できないので、
欄の外のタップの判定（`OutsideTapTracker`）と戻るの規則（`TextFieldBackPolicy`・`BackChain.WouldHandleAfter`）は単体テスト（`TextFieldTests.cs`）で確かめる。

## 12. Wake or Pay での使い方

```csharp
// (1) 数値の欄（猶予 1〜5 分。スライダと双方向。欄にフォーカスが無いときだけ外の値で上書き）
grace.TextChanged += (_, t) => { if (NumberText.TryParseClamped(t, 1, 5, out var v)) slider.SetValue(v, notify: false); };
grace.FocusChanged += (_, focused) => { if (!focused) Normalize(); };   // 外れたら収めた値の文字へ直す
grace.Submitted += (_, _) => Normalize();
slider.ValueChanged += (_, v) => grace.SetTextUnlessFocused(NumberText.Format((long)MathF.Round(v)));

// (2) 計算の起床確認（数字の欄＋「答える」。まちがえたら打ち直せるよう完了でフォーカスを外さない）
answer.UnfocusOnDone = false;
answer.Submitted += (_, a) => { if (a == TextInputAction.Done) Check(); };
answerButton.Clicked += _ => Check();   // Check: 読めて正解なら解除・answer.Unfocus()、違えば answer.SetError(true)

// (3) 文字入力の起床確認（手本の文と入力欄。貼り付け・コピー禁止。1 文字ごとに全文一致で自動の解除）
typing.AllowPaste = false; typing.AllowCopy = false;
typing.TextChanged += (_, t) => { if (t == sample) { typing.Unfocus(); Unlock(); } };
typing.PasteBlocked += _ => ShowHint("貼り付けはできません");

// (4) 名前の変更のダイアログ（1 行・例の文・前後の空白を落とす）
var h = Dialog.Show(new DialogOptions { Title = "名前の変更", PositiveText = "変更", NegativeText = "キャンセル",
    Input = new DialogInputOptions { Text = profile.Name, Placeholder = "例：田中太郎", MaxLength = 20 } });
h!.Completed += r => { if (r == DialogResult.Positive && h.InputText is { Length: > 0 } n) profile.Name = n; };
```

差し替えの手順: (a) サブ画面の数値の表示を `number_input.actor` に置き換え（幅 112 は既定）、`Min`/`Max` は画面のスクリプトの `NumberText.TryParseClamped` で持つ。
(b) 鳴動画面の中身を縦の `CanvasScroll`（CanvasClip つき）の下に入れる（キーボードを避ける）。(c) 名前の変更は `DialogOptions.Input`。
(d) 数字の欄の桁の上限は `MaxLength`（上限金額 1000000 なら 7）。(e) `project_settings.json` の `render_policy: on_demand` でもカーソルは点滅する（部品が申告する）。

## 13. 実機での確かめ方（Pixel 6a。利用者の手で）

試験用のアプリ（`com.seedengine.uidevice`。開始のシーンを `ui_text_input.scene` にした複製）を SeedAndroid で入れ、ログは `adb logcat -s SEED` の `[SEED TEXT INPUT]`
（本文は出ない。長さ・選択・変換中の区間・命令の種類）で見る。

1. 一番上の欄（例：田中太郎）をタップ → 日本語のキーボードが出て、枠が紫になり、カーソルが点滅する
2. かなで「たなか」と打ち、変換して「田中」を確定 → 変換中は文字の下に下線、確定で下線が消える。下の行「入力中: …」にも同じ文字が出る
3. キーボードの完了（右下のアクションキー）→ キーボードが閉じ、枠が灰色に戻る
4. 数値の欄（1 の箱）をタップ → 数字のキーボード。4 と打つとスライダが 4 へ動く。キーボードで「あA」に切り替えてかなを打っても欄には入らない
5. 9 を足して完了 → 欄が範囲の上限の 5 に直る（キーボードは閉じる）
6. 計算の欄に 18 → 完了で赤い枠と「ちがいます」。19 に打ち直して完了 → 「正解（解除）」でキーボードが閉じる
7. 「おはようございます」の下の欄に IME のクリップボードから貼り付ける → 入らず「貼り付けはできません」。手で「おはようございます」と打つ → 最後の文字で「一致しました（解除）」になりキーボードが閉じる
8. 「名前を変える」→ ダイアログの欄に今の名前が入り、キーボードが出ても札が隠れない（上へ持ち上がる）。「 田中 太郎 」にして完了 → 閉じて「名前: 田中 太郎」（前後の空白なし）
9. もう一度「名前を変える」→ 戻るを 2 回 → 1 回目でキーボードだけが閉じ、2 回目でダイアログが閉じる（アプリは閉じない）
10. ページを下へスクロールし、一番下の欄をタップ → 欄がキーボードの上に見えるまでページが送られ、キーボードが出たままでもページを上下に最後までスクロールできる
11. その欄（根の画面の欄）にフォーカスがある状態で戻るを 3 回 → 1 回目でキーボードが閉じ、2 回目で枠が灰色に戻る（アプリは閉じない）、3 回目でアプリが背面へ回る。
    戻るは画面の端からのスワイプ（ジェスチャー）でも、3 ボタンの戻るでも同じ
12. どれかの欄をタップしてすぐ（キーボードが出きる前後に）1 文字だけ打つ → その 1 文字が欄に入る（次の打鍵を待たずに見える）

### 13.1 実機の結果

- **2026-09-30（1 回目・Pixel 6a・Simeji）**: 1〜10 は期待どおり。**11 が NG**（2 回目の戻るでアプリが背面へ回った）。ログから 2 つの原因:
  (a) 戻るのジェスチャーの指（画面の左端 (37,1571) で押下 → システムのジェスチャーが指を奪い `Cancelled`。動きは 19 px で欄の外のタップの許容 8 dp ≒ 21 px の内）が欄の外のタップと数えられ、
  戻るキー（`BrowserBack`）が届く前にフォーカスが外れた → フォーカスの層が空 → 見本のシーンには戻る先の画面が無いので `App.MoveTaskToBack`。
  (b) そもそも当時の `HandleBack` は「キーボードを閉じた後の戻るはフォーカスを外して後ろの層へ回す」作りで、根の画面では (a) が無くても背面へ回っていた（手順の書き方と作りの食い違い）。
  もう 1 点、フォーカスの直後 500 ms は IME の返り（送った本文の写し）を待ってそれ以外の写しを捨てる作りだったが、Simeji は返りを送らない（欄の開始 12 回のどれにも
  600 ms 以内の返りが無く、時間切れのログは 6 回）ので、**500 ms 以内の最初の打鍵を捨てる作りだった**（実機で抜けた記録は無い。12 回とも最初の写しは欄の開始の
  0.8 秒より後。コードからの推論）。
  直し: (a) 取り消された指を欄の外のタップと数えない（`OutsideTapTracker`）。(b) 後ろに戻るを受ける層が無い根では、2 回目の戻るはフォーカスを外すだけで受ける（`TextFieldBackPolicy`・`BackDispatcher.WouldHandleAfterFocus`）。
  (c) フォーカスの直後に捨てるのは「前の欄の最後の本文と同じ写し」だけにする（`hub.rs` の `EchoBarrier`。§5）。PC で取り消された指は IPC で注入できないので、(a) は単体テストで確かめた。
  コミット c05e5d93 の説明の「フォーカス直後 500 ms 以内の最初の打鍵が抜けていた」は言い過ぎ（上のとおり、捨てる作りだったというコードからの推論で、実機で抜けた記録は無い）。
- **2026-09-30（2 回目・10:46〜10:51・直した APK〈c05e5d93 の中身〉・Pixel 6a・Simeji）**: 手順は 12（一番上の欄と、キーボードを出したまま移った欄で 1 文字ずつ）・9・10・11 の
  5 項目。利用者の申告はすべて期待どおり。ログと照らすと:
  - **ログでも確認**: フォーカスの直後の打鍵（「た」は欄の開始の +0.878 秒、「お」は +0.733 秒で届き、どちらも受けた。捨てた写しは 0 件）、ページの下の欄のフォーカスと
    キーボードの表示（送りの量はログに出ないので、見え方は利用者の申告）、根の画面の欄での戻る 2 回まで（1 回目は IME がキーボードを閉じてアプリへは届かない。
    2 回目は `BrowserBack` → `[UI] back: focus` → `HideKeyboard` でフォーカスが外れ、アプリは前面のまま。10:47:50 と 10:50:40 の 2 回）。
    戻るのジェスチャーに取り消された指（`Cancelled`）は 4 回あり、どれも戻るキーより先にフォーカスを外さなかった（10:47:50.764 の指は動き 0 px。押した所が欄の外なら〈推定〉、旧コードではタップと数えてフォーカスを先に外した形）。
  - **未確認扱い**: ダイアログの中の戻る 2 回（手順 9）と、根の画面の 3 回目の戻る（背面へ）は、利用者の申告では期待どおりだがログに跡が無い
    （`[UI] modal`・`move_task_to_back` が出ていない。試験アプリを止めた 10:51:40 にも前面にいた）。フォーカスの直後 500 ms 以内の打鍵も実機では起きていない（単体テストだけ）。
  - IME の返り（差し替えた状態と同じ写し）は、キーボードを出したまま欄を移ったとき（10:48:01）だけ +1 ms で来た。キーボードが隠れた状態で始めた 7 回には来なかった。
  - キーボードの高さは文字・数字のキーボードとも 979 px。表示の知らせ（`キーボードの表示: true`）は `ShowKeyboard` の 59〜70 ms 後（起動の直後の最初だけ 167 ms）。

## 14. 制限（`docs/backlog.md` の「W2-6 の残り」）

- 複数行の入力欄（`TYPE_TEXT_FLAG_MULTI_LINE`・改行）は無い（Wake or Pay の v1 は 1 行だけ）。
- Android のしずく形のカーソルのつまみ・選択のハンドル・長押しのメニュー（コピー・貼り付け）・ドラッグでの選択は無い（長押しは全選択。選択を許さない欄はタップと同じ）。
- 選択を許さない欄（`AllowSelection = false`）の畳み方は単体テストだけ（PC の Shift ＋ 矢印・Android の IME の選択の操作を Play・実機では確かめていない）。
- Android の貼り付けの見分けは推測（変換の外の 2 書記素以上の一度の挿入）。次の単語の予測の候補・音声入力も止まる。IME によっては 1 文字ずつ貼る・変換中として貼るものは止まらない。
- PC の Ctrl の組み合わせは物理キーの位置で読む（AZERTY などの配列では位置が違う）。
- キーの状態（`Input.GetKey`）は入力欄が受けたキーでも届く（ゲームのショートカットは画面の側で止める）。
- エディタに埋め込んだ Play（WPF の子の窓）での IME の候補窓・WPF の IME との取り合いは未確認。
- 最大の長さは書記素で数えるが、書記素の区切りは unicode-segmentation の拡張書記素（端末の IME の数え方と違うことがある）。
- キーボードを避けるのは縦のスクロールの窓とダイアログだけ（シート・覆いの中の欄は避けない）。横画面は未確認。
