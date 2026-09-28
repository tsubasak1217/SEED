# アプリ基盤ロードマップ（W1 Android サービス層 / W2 UI 部品群）

ゲームではない**アプリ**（最初の利用者は目覚ましアプリ「Wake or Pay」）を SEED で作るために、エンジン本体へ足す汎用機能の設計と段階の正典。
アプリの要件の出どころは `D:\SEED_projects\WakeOrPay\docs\WAKEORPAY_SEED_SPEC.md`（以下「アプリ仕様」）。ここに書くのは**どのプロジェクトでも使える形**にしたエンジン側の要件・受け入れ基準・検証方法で、Wake or Pay 固有の画面や規則は書かない。

関連する正典: スクリプト API は [scripting_api.md](scripting_api.md)、Android 対応の全体像は [android.md](android.md)、プロジェクト設定は [project_system.md](project_system.md)。未着手・保留の課題は [backlog.md](backlog.md) の「アプリ基盤（W1 / W2）」節。

## 0. 位置づけ

### 0.1 段階（2026-09-27 のユーザーの決定）

| 段階 | 内容 | 置き場所 |
|---|---|---|
| W0 | アプリの仕様の棚卸し・この文書・アプリのプロジェクトの雛形 | 済（2026-09-27） |
| **W1** | **Android サービス層**（`SEED.Platform`）: 正確な目覚ましの予約、前景サービスでの鳴動、フルスクリーン通知、通知、実行時権限、画面点灯とロック画面上の表示、ブラウザ起動、ディープリンク、起動理由。保存の耐久性。v2 で TTS・録音・センサー | SEED 本体 |
| **W2** | **UI 部品群**: レイアウト、スクロール（慣性）、一覧、ボタン／トグル／スライダ、時刻ホイール、文字入力と IME、タブ、モーダル／覆い、グラフ、テーマ、安全領域、戻る操作。プレハブとデータ駆動 | SEED 本体（＋テンプレートライブラリ） |
| W3・W4 | アプリ本体・庭（Wake or Pay のプロジェクト） | `D:\SEED_projects\WakeOrPay` |
| W5 | 外部連携（Discord・メール・SMS・TTS・録音・課金）。エンジン側には安全な保存・ネットワークの権限・App Links などが要る | 両方 |
| W6 | 配布 | 両方 |

### 0.2 前提にするアプリの要件（アプリ仕様 §6 の要約）

| 要件 | 由来 |
|---|---|
| 予定時刻に**確実に**鳴る: 端末スリープ中（Doze）・ロック中・アプリのプロセスが無いとき・再起動の後 | 目覚ましの本分。設計原則 1 |
| 鳴っている間は画面を点け、ロック画面の**上**に鳴動画面を出す（ロックは解除しない）。鳴っていないときはロック画面の上に出ない | Flutter 版の不具合 2 件の教訓（アプリ仕様 §6.12） |
| 戻る・ホーム・最近のタスクから消す・通知のスワイプでは音が止まらない。止めるのはアプリの「解除」だけ。60 分で必ず止まる（安全弁） | アプリ仕様 P-7・P-8 |
| スヌーズ中は常駐通知に「解除」ボタン。押すとアプリが前に出て確定する | アプリ仕様 F-B06・F-B07 |
| 起動したアプリが「なぜ起動したか」（目覚まし・通知のボタン・普通の起動）を知れる | 起動時の復旧と鳴動画面への直行 |
| 鳴った時刻は**予定時刻**として渡す（開いた時刻ではない） | 損失は予定時刻から数える（アプリ仕様 §5.1） |
| お金と履歴を保存する（途中で殺されても壊れない） | アプリ仕様 §4.4 |
| スマホのアプリとしての操作感（当たり判定・慣性スクロール・戻る・安全領域・文字入力） | ユーザーの決定（ゲーム的な独自 UI だが操作感は守る） |

## 1. 前提: いまの SEED の仕組み（2026-09-27 時点）

W1・W2 がつなぐ先。根拠はコードと android.md（W0 で読んだもの）。

### 1.1 Android の構成

| 項目 | いまの形 | 根拠 |
|---|---|---|
| Activity | `MainActivity`（AGDK の **GameActivity** 派生）が唯一。ネイティブとの橋は Rust の android-activity（winit 0.30 の `android-game-activity`）。`launchMode="singleTask"`、`configChanges` を広く列挙（作り直させない） | `runtime/android/app/src/main/AndroidManifest.xml`、`MainActivity.java`、android.md §4.2・§10 |
| プロセス | 1 プロセス = 1 Activity = 1 EventLoop（winit の EventLoop はプロセスで 1 回しか作れない）。**Activity の破棄でセーブを書き出してから `Process.killProcess`**（winit が onDestroy を知らせないため） | `MainActivity.java:244-279`、android.md §8・§14.2 |
| 背面 | `suspended` でセーブ・パイプラインキャッシュを書き出し、物理と音声の出力を止め、サーフェスを破棄してイベントループを Wait にする。前面へ戻るとサーフェスを作り直し、背面の時間を捨てる。C# の状態は保たれる | android.md §4.3・§14.4・§16.2 |
| C# | 同梱の **CoreCLR（.NET 10）** を hostfxr で起動。初回は `files/dotnet/` へ展開（実機 0.5〜0.8 秒）、CLR の起動 61〜175 ms、ユーザースクリプトの読み込み 10〜24 ms。**展開と起動は android_main のスレッドで同期**（ANR の恐れ。APK の更新直後の初回に 44 秒かかった例がある） | android.md §17.11、backlog「同梱 .NET の展開と CLR の起動が android_main で同期に走る」「起動の初期化が android_main スレッドで同期に走る」 |
| APK | Gradle のテンプレートは `runtime/android/` の 1 組だけ（全プロジェクトで共有）。プロジェクトの値は `-Pseed.*` で渡し、`app/build.gradle.kts` が `applicationId` などと `manifestPlaceholders`（`seedScreenOrientation`・`seedAppLabel`・`seedAppIcon` の 3 つ）へ入れる | `runtime/android/app/build.gradle.kts`、android.md §18 |
| SDK | `minSdk 29`・`targetSdk 36`・`compileSdk 36`。Vulkan 1.1 必須。`android:appCategory="game"` 固定。`enableOnBackInvokedCallback="false"`（戻る → Escape を届けるため）。R8 は使わない | `build.gradle.kts:35,48,202`、AndroidManifest.xml |
| 権限・コンポーネント | `<uses-permission>`・`<service>`・`<receiver>`・`<provider>` は main のマニフェストに**1 つも無い**。`INTERNET` はデバッグ版のマニフェストだけ（エディタとの IPC 用）。**プロジェクトごとに権限や intent-filter を足す仕組みは無い**（`AndroidAppSettings.ExtraData` は保存で消えないだけで、ビルドのどこからも読まれない） | `runtime/android/app/src/debug/AndroidManifest.xml`、W0 の調査 |
| Java | `MainActivity`・`ScreenReporter`（安全領域と回転）・`AudioFocusController`（音声フォーカス）・`DotnetJniLibraries`（.NET の暗号の .so の先読み）の 4 ファイル。Kotlin は無い | `runtime/android/app/src/main/java/com/seedengine/runtime/` |
| 起動の Intent | `seed.` で始まる文字列の extra を JSON にしてネイティブへ渡す（起動するシーン・IPC のポート）が、**デバッグ版だけ**（配布版では他のアプリの Intent で途中のシーンへ飛べないように何も渡さない）。`onNewIntent` は無い | `MainActivity.java:192-233` |

### 1.2 JNI の流儀（いまの決まり）

- 方向は **Java → native だけ**。`private static native void nativeXxx(...)` を JNI の命名規則 `Java_com_seedengine_runtime_<Class>_<method>` で `libSEED.so` の関数に結ぶ（RegisterNatives も jni クレートも使わない）。4 本: `nativeFlushSaveData`・`nativeSetLaunchOptions(byte[])`・`nativeOnScreenChanged(int×9)`・`nativeOnAudioFocusChanged(int)`（`runtime/android/native/src/jni_exports.rs`）。
- 値は数値か **UTF-8 の byte[]（JSON）**（修正 UTF-8 の JNI 文字列を避ける）。byte[] は JNIEnv の関数表を番号で呼んで読む（`jni_env.rs`。4 関数だけ）。
- 受けた値はエンジンの `platform::*` の置き場（Mutex・原子変数）へ入れるだけで、エンジンはイベントループの周回かフレームの頭で読む（`platform/screen/`・`platform/audio_focus.rs`）。
- どの関数も本体を `catch_unwind` で包み、失敗はログだけで続ける。Java 側は `UnsatisfiedLinkError` を捕まえて機能を諦める（アプリを落とさない）。
- **ネイティブのスレッドからの `FindClass` はシステムのクラスローダーになり APK のクラスが見えない**（.NET の暗号の初期化で実際に abort した。android.md §17.8）。Java のクラスは Java 側から渡してもらう。
- 環境変数は `super.onCreate` の前に Java の `Os.setenv` で設定する（Rust 2024 の `set_var` は unsafe）。
- **native → Java の呼び出しは 1 つも無い**（W1 で初めて作る）。

### 1.3 スクリプト API で使えるもの

| API | アプリに使える点 | 足りない点 |
|---|---|---|
| `Input`（タッチ） | 最大 10 本・`Began/Moved/Stationary/Ended/Canceled`・指0 がマウスも動かす・素早いタップも 2 フレームに分けて見える | タイムスタンプが無い（慣性の速度はフレーム時間から推定するしかない）・ジェスチャーの判定は無い |
| ポインタイベント（`OnPointer*`） | スクリーンスペースのキャンバスで、`raycast_target` の Sprite の最前面 1 つに届く | 「スクロールを始めたら押下を取り消す」仕組みが無い（押して離せば `OnPointerClick`） |
| `Screen` | `Width/Height`・`SafeArea`・`Orientation`・`DPI` | 安全領域はキャンバスへ自動では効かない（スクリプトが読んで配置する） |
| `Text` | SDF の文字、フォントの指定、枠と自動折り返し（日本語は 1 文字単位＋簡易禁則）、太さ・影・縁取り、インライン画像（`[icon:…]`）、差し込みスロット | 1 つの Text で 4,096 文字まで |
| `Draw` | 線・円・角丸矩形・円弧・リング・折れ線・多角形・ベジエ。レイヤーはスプライト・テキストと同じ軸 | 1 フレーム 4,096 図形・1 図形 1,024 点。**クリップ（はみ出しの切り取り）が無い**（詳細は §3） |
| `SaveData` | キー＝値（int・long・float・string・bool）を JSON 1 ファイルに。背面へ回るとき・Activity の破棄で自動保存 | ~~§2.7 の耐久性の穴~~ → W1-S で対応済み（sync・1 世代前・`RecoveredFrom`・`Batch`・大きな文字列。§2.7） |
| `Assets.ReadText` | pak 同梱でも同じパスでデータファイル（JSON 等）を読める（データ駆動） | 書き込みは無い |
| `Events` | 名前付きのイベントバス（引数 0〜1 個） | — |
| `Scene` | 名前で遷移（現在のシーンは全部破棄） | タブ・覆いには向かない（アプリは 1 シーン＋プレハブで組む） |
| `GameObject.Instantiate` | `.actor` プレハブの生成、親を指定した生成 | 生成・破棄はフレーム末尾に反映 |
| `Application` | `IsPackaged`・`IsDebugAllowed`（デバッグ機能のゲート） | アプリを終える API が無い（backlog） |
| `Audio` | BGM・効果音（rodio＋cpal。Android は AAudio、`USAGE_MEDIA` 固定） | アラームの音（`USAGE_ALARM`）では鳴らせない → W1 の鳴動サービスが鳴らす |

## 2. W1: Android サービス層（`SEED.Platform`）

### 2.1 設計の柱

| # | 柱 | 理由（根拠） |
|---|---|---|
| W1-P1 | **鳴動は Java だけの別プロセス（`:seed_platform`）で完結させる**。予約・鳴動・鳴動の通知・安全弁・再起動後の張り直しはエンジン（Rust・C#）を一切必要としない | ① `MainActivity.onDestroy` はセーブを書き出して `Process.killProcess` する（winit 0.30 が破棄を知らせず、同じプロセスでエンジンを作り直せないため。`runtime/android/app/src/main/java/com/seedengine/runtime/MainActivity.java:244-279`、android.md §14.2）。同じプロセスに前景サービスを置くと「最近のタスクから消したら止まる」。② エンジン（Rust の panic・CLR の例外）が落ちても音は止めない。③ エンジンの冷えた起動（初回の .NET 展開 0.5〜0.8 秒＋CLR の起動 61〜175 ms＋GPU・シーン。android.md §17.11）を待たずに鳴り始める |
| W1-P2 | **スクリプトは「予約の宣言」と「鳴った後の判断」だけを持つ**。何時に・どの音で・何分まで鳴らすかを登録し、鳴ったことを起動理由とイベントで受け取り、止める（解除・スヌーズ）のもスクリプトの命令 | 金額や判定の規則はアプリの持ち物で、プラットフォーム層は規則を知らない（Flutter 版の `SnoozeService.kt` も「お金の規則は Dart、本文を受け取るだけ」だった） |
| W1-P3 | **JNI の面を増やさない**。native→Java は汎用の 1 本 `SeedPlatform.invoke(module, method, byte[] json) → byte[] json`、Java→native はイベント 1 本 `nativeOnPlatformEvent(byte[] json)`。機能は Java のモジュール表と C# の包みで増やす | 既存の流儀（UTF-8 の byte[] で JSON を渡す `nativeSetLaunchOptions`。jni_exports.rs）の延長。機能のたびに JNI 関数・`ScriptHostApi` の欄を足さない |
| W1-P4 | **機能はプロジェクトごとの opt-in**。`project_settings.json` の `android.features` に書いた機能だけが、権限・サービス・受信機としてマニフェストに入る | USE_EXACT_ALARM・前景サービス・フルスクリーン通知は Google Play の審査の対象。無関係なゲームに入れてはいけない（§2.6） |
| W1-P5 | **結果が後で来るものは「要求 ID → 結果イベント」**。権限のダイアログ、設定画面から戻ったときの状態の変化など | スクリプトのフレームを止めない |
| W1-P6 | **起動理由はリリース版でも取れ、しかも偽造できない**。プラットフォーム層が自分で作った PendingIntent（エクスポートしない `activity-alias` 経由）の Intent だけを信用する | 今の起動オプション（`seed.*` の extra）はデバッグ版だけ（他のアプリの Intent で途中のシーンへ飛べないように。MainActivity.java:192-233）。MainActivity はランチャーのためエクスポートされている |
| W1-P7 | **デスクトップでも同じ API が動く**（予約はエディタの Play 中にタイマーで模擬、通知はログ、権限は常に許可）。デバッグ命令で「いま鳴ったことにする」を出せる | 画面の作り込み（W3）は大半をデスクトップで行うため |

### 2.2 構成（W1-0 のスパイクで骨格を確かめた。2026-09-27。細部は W1-1〜W1-4 で決める）

```
[メインプロセス（今の APK）]
 C# SEED.Platform.* → host_api（新カテゴリ）→ Rust engine::platform::bridge
   └ runtime/android/native/src/platform_bridge/（JNI。jni クレート 0.22。E-02）
       ├ invoke → Java の SeedPlatform.invoke ── ContentProviderClient.call（同期・Binder）──→ :seed_platform の PlatformProvider
       └ イベントの受け口 nativeOnPlatformEvent ←── PlatformConnection ←── Binder のコールバック（:seed_platform から）
 app/src/main/java/com/seedengine/runtime/platform/
   ├ SeedPlatform … JNI の入口（invoke と nativeOnPlatformEvent）
   ├ PlatformConnection … ContentProviderClient を持ち続け、Binder のコールバックを PlatformProvider へ登録する
   └ LaunchReason … 起動の Intent が PlatformEntry 経由（信頼できる目覚まし・通知のボタン）かを判定する
 MainActivity（GameActivity）
   ├ onCreate: 起動の Intent が PlatformEntry 経由なら showWhenLocked / turnScreenOn を上げる
   ├ onNewIntent / 起動の Intent → 起動理由 → nativeOnPlatformEvent
   ├ onResume: 未読の記録を取りに行く（コールバックの取りこぼしの保険）
   └ onRequestPermissionsResult → nativeOnPlatformEvent
 activity-alias PlatformEntry（exported=false → MainActivity）… プラットフォーム層の PendingIntent だけがここを通る
 [デバッグ版だけ] app/src/debug/ の DebugControlReceiver（exported。adb の am broadcast -n で予約・停止・計測。W1-0 の同名の受信機の後継）

[:seed_platform プロセス（Java だけ・新規）] app/src/main/java/com/seedengine/runtime/platform/service/
 PlatformProvider（ContentProvider。exported=false）… call("alarm.schedule" …) など同期の命令と問い合わせ・コールバックの登録
   ├ AlarmStore … 予約の控え（端末保護ストレージの seed_platform/alarms.json。一時ファイル → fsync → rename）
   └ EventJournal … 発火・停止・通知の操作の記録（未読をエンジンが取りに来る）
 AlarmScheduler … AlarmManager.setAlarmClock（Doze でも時刻どおり）
 AlarmReceiver（exported=false）… 発火 → 真っ先に RingService を前景で起動（配信に付く 10 秒の一時許可の間に）
 RingService（前景サービス mediaPlayback。音は USAGE_ALARM 固定）
   ├ MediaPlayer（ループ）・バイブ・PARTIAL_WAKE_LOCK
   ├ フルスクリーン通知（→ PlatformEntry）＋鳴動中の常駐通知（端末の使用中はヘッドアップ通知になる）
   ├ 音声フォーカスを失っても・最近のタスクから消されても止めない（onTaskRemoved は記録だけ）
   └ 安全弁（既定 60 分）で自動停止し記録する
 BootReceiver（exported=false・directBootAware）… LOCKED_BOOT_COMPLETED / BOOT_COMPLETED（強制停止からの復帰でも届く）/
   MY_PACKAGE_REPLACED / TIME_SET / TIMEZONE_CHANGED / 正確なアラームの権限の変化で setAlarmClock を張り直す
 既定の音 … app/src/main/res/raw/（音源が読めないときに使う）
```

- **鳴動の流れ**: `AlarmManager.setAlarmClock` → `AlarmReceiver`（:seed_platform）→ `RingService` を前景で起動（正確なアラームの配信は背面からの前景サービス起動の制限の例外。実機では配信に 10 秒の一時許可が付く）→ 音・バイブ・WakeLock → フルスクリーン通知（`PlatformEntry` 行きの PendingIntent。extras に予定時刻・予約 ID・payload）→ 端末がロック中・画面オフなら MainActivity が起動し、使用中なら通知がヘッドアップで出る（W1-0 の実機で両方を確認）→ エンジンが起動理由を読み、スクリプトが鳴動画面を出す → 解除・スヌーズで `Alarms.StopRinging(id)`。W1-0 の実測（Java だけの鳴動画面・両プロセスが無い状態から）は、予定時刻から音 +0.73 s・画面の最初のフレーム +0.95 s（§2.9.1）。
- **エンジンが生きているときの知らせ**: :seed_platform は、エンジンが `call` の `Bundle.putBinder` で登録した **Binder のコールバック**で「記録あり」を知らせ、native が `PlatformProvider` から未読の記録を取る。エンジンが居なければ何も起きない（プロセスを起こさない）。前面へ戻ったとき（onResume）にも未読を取る。自パッケージ宛ての放送は使わない（W1-0 で、受け手のプロセスが別の放送を処理している間 5 秒待たされた。§2.9.1 の F-3）。
- **最初の呼び出し**: :seed_platform が居なければ、最初の `call` はプロセスの起動を待つ（W1-0 で 123 ms）。描画のスレッドからは呼ばない（W1-1）。
  W1-1 の実装: つながっていないときの `invoke` は背面のスレッドで接続を始めて**すぐ** `{"ok":false,"error":"connecting"}` を返し、つながったら
  `platform.connected` のイベントを送る（docs/android.md §25.3）。client は unstable で取る（安定な取得は :seed_platform の死でゲームのプロセスまで片付けられる）。
- **マニフェスト**: 上のコンポーネントの宣言は機能ごとの断片として `android.features` で出し入れし（E-04・§2.5）、Java のコードは常に APK に入れる。`PlatformProvider` だけは機能によらず main に常設（W1-2 の決定）。断片の差し込み方は W1-2 で確かめて **AGP の variant API**（`androidComponents.onVariants` の `sources.manifests.addStaticManifestFile` と `sources.res.addStaticSourceDirectory`）に決めた（E-04・docs/android.md §25.10）。
- **音源**: 予約のときに、スクリプトが渡した `assets://` の音をエンジン側が `files/seed_platform/sounds/<内容のハッシュ>.<拡張子>` へ書き出し、絶対パスを予約の控えに入れる（pak は :seed_platform から読めないため）。読めなければ **モジュールに同梱した既定の音**で鳴らす（無音にしない。Flutter 版も未知の音は bell に落とした）。
- **既存の音声との関係**: エンジンの音声は前面で `AUDIOFOCUS_GAIN`（USAGE_GAME）を要求する（`AudioFocusController.java`、android.md §16.3）。鳴動画面が前に出るとエンジンがフォーカスを取り、鳴動側は失う。**鳴動側は音声フォーカスの喪失で止まらない**ことを必須にする（止めると鳴動画面を開いた瞬間に鳴り止む）。鳴動の音は **`USAGE_ALARM` に固定**する（Android 17 の背面の音の制限の免除の条件。§2.6・X-7）。

#### 鳴動の方針（Flutter 版の穴から決めたもの）

Flutter 版は鳴動の確実さを `alarm` パッケージ 5.12.0 のネイティブ層に任せていた。その実装を読んで見つかった穴を、SEED では最初から塞ぐ（根拠はアプリ仕様 §6.11・§9）。

| 項目 | Flutter 版（alarm 5.12.0） | SEED の W1 |
|---|---|---|
| 予約の API | `setExactAndAllowWhileIdle(RTC_WAKEUP)`。正確なアラームが許可されていなければ不正確な `setAndAllowWhileIdle` に落とす | `setAlarmClock`（Doze でも配信時刻を調整しない。ステータスバーに目覚ましの印が出る）。許可が無いときは予約せず、`Schedule` が false を返し `PermissionStatus.NeedsSettings` を見せる（黙って遅らせない） |
| 鳴動の重なり | 鳴動中に別のアラームの時刻が来ると、後のほうを**黙って捨てる**（両方のストアから消す） | 捨てない。`AlarmQueued` を記録し、今の鳴動が止まったら続けて鳴らす |
| 最大鳴動時間 | 無い（ループ再生が解除まで続く。60 分の安全弁はアプリの起動時にしか効かない） | `MaxRingMinutes` でネイティブが止め、`AlarmRingStopped(Timeout)` を記録 |
| 再起動で時刻を過ぎていたもの | 15 分を超えて過ぎていたら破棄（アプリは知らされない） | 鳴らさずに `AlarmMissed(DeviceOff)` を記録し、次の起動でアプリへ渡す（アプリが「鳴らなかった朝」をどう扱うかはアプリ仕様 §10） |
| 受信機の公開 | `AlarmReceiver` が exported（他のアプリから止められる・鳴らせる） | すべて exported=false。アプリ内の PendingIntent だけで動く |
| キーガード | セキュアでないロックは自動で解除（`requestDismissKeyguard`） | 解除しない（ロック画面の上に出すだけ） |
| 音源が読めない | 無音（例外を握りつぶす） | モジュールに同梱した既定の音、それも駄目なら端末の既定のアラーム音 |
| 音量 | `STREAM_ALARM` を最大にし、1 秒ごとに戻す。5 秒で漸増。止めたら元の音量へ | `ForceVolume`・`KeepVolume`・`FadeInSeconds` で指定（アプリのデータで決める） |
| 時刻・タイムゾーンの変更 | 何もしない（次の起動まで古い絶対時刻で鳴る） | `TIME_SET` / `TIMEZONE_CHANGED` を受けて `AlarmsRescheduled(TimeChanged)` を記録し、アプリが次回の時刻を計算し直す。アプリが起動していなければ UTC の予定のまま鳴らす |
| 再起動直後（ロック解除前） | 非対応（最初のロック解除まで戻らない。記憶・要確認） | 予約の控えと既定の音を**端末保護ストレージ**に置き、受信機・鳴動サービスを `directBootAware` にする（W1-9）。W1-0 で、exported=false・directBootAware の受信機に `LOCKED_BOOT_COMPLETED` が届き、端末保護ストレージの控えから張り直せることを確かめた（強制停止からの復帰で観測。再起動そのものでは未確認）。ロック解除前はエンジンの画面を出せない見込みなので、Java だけの鳴動画面か音と通知だけにする（E-10） |

> W1-0 のスパイク（2026-09-27、Pixel 6a・Android 16。§2.9.1）の結果: (1) 別プロセスの前景サービスは、鳴動画面のタスクを消しても鳴り続けた（確認済み）。(2) フルスクリーン通知から **Java だけの**鳴動画面が、ロック画面の上に冷えた状態で出た（予定時刻から +0.95 s）。GameActivity での計測は W1-4 の頭へ移した。(3) `ContentResolver.call` の往復は 0.64〜0.87 ms（`ContentProviderClient` を持てば 0.36〜0.51 ms）で、10 ms を大きく下回る（確認済み）。(4) Doze の下の測定は、利用者の操作で Doze を抜けたため無効（再試験が要る）。(5) 再起動は未実施（利用者の許可待ち）。(1) の結果で E-01 を決めたので、同一プロセス案は比べ直さない（§5）。
> → W1-4b（2026-09-27。§2.9.2）: (2) は実 GameActivity で予定時刻 +2.03 s（エンジンの最初のフレーム・中央値）、(4) は SEED の APK で測り直して時刻どおり（発火の時点は `min_time_to_alarm` の仕組みで IDLE ではない）、(1) は SEED の `RingService` でも確かめた。(5) は W1-4b でも見送った。

### 2.3 スクリプト API（案）

名前空間 `SEED.Platform`。**同期の呼び出しは「受理したか」だけを返し**、結果や状態の変化はイベント（`Platform.Events`、または既存の `SEED.Events` へ `platform.*` の名前で流す）で届く。時刻はすべて UTC の epoch ミリ秒。

```csharp
// ── 目覚まし（features: "alarm"）─────────────────────────────
public sealed class AlarmRequest
{
    public string Id;                 // 予約の ID（同じ ID は置き換え）
    public long   TriggerAtUtcMs;     // 鳴らす時刻（壁時計の計算はアプリがする）
    public string SoundAsset;         // "assets://..." か端末のファイルの絶対パス。読めなければ既定の音
    public bool   Vibrate = true;
    public float  ForceVolume = -1f;  // 0..1。アラームの音量（STREAM_ALARM）を鳴っている間だけこの値にし、止めたら戻す（負 = 触らない）
    public bool   KeepVolume;         // true なら、鳴動中にユーザーが下げても 1 秒ごとに ForceVolume へ戻す（Flutter 版の volumeEnforced）
    public float  FadeInSeconds = 5f; // 音量の漸増（0 = 最初から最大）
    public int    MaxRingMinutes = 60;// 安全弁。これを過ぎたら自動で止めて AlarmRingStopped(Timeout)
    public string Title;              // 鳴動中の通知・フルスクリーン通知の題
    public string Body;               // 同・本文
    public string PayloadJson;        // 起動理由・イベントにそのまま返す任意の JSON（アプリのアラーム ID 等）
}
public static class Alarms
{
    public static bool   IsSupported { get; }                 // Android かつ features に "alarm"。デスクトップは模擬で true
    public static bool   CanScheduleExact { get; }            // 正確なアラームの特別なアクセス（Android 12 系）。不要な版は true
    public static bool   Schedule(AlarmRequest request);      // 予約（控えにも書く。再起動後も残る）
    public static bool   Cancel(string id);
    public static bool   CancelAll();                         // W1-3 の実装では bool（取り消せたか。案では void だった）
    public static ScheduledAlarm[] GetScheduled();            // 控えの一覧（id, TriggerAtUtcMs, PayloadJson）
    public static RingingAlarm?    GetRinging();              // 鳴動中（id, ScheduledAtUtcMs, StartedAtUtcMs, PayloadJson）。無ければ null
    public static bool   StopRinging(string id);              // 音と鳴動の通知を止める（解除・スヌーズ）
}
// イベント: AlarmStarted { Id, ScheduledAtUtcMs, StartedAtUtcMs, PayloadJson }
//           AlarmRingStopped { Id, Reason = Stopped | Timeout | Error }
//           AlarmQueued { Id, ScheduledAtUtcMs, WaitingFor }        // 別の鳴動中に時刻が来た（捨てずに待たせる）
//           AlarmMissed { Id, ScheduledAtUtcMs, Reason = DeviceOff | PermissionRevoked | StartFailed }
//           AlarmsRescheduled { Reason = Boot | TimeChanged | PackageReplaced | PermissionChanged }

// ── 通知（features: "notifications"）──────────────────────────
public enum NotificationImportance { Low, Default, High }
public sealed class NotificationAction { public string Id; public string Label; }
public sealed class NotificationRequest
{
    public string Id; public string ChannelId;
    public string Title; public string Body;        // Body は長文（BigText）で出す
    public bool   Ongoing;                           // 常駐（スワイプで消えにくい。Android 14+ は消せる場合がある）
    public string Category;                          // "alarm" / "reminder" / "status" など
    public NotificationAction[] Actions;             // 最大 3。押すとアプリが起動し LaunchReason = NotificationAction
    public string PayloadJson;
}
public static class Notifications
{
    public static void EnsureChannel(string channelId, string name, NotificationImportance importance, string description); // W1-5 の実装では bool（受け付けたか。description は省略可）
    public static bool Show(NotificationRequest request);
    public static void Cancel(string id);             // W1-5 の実装では bool（出ていない ID でも true）
    public static bool AreEnabled { get; }            // アプリの通知が端末で有効か
}

// ── 権限 ─────────────────────────────────────────────────
public enum PermissionKind { PostNotifications, ExactAlarm, FullScreenIntent, /* v2: */ RecordAudio, SendSms }   // W1-5 の実装では先頭に Unknown（知らない種類のイベント）
public enum PermissionStatus { Granted, Denied, DeniedPermanently, NeedsSettings, NotApplicable }                // W1-5 の実装では先頭に Unknown（Check の失敗）
public static class Permissions
{
    public static PermissionStatus Check(PermissionKind kind);
    public static int  Request(PermissionKind kind);   // 実行時ダイアログ。要求 ID を返し、結果は PermissionResult { RequestId, Kind, Status }（W1-5: 失敗は 0）
    public static void OpenSettings(PermissionKind kind); // 特別なアクセスの画面（正確なアラーム・フルスクリーン通知）／アプリの通知設定（W1-5 の実装では bool）
}
// 前面へ戻ったとき（設定画面から帰ってきた）に状態が変わっていれば PermissionChanged { Kind, Status }
// W1-5 の実装: イベントは PermissionResultEvent（"platform.permission_result"）・PermissionChangedEvent（"platform.permission_changed"）。docs/android.md §25.13・§25.14

// ── 画面・アプリ ───────────────────────────────────────────
public static class Window
{
    public static void SetShowWhenLocked(bool on);      // ロック画面の上に出す＋画面を点ける（setShowWhenLocked / setTurnScreenOn）。既定 off。ロックは解除しない
    public static void SetKeepScreenOn(bool on);        // FLAG_KEEP_SCREEN_ON
    public static void SetSystemBarsVisible(bool on);   // ステータスバー・ナビゲーションバー。安全領域（Screen.SafeArea）はこれに追従
}
public enum LaunchKind { Launcher, Alarm, NotificationTap, NotificationAction, DeepLink, Other }
public sealed class LaunchInfo { public LaunchKind Kind; public string Id; public string ActionId; public string Uri;
                                 public long ScheduledAtUtcMs; public string PayloadJson; }
public static class App
{
    public static LaunchInfo LaunchReason { get; }      // この起動の理由（プラットフォーム層の Intent だけを信用）
    // イベント: Intent { LaunchInfo }（起動後に届いた Intent。singleTask の onNewIntent）
    public static void MoveTaskToBack();                // 戻るの最上位で「閉じずに背面へ」（閉じるとプロセスごと終わり、次の起動が冷える）
    public static bool OpenUrl(string url);             // ブラウザ等で開く（ACTION_VIEW）
    public static void OpenAppSettings();               // 端末の「アプリ情報」
}
public static class Haptics { public static void Tap(); public static void Vibrate(int milliseconds); } // UI の触感
```

- **ディープリンク**（features: "deep_links"）: `android.deep_links: [{ "scheme": "wakeorpay", "host": "…", "path_prefix": "…", "auto_verify": false }]` を MainActivity の intent-filter にする。受け取った URI は `LaunchKind.DeepLink`。v1 のアプリは使わないが、W1 で仕組みを入れ、App Links（`autoVerify` と `assetlinks.json`）は W5。
- **W1-6 の実装で案から変えたところ**（2026-09-27。docs/android.md §25.15・scripting_api.md §7.13）: `Window.SetKeepScreenOn` / `SetSystemBarsVisible`・`App.MoveTaskToBack` / `OpenAppSettings`・`Haptics.Tap` / `Vibrate` は void ではなく bool（受け付けたか。失敗は `Platform.LastError`）。`LaunchKind.DeepLink` は既存の値の番号を変えないよう列挙の末尾（`Other` の後）。`App.OpenUrl` は `file:` / `content:` / `javascript:` を断り（`scheme_not_allowed`）、開けるアプリが無ければ `no_handler`。`Haptics.Vibrate` は 1〜5000 ms（長い値はそろえる）。
- **センサー（W1-8 で入れた。2026-09-27。docs/android.md §25.16・scripting_api.md §7.13）**: アプリ仕様 §10 U-04 で「振る」を v1 に残すと決まったので、v2 から W1-8 へ前倒しした。`Sensors.IsSupported(SensorKind)`・`Start(SensorKind, int rateHz = 50)`・`Stop`・`Read(SensorKind, out SensorSample)`（`Acceleration`・`TimestampMs`・`PeakMagnitude`・`SampleCount`）。重力を除いた加速度（`TYPE_LINEAR_ACCELERATION`、無い端末は加速度から低域通過で重力を引いた値）で、`PeakMagnitude` と `SampleCount` は前回の `Read` からの分（フレームに依らず振りを落とさない）。標本ごとのイベントは流さない。案に無かったものとして、出どころを返す `Sensors.GetSource` と、模擬だけで標本を入れる `Sensors.SimulateSample` を足した。「振った」の判定（閾値・回数・時間）はアプリの純粋ロジック（scripting_api.md に例）。
- **v2（W5）で足すもの**: 読み上げ（TTS。鳴動音の上に重ねる）、録音（AAC/m4a・マイク権限）、安全な保存（Android Keystore）、ネットワークの権限を配布版へ入れる設定、ファイルの選択（SAF。音源の取り込み）。（センサーは W1-8 へ前倒しした。上の項目）

### 2.4 JNI の流儀（今の決まりと、W1 で足す部分）

| 項目 | 今の決まり（段階0〜D） | W1 で足す部分 |
|---|---|---|
| Java→native | `private static native void nativeXxx(...)` を `Java_com_seedengine_runtime_<Class>_<method>` で結ぶ。RegisterNatives も jni クレートも使わない。引数は数値か UTF-8 の byte[]（JSON）。受けた値は engine の `platform::*` の置き場（Mutex・原子変数）へ入れるだけで、エンジンはイベントループの周回・フレームで読む（jni_exports.rs 冒頭、android.md §4.3・§4.4） | `nativeOnPlatformEvent(byte[] json)` の 1 本。イベントはキュー（上限つき）へ積み、フレームの頭でスクリプトへ配る |
| native→Java | **無い**（まだ 1 か所も呼んでいない） | `SeedPlatform.invoke(String module, String method, byte[] json): byte[]` の 1 本。ネイティブのスレッドから `FindClass` するとアプリのクラスが見えない（システムのクラスローダーになる）ので、**起動時に Java から `nativeRegisterPlatformBridge(Class)` で自分のクラスを渡し、GlobalRef で持つ**。JavaVM は `AndroidApp::vm_as_ptr()`（winit 経由の android-activity）から取り、呼ぶスレッドを attach する |
| 呼び出しの手段 | JNIEnv の関数表を番号で呼ぶ（`jni_env.rs`。4 関数だけ） | 必要な関数が 10 前後に増える（NewGlobalRef・GetStaticMethodID・CallStaticObjectMethod・NewByteArray・SetByteArrayRegion・DeleteLocalRef・AttachCurrentThread など）。**`jni` クレート 0.22 を入れる**（E-02 の決定。android-activity 0.6.1 経由で既に `Cargo.lock` にあり、新しい依存は増えない）。Rust 側の試作は W1-1 の最初に行う（W1-0 は Java だけ） |
| UI スレッド | Java 側の各クラスは UI スレッド専用 | ウィンドウ操作（showWhenLocked・システムバー）は `runOnUiThread`。`invoke` は呼び出し元のスレッドで同期に動き、長い処理はしない（Binder の往復 1 回程度） |
| エラー | panic は JNI の境界で受け止め、ログは liblog へ直接 | `invoke` は例外を投げず `{ "ok": false, "error": "…" }` を返す。スクリプトには bool と `Platform.LastError` で見せる |

### 2.5 プロジェクト設定とマニフェスト

`project_settings.json` の `android` 節に足すキー（エディタのモデルは `editor/src/ProjectSettings/AndroidAppSettings.cs`。今も知らないキーは `ExtraData` で保たれる）:

W1-2 で実装した（2026-09-27。正典は docs/android.md §25.10、キーと語彙は docs/project_system.md の `android` 節）:

| キー | 例 | マニフェスト・Gradle への反映（実装） |
|---|---|---|
| `features` | `["alarm", "notifications"]` | SeedAndroid が機能の表 `runtime/android/platform_features.json` から**マニフェストの断片**を `runtime/android/app/src/seedFeatures/AndroidManifest.xml` へ生成し（機能が空でも中身の無い断片を毎回書く）、`build.gradle.kts` が variant の API で main へ重ねる（E-04）。`-Pseed.features` は渡さない（断片の中身が正本）。表に無い名前は注意を出して無視 |
| `deep_links` | `[{ "scheme": "…", "host": "…", "path_prefix": "…", "auto_verify": false }]` | 機能 `deep_links` があるときだけ、断片の MainActivity に 1 件 1 つの intent-filter（VIEW・DEFAULT・BROWSABLE）。形の誤りはビルドと保存を止める |
| `system_bars` | `"visible"`（アプリ）／`"hidden"`（既定。今のゲームの振る舞い） | 断片と同じ置き場の `res/values/seed_platform.xml` の bool `seed_system_bars_visible`（main の既定値 false を上書き）→ MainActivity（`SystemBarsController`）の起動時の既定。スクリプトからの切り替え（`Window.SetSystemBarsVisible`）は W1-6 で入れた（docs/android.md §25.15.3） |
| `app_category` | `"productivity"` | `-Pseed.appCategory`（既定の game は渡さない）→ `manifestPlaceholders` の `seedAppCategory` → `android:appCategory`（語彙は SDK の attrs_manifest.xml の 9 つ） |

機能ごとに入るもの（W1-2 の実装。部品は W1-3・W1-4 で機能の表の `application_elements` に行を足す）:

| 機能 | 権限 | コンポーネント |
|---|---|---|
| `alarm` | `USE_EXACT_ALARM`、`SCHEDULE_EXACT_ALARM`（maxSdkVersion 32）、`RECEIVE_BOOT_COMPLETED`、`WAKE_LOCK`、`USE_FULL_SCREEN_INTENT`、`FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_MEDIA_PLAYBACK`（E-03。予備の `_SYSTEM_EXEMPTED` は入れない）、`POST_NOTIFICATIONS`（W1-2 は `VIBRATE` も入れていた。W1-6 で触感〈`Haptics`〉のために main のマニフェストへ常設に移した。normal 権限） | W1-2 では宣言しない（存在しないクラスを宣言すると起動時に落ちる）。W1-3 で `AlarmReceiver`・`BootReceiver`（`android:process=":seed_platform"`・exported=false・directBootAware。BootReceiver は 6 つの放送の intent-filter）を足した。W1-4 で `RingService`、`activity-alias PlatformEntry` を足す。**`PlatformProvider` は機能の断片ではなく main に常設**（`:seed_platform` は最初の呼び出しまで起動しないので害が無く、features が空でもスクリプトの `Platform.IsSupported` が実機で一貫する） |
| `notifications` | `POST_NOTIFICATIONS` | （通知のボタンは `PlatformEntry` 行きの PendingIntent。Android 12+ はトランポリン禁止なので受信機を挟まない。W1-5 で実装: 部品は無く、常設の `PlatformProvider` の `NotificationModule` が出す。Java は `POST_NOTIFICATIONS` の宣言の有無で機能を判定する） |
| `deep_links` | — | MainActivity の intent-filter（`deep_links` の 1 件ごと） |

`SeedAndroid` の Google Play の要件チェック（`editor/src/Android/Release/AndroidPlatformFeatureChecks`、`runtime/android/play_requirements.json` の `permission_policies`。android.md §24.8）に次を足した: 「features の機能の権限が配布物にそろっているか（無ければ不合格）／features に無い機能の権限が入っていないか（あれば注意）」「`USE_EXACT_ALARM` は目覚まし・カレンダーのアプリとしての申告と説明が要る」「`USE_FULL_SCREEN_INTENT` は通話・目覚ましが中核でないと取り上げられる」「`FOREGROUND_SERVICE_MEDIA_PLAYBACK` は前景サービスの申告が要る」（方針の 3 つは注意）。「前景サービスの種類が `<service>` に宣言されているか」は、サービスを足す W1-4 で配布物のマニフェストから見る（backlog）。

### 2.6 Google Play と Android の制約（2026-09-27 に公式のページで確かめたものは「確認済み」。W1-0 の実機の結果は「実機 W1-0」と併記）

| 項目 | 内容 | 状態 |
|---|---|---|
| 正確なアラームの権限 | Android 12（API 31–32）は `SCHEDULE_EXACT_ALARM`（ユーザーが許可・取り消し可）。Android 13+ は `USE_EXACT_ALARM`（インストール時に自動で許可・取り消せない）か `SCHEDULE_EXACT_ALARM`。Android 14+ の新規インストールでは `SCHEDULE_EXACT_ALARM` は**既定で拒否**。`setExact` / `setExactAndAllowWhileIdle` / `setAlarmClock` はどれもこの権限が要る。API 36 の実機では、`USE_EXACT_ALARM` はインストール時に許可され `canScheduleExactAlarms()` が true（appops は要らない） | 確認済み（developer.android.com「Schedule alarms」「Schedule exact alarms are denied by default」）＋実機 W1-0 |
| `USE_EXACT_ALARM` の Play ポリシー | 目覚まし・カレンダーが中核機能のアプリだけが宣言できる（審査あり）。それ以外は公開できない | 確認済み（Play Console ヘルプ「Permissions and APIs that Access Sensitive Information」を検索結果で確認） |
| 両方を宣言する形 | `SCHEDULE_EXACT_ALARM` に `maxSdkVersion="32"` を付けて `USE_EXACT_ALARM` と並べる形は公式の文書には載っていない（コミュニティの定番） | lint（AGP 9.1.0）は指摘なし（実機 W1-0 のビルド。`runtime/android/spikes/platform_spike/results/lint-results-debug.txt`）。Play Console の事前審査は**未確認** |
| 権限の取り消し | `SCHEDULE_EXACT_ALARM` が取り消されるとアプリは止められ、以後の正確なアラームは全部取り消される。`ACTION_SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED` を受けて張り直す | 確認済み |
| 再起動 | すべてのアラームは電源断で消える。`RECEIVE_BOOT_COMPLETED` の受信機で張り直す | 確認済み（実機での再起動の試験は未実施。§2.9.1） |
| 強制停止 | 強制停止（設定の「強制停止」・`am force-stop`）で予約も PendingIntent も消える（実機で `dumpsys alarm` の保留 2 → 0）。Android 15+ はアプリが停止状態から出たとき（利用者がアプリを開く等）に `BOOT_COMPLETED` を届けて張り直しの機会を与える。実機では exported=false の受信機に `LOCKED_BOOT_COMPLETED` と `BOOT_COMPLETED` が届き、`MainActivity` の起動より前に張り直せた。保険として**起動のたびの全予約の張り直し**も残す（Flutter 版も起動時に `rescheduleAll`） | 確認済み（developer.android.com「Android 15 の動作の変更: すべてのアプリ」の「Changes to package stopped state」 https://developer.android.com/about/versions/15/behavior-changes-all ）＋実機 W1-0 |
| `setAlarmClock` と Doze | 「システムは配信時刻を調整しない。最も重要なアラームとして扱い、必要なら低電力モードを抜けて配信する」 | 確認済み（Doze の下の実機の測定は W1-0 で無効になり再試験待ち。§2.9.1） |
| 正確なアラームと前景サービスの起動 | 「正確なアラームは前景サービスの起動の制限を受けない」。実機では `setAlarmClock` の配信に 10 秒の一時許可（理由 `ALARM_MANAGER_ALARM_CLOCK`）が付き、受信機からの `startForegroundService` が通った。予約を経ない背面の受信機からは `ForegroundServiceStartNotAllowedException` | 確認済み（developer.android.com「Schedule alarms」、「Restrictions on starting a foreground service from the background」 https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start ）＋実機 W1-0 |
| フルスクリーン通知 | Android 14+ の新規インストールでは `USE_FULL_SCREEN_INTENT` は**既定で有効**だが、Google Play ストアは通話・目覚ましが中核でないアプリから取り上げる（Play Console での申告。2025-01-22 以降）。**Play 以外（adb・サイドロード）のインストールでは既定で有効**のまま（実機でも adb でのインストール直後に `canUseFullScreenIntent()` が true）。利用者が設定で切れるので、`NotificationManager.canUseFullScreenIntent()` で確かめて `ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT` の設定画面へ案内する。拒否されるとフルスクリーンの画面は出ず（AOSP の記述では 60 秒のヘッドアップ通知になる）、音は鳴るのに画面が点かない（Flutter 版で実際に起きた）。端末の使用中は、許可があってもヘッドアップ通知になる（実機 W1-0） | 確認済み（Play Console ヘルプ 13392821、AOSP「Full-screen intent limits」 https://source.android.com/docs/core/permissions/fsi-limits ）＋実機 W1-0＋Flutter 版の記録（コミット `7899d17`） |
| 前景サービスの種類 | targetSdk 34+ は種類の宣言と `FOREGROUND_SERVICE_<種類>` の権限が必須。Play Console で種類ごとに「機能の説明・遅延／中断されたときの影響・動画」を申告する | 確認済み（Play Console ヘルプ 13392821） |
| 種類の候補 | `mediaPlayback`（背面での音声の再生。Android 15+ は BOOT_COMPLETED から起動できない＝鳴動では無関係）、`systemExempted`（**`SCHEDULE_EXACT_ALARM` か `USE_EXACT_ALARM` を持つアプリは使える**。そうでなければ例外）、`specialUse`（他に当てはまらない用途。理由を書いて審査。Flutter 版のスヌーズの常駐サービスが使っていた） | 確認済み（developer.android.com「Foreground service types」 https://developer.android.com/develop/background-work/services/fgs/service-types ）＋実機 W1-0（`mediaPlayback`・`systemExempted` とも予約経由で起動できた） |
| 決定（E-03。2026-09-27 W1-0） | 鳴動は **`mediaPlayback`**（申告の用途が明快。予約経由の起動が 3 回とも `ALARM_MANAGER_ALARM_CLOCK` で許可された）で、音は **`USAGE_ALARM`**（下の Android 17 の制限の免除の条件）。`systemExempted` は予備（動作は確認済み・Play の申告での扱いは未確認）。**`specialUse` は使わない**（スヌーズ中の通知は前景サービスにせず普通の通知にする。アプリ仕様 §10 U-11） | 決定 |
| Android 17 の背面の音の制限 | Android 17 では、見えている Activity も（short service 以外の）前景サービスも無いアプリの音の再生・音声フォーカスの要求・音量の変更が**黙って失敗**する（targetSdk によらず全アプリ）。targetSdk 37 のアプリは、背面では「使用中（while-in-use）」の権能を持つ前景サービスが要るが、**正確なアラームの権限を持ち `USAGE_ALARM` の音を扱う**なら免除される。`BOOT_COMPLETED` から起こした前景サービスの音は抑えられる。Android 16 の実機でも、画面の無い鳴動中に `AudioHardening background playback would be muted … level: full` が記録された（今は記録だけで消音されない） | 確認済み（developer.android.com「Background audio hardening」 https://developer.android.com/about/versions/17/changes/bg-audio ）＋実機 W1-0 のログ。対応は §4 の X-7 |
| 通知の権限 | Android 13+ は `POST_NOTIFICATIONS` の実行時許可が要る | 実機 W1-0（`pm grant` の前は `areNotificationsEnabled()` が false） |
| マニフェストの受信機と暗黙の放送 | adb の `am broadcast -a <アクション>` だけ（`-n` 部品名・`-p` パッケージ名なし）の暗黙の放送は、targetSdk 26+ のアプリのマニフェストの受信機に**届かない**。デバッグの操作は `-n` か `-p` を付ける。`-n` の明示の放送は強制停止中のアプリにも届き、停止状態を解く | 実機 W1-0 |
| 通知のトランポリン | Android 12+ は通知（本文・ボタン）の PendingIntent から受信機・サービスを経由して Activity を起動できない（logcat に `Indirect notification activity start (trampoline)`）。Activity を直接起動する PendingIntent にする（W1-5 の通知の本文・ボタンは `PlatformEntry` 行きの Activity の PendingIntent） | 確認済み（developer.android.com「Android 12 の動作の変更」の Notification trampoline restrictions。W1-5 の実装時に読んだ。実機では未確認） |
| バッテリー最適化・メーカー独自の省電力 | Samsung・Xiaomi などでは最適化の除外が要ることがある（dontkillmyapp.com）。`REQUEST_IGNORE_BATTERY_OPTIMIZATIONS` は Play の制限つき権限 | 推測（W1 は案内の画面を開く API だけ。要求の権限は入れない） |
| 端末の条件 | SEED の APK は `minSdk 29`・`targetSdk 36`・Vulkan 1.1 必須（`build.gradle.kts:35,48`、AndroidManifest.xml）。Flutter 版は minSdk 26 | 確認済み（コード） |

### 2.7 保存の耐久性（W1-S）

アプリはお金と履歴を SaveData に持つ（アプリ仕様 §4.4）。今の SaveData には次の穴がある（`runtime/src/engine/core/save/store.rs`）:

| 穴 | 今の動き | 直し方（案） |
|---|---|---|
| 置き換えの隙間 | `flush` は「.tmp へ書く → **既存の save.json を削除** → rename」（:269-289）。削除と rename の間に落ちると save.json が無くなる（`.tmp` からは自動で戻らない） | 削除をやめて rename だけにする。Rust の `std::fs::rename` は Unix でも Windows でも**既存の宛先を置き換える**（Rust 標準ライブラリの文書で確認。コメントの「Windows の rename は上書き不可」は古い） |
| 電源断 | fsync をしていない | 一時ファイルを `sync_all` してから rename し、Android ではフォルダも fsync する |
| 壊れたファイル | 読めない save.json は**空として読み**、次の保存で上書きする（:136-165） | 1 つ前の世代（`save.json.bak`）を残し、本体が無い・壊れていれば `.bak` から読む。どちらかから復旧したことをスクリプトが知れる（`SaveData.RecoveredFrom`） |
| 大きな文字列の書き込み | C# の `ScriptHost.SaveSetString` が値の UTF-8 を `stackalloc byte[値の長さ]` で**スタックに**確保する（上限なし。`scripting/src/Api/ScriptHost.cs:826-841`）。数百 KB〜MB の JSON を 1 キーに入れるとスタックが溢れ、プロセスが落ちうる（.NET のスタックオーバーフローは捕まえられない） | 一定の長さ（例 1 KB）を超えたら `ArrayPool<byte>` のヒープを使う。文字列を渡す他の FFI（`Text.Content` など）も同じ形か点検する |
| 別スレッドの書き出し | `MainActivity.onDestroy` は UI スレッドから `nativeFlushSaveData` を呼ぶ（Mutex で守られるのは 1 回の書き出しだけ）。スクリプトが複数のキーを順に書き換えている最中に入ると、半端な組み合わせがディスクに残りうる | 複数キーの更新を 1 まとまりにする `SaveData.Batch(() => { … })`（その間の自動書き出しを待たせる）を足すか、「1 文書を 1 キーに入れる」使い方を scripting_api.md に書く（Wake or Pay は後者で作る） |

**済（2026-09-27 W1-S。上の案どおりに実装）**。コードは `runtime/src/engine/core/save/`（単一責任で分けた: `value`・`codec`・`file_set`・`durable_file`・
`recovery`・`batch`・`store`・`mod`）と `scripting/src/Api/Interop/Utf8Arg.cs`。利用者向けは docs/scripting_api.md §7.7、Android の置き場は docs/android.md §14.2。

| 穴 | 実装 |
|---|---|
| 置き換えの隙間 | 既存の save.json の削除をやめ、rename だけで置き換える（`durable_file.rs`）。Rust 1.98 の標準ライブラリの文書と実装で確認: `rename` は「`to` があれば置き換える」、Windows は `MoveFileExW(MOVEFILE_REPLACE_EXISTING)`、だめなら `SetFileInformationByHandle` の `FILE_RENAME_FLAG_REPLACE_IF_EXISTS \| POSIX_SEMANTICS` |
| 電源断 | 一時ファイルを `File::sync_all`（Windows は `FlushFileBuffers`、Unix は `fsync`）してから rename。rename の後に Unix（Android）だけフォルダも `File::open(dir)?.sync_all()`。新しく作った保存先のフォルダは親フォルダも sync |
| 壊れたファイル | 書き出しの順序を「.tmp を書いて sync → save.json を save.json.bak へ rename（無ければ飛ばす）→ .tmp を save.json へ rename → フォルダを sync」にし、読み込みを「save.json → 無い・壊れていれば .bak → 空」にした（落ちる位置ごとに残るものの表は `durable_file.rs` の先頭）。壊れた save.json は `save.json.corrupt-<UTC の yyyyMMdd-HHmmss>` へ rename で退避し 1 つだけ残す。`SaveData.RecoveredFrom` は案の `None` / `Backup` に **`Lost`（本体か .bak が有ったのに読めず空で始めた）を足した**（`None` だと初回の起動と見分けられず、データを失ったことをアプリが利用者へ知らせられないため）。復旧したときは未書き出しとして始め、次の書き出しで save.json を作り直す（毎回の起動で Backup と出続けないように） |
| 大きな文字列の書き込み | 共通の入れ物 `Utf8Arg`（ref struct）: UTF-8 の最大長が 1,024 バイト以下と文字数から保証できる文字列（340 文字まで）だけ呼び出し元の `stackalloc`、それ以外は `ArrayPool<byte>.Shared` から借りて `using` で返す。`ScriptHost.cs` の文字列を渡す FFI 呼び出し 24 か所（32 個の文字列）をすべてこの形にした（上限なしの `stackalloc byte[長さ]` は 0 件）。受け皿（`SaveGetString`・`TryGetString`・`AssetText`）の 1 回目は定数 260 バイトのスタック、足りなければヒープのままで問題なし。`PlatformInvoke` は元からヒープ |
| 別スレッドの書き出し | **両方**: `SaveData.Batch(Action)`（深さと「待たせた書き出しの要求」はストアと同じ Mutex の中。自動保存・明示の Save は Batch の途中なら書かずに覚え、最も外側の終わりに 1 回だけ書く。C# は `try/finally` で例外でも深さを戻す。取り消しはしない）と、scripting_api.md §7.7 の「1 文書を 1 キーに入れる」例 |

確かめたこと: Rust の単体テスト 55 件（`cargo test -p SEED --lib -- core::save`。壊れ方ごと: 通常・.tmp だけ残る・本体が無く .bak・本体が壊れている・2 回書くと .bak が 1 世代前・Batch の途中の書き出しは待たされ終わりに 1 回・旧形式の save.json をそのまま読み同じ形で書き戻す・2 MB）。
PC の Play（`SaveSmoke.scene`。`SEED_SAVE_DIR` で一時フォルダへ）で 2 MB の文字列の往復・340/341 文字の境目・2,000 文字のキー・Batch の待たせ（save.json を直接読んで確認）・
Batch の中の例外の後の Save、壊した save.json からの `Backup`、両方壊した状態からの `Lost`。Android は `cargo ndk` と SeedAndroid のビルドまで（実機の `kill -9` の繰り返し＝AC-10 は W1-7）。

**W1-7 で書き出しの手順 2 を変えた（2026-09-27）**: 実機の `kill -9` × 100（AC-10）の 1 回が「save.json を .bak へ rename した後・.tmp を save.json へ
rename する前」に当たり、save.json が無い状態から `.bak` で起動した（`RecoveredFrom=Backup`。失ったのは書きかけの 1 回分）。そこで手順 2 を
「本体の写しを `save.json.bak.new` に作り（本体は動かさない）→ `.bak` へ rename」にし、**どの瞬間にも本体がある**ようにした（`durable_file.rs` の表）。
写しは PC は hard link、**Android は複製**（読んで書いて sync）: 最初は hard link にしたが、Android のアプリは SELinux でアプリのデータに hard link を
作れず（実機で `avc: denied { link } … untrusted_app_34 … app_data_file`・`Permission denied (os error 13)`）、rename へ戻っていた。写しを作れないとき
（容量が足りない等）だけ W1-S の rename に戻る（警告はプロセスで 1 回）。複製の分だけ大きなセーブは重い（実機で 2.4 MB の `Save()` が約 230 → 290〜320 ms）。
単体テストは 59 件（新規 4: hard link と複製のどちらでも 3 世代と作りかけが残らない・手順 2 の直後も本体がある・hard link の失敗を注入すると複製・
hard link と複製の失敗を注入すると rename）。実機の結果は §2.9.2。

### 2.8 受け入れ基準（W1 の完了の条件）

実機（Pixel 6a・Android 16。W1-7 の途中〈T4 の再起動〉で Android 17 に更新された）とエミュレータ（API 34・35・36）で確かめる（W1 ではエミュレータは使わなかった）。Flutter 版の「実機で確認すべきこと」（`wake-or-pay:README.md:1166-1190`）を引き継いだ。

| # | 基準 |
|---|---|
| AC-1 | 画面オフ・ロック中に、予定時刻から **2 秒以内に音**が鳴り始め、**3 秒以内に**ロック画面の上にエンジンの画面（最初のフレーム）が出る。`dumpsys deviceidle force-idle` の下でも同じ。根拠（W1-0 の実測。§2.9.1）: 両プロセスが無い冷えた状態から、配信 +1 ms・音 +0.73 s・Java だけの鳴動画面の最初のフレーム +0.95 s（温かいときは音 +0.15 s・画面 +0.22 s）。エンジンの画面が 3 秒に収まるかは X-1 と AC-12 が前提で、W1-4 の頭で測る。**実機 W1-4b（2026-09-27。§2.9.2 の T1・T3）: 満たした**。実 GameActivity（Wake or Pay・スクリプトの無い空の開始シーン）を両プロセスが無い冷えた状態から、画面オフ・ロック中の 3 回の中央値で、音 +0.26 s（`RingAudio` の開始。AudioFlinger のトラックの開始は +0.37 s）・エンジンの最初のフレーム +2.03 s（最大 +2.10 s）。`force-idle` の下（T3）でも音 +0.54 s（トラック +0.69 s）・最初のフレーム +2.13 s。ただし T3 の発火の時点は深い Doze（IDLE）ではなかった（`setAlarmClock` の予約が `min_time_to_alarm`〈この端末は 1 時間〉より近いと、DeviceIdleController が自分で IDLE を抜ける。§2.9.2）。**実機 W1-7（直した後の回帰。§2.9.2 の T1。端末は Android 17）: 音は満たした・画面は OS の更新の直後だけ超えた**: 画面オフ・ロック中の冷えた起動で音（トラックの開始）+514 ms、最初のフレーム +4608 ms（Android 17 への更新でパイプラインキャッシュが使えず DrawContext 3249 ms）。キャッシュを作り直した後の 2 回目（画面点灯・ロック中）は音 +340 ms・最初のフレーム +1124 ms。更新の直後の最初の鳴動は backlog。Doze（T3）は W1-7 では測り直していない |
| AC-2 | 鳴動中に最近のタスクからアプリを消しても**音が続き**、通知からアプリへ戻れる。**実機 W1-4b（§2.9.2 の T2）: 前半を満たした**（鳴り始め +4.2 s に `am stack remove` → メインプロセスは `onDestroy` で終わり、`:seed_platform` と `USAGE_ALARM` の再生は 3 s 後・13 s 後とも続いた）。「通知からアプリへ戻れる」は adb では確かめていない（タップが要る）。**W1-7: 変わらず**（後半は手作業が要るので未実施。W1-7 の修正〈見張り・ループ〉は前半の経路を変えていない。T5 で `:seed_platform` の死からも戻るようになった）。**W1-7 の手作業の回（2026-09-28。§2.9.2 の M3）: 後半も満たした**: 鳴動中に `am stack remove` でタスクを消す → メインプロセスが終わっても音は続き、利用者が通知をタップするとメインプロセスが冷えた状態から起き、アプリへ戻れた（起動理由 `notification_action`） |
| AC-3 | 戻る・ホーム・通知のスワイプでは止まらない。`StopRinging` でだけ止まる。`MaxRingMinutes` で自動で止まり、`AlarmRingStopped(Timeout)` が次の起動で届く。**実機 W1-4b（§2.9.2）: 一部**。`StopRinging` で止まり `ring_stopped(stopped)` が記録された（7 回すべて）・タスクを消しても止まらない（T2）。**`:seed_platform` がプロセスごと殺されると黙って止まる**（T5。`ring_stopped` も無い。backlog）。戻る・ホーム・スワイプ・安全弁は実機では未確認。**W1-7: 大半を満たした**: `:seed_platform` の `kill -9` からは見張りの予約で鳴動が戻る（2 回。無音 20.1 s / 15.4 s。`STOP_RINGING` で止まり音量も戻る）・強制停止では戻せず次の起動で `ring_stopped(error)`。安全弁は T4 で 60 s に止まり `ring_stopped(timeout)` が記録された（次の起動で届くのは記録〈`journal.json` の seq 23〉まで確かめた。スクリプトが受け取るところは見ていない）。戻る・ホーム・スワイプは手作業が要るので未実施 |
| AC-4 | `adb reboot` の後も予約が残り（`dumpsys alarm`）、時刻どおりに鳴る。端末の時刻・タイムゾーンを変えても、アプリが渡した UTC の時刻で鳴る（壁時計の再計算はアプリが `AlarmsRescheduled(TimeChanged)` を受けて行う）。**実機 W1-4b: 未実施**（利用者が外出先で端末を使っていて、再起動で PIN の入力が要るため見送った。手順は §2.9.2 の T4）。**実機 W1-7（§2.9.2 の T4）: 満たした**: `adb reboot` の後、ロック解除の前（`RUNNING_LOCKED`）に `LOCKED_BOOT_COMPLETED` で張り直し（`alarms.rescheduled(boot)`）、予定から +26 ms で発火・鳴動・安全弁で停止・音量が戻った。起動の直後に端末の時計が約 75 秒進んでいて `TIME_SET` で戻った（`rescheduled(time_changed)` 2 件）が、予約は UTC の時刻どおりに鳴った。タイムゾーンの変更は端末の設定を変えない約束なので試していない |
| AC-5 | 鳴っていないときは、アプリを開いたまま画面を消して電源ボタンを押すと**ロック画面が出る**（アプリが上に出ない）。**W1-7: 未実施**（電源ボタンは手作業が要り、`input keyevent` は使わない約束。仕組みは W1-6 の開き直しの忘れ対策〈docs/android.md §25.15.7〉）。**W1-7 の手作業の回（2026-09-28。§2.9.2 の M5）: 満たした**: 普通に起動したアプリ（起動理由 `launcher`）を前面に出したまま、利用者が電源ボタンで画面を消して点け直すと、ロック画面が出た（`isKeyguardShowing=true`・`mKeyguardOccluded=false`・前面は `NotificationShade`・スクリーンショットで確認）。目覚ましで起きた後にアプリが下ろし忘れた場合（`SetShowWhenLocked(false)` を呼ばない）は試していない |
| AC-6 | 目覚ましで起動したとき、**リリース版でも** `App.LaunchReason` が `Alarm`・予約 ID・予定時刻・payload を返す。`adb shell am start -n <applicationId>/com.seedengine.runtime.MainActivity --es …` で偽装しても `Alarm` にならない。**W1-7（デバッグ版）: 満たした**: `am start -n …/MainActivity --es com.seedengine.runtime.platform.extra.LAUNCH '{"kind":"alarm",…}'` は「起動理由: launcher」（2 回。T5 の強制停止の試験）。目覚ましの起動は W1-4b の T1 で `alarm`。`PlatformEntry` の偽装は `not exported` で拒否（W1-4b の T6）。**リリース版では未確認**（この PC のプロジェクトにアップロード鍵が無い） |
| AC-7 | 通知のボタンを押すと、アプリが死んでいても起動し `NotificationAction`（ボタンの ID・payload）が取れる。生きていれば `Intent` イベントで届く。**W1-7: 一部**（生きているときは W1-4b の T6 で利用者の「開く」のタップが `onNewIntent` → `platform.launch`〈`notification_action`〉で届いた。アプリが死んでいるときのボタンは手作業が要るので未実施）。**W1-7 の手作業の回（2026-09-28。§2.9.2 の M1〜M4）: 鳴動の通知では満たした**: 生きているとき「開く」→ `onNewIntent` の `notification_action ac7a/open`・本文 → `alarm ac7b`（どちらも `platform.launch` を読み込み済みのエンジンへ流した）。メインプロセスが死んでいるとき「開く」→ 冷えた起動で `App.LaunchReason` = `NotificationAction`（`ac2/open`）・本文 → `Alarm`（`ac7c`）をスクリプト（PlatformSmoke）で読めた。W1-5 のアプリの通知（`Notifications.Show` のボタン）と payload の往復は試していない |
| AC-8 | 通知の実行時権限を求めて結果イベントが届く。正確なアラーム・フルスクリーン通知の状態が取れ、設定画面を開いて戻ると `PermissionChanged` が届く。`appops` で拒否した状態でも落ちない。**W1-7: 未実施**（権限の画面の操作は手作業が要り、`pm grant/revoke`・`appops set` は使わない約束。W1-6 の実機の回で PlatformSmoke の権限の要求が動いたところまで〈docs/android.md §25.15.10〉）。**W1-7 の手作業の回（2026-09-28。§2.9.2 の M7・M8）: 一部**: 通知の設定画面（`APP_NOTIFICATION_SETTINGS`）で利用者がオン → 戻ると `権限 post_notifications の状態が変わりました: denied → granted`（`platform.permission_changed` を読み込み済みのエンジンへ流した）。**オフにすると Android がアプリの両プロセスを止める**（`Killing … PermissionHelper`）ので、戻ったときは新しいプロセスで起動し直し、前回の状態が無いので `PermissionChanged` は届かない（アプリは起動時の `Permissions.Check` で拒否を知る）。実行時の確認の画面・`appops` の拒否は未実施 |
| AC-9 | エンジンを意図的に落としても（デバッグ命令で panic）鳴り続ける。**実機 W1-7（§2.9.2 の T8）: 満たした**（panic の命令は無いので、より強い形としてエンジンのメインプロセスを `run-as … kill -9`。鳴り始め +3 s に殺しても `:seed_platform` は同じ pid のまま、`USAGE_ALARM` の再生は 4 s 後・8 s 後とも続き、`STOP_RINGING`〈新しいメインプロセスから〉で止まり音量が戻った。panic の命令は backlog） |
| AC-10 | `SaveData.Save()` の直後にプロセスを `kill -9`（デバッグ版の `run-as`）する試験を 100 回繰り返しても save.json が消えず壊れない。壊した save.json からは前の世代で起動する。**実機 W1-7（§2.9.2）: 直して満たした**。W1-S の書き出しでは 100 回中 1 回（74 回目）が「save.json を .bak へ rename した後・.tmp を save.json にする前」に当たり、save.json が無い状態から `.bak` で起動した（`RecoveredFrom=Backup`。失ったのは書きかけの 1 回分・壊れは 0）。手順 2 を「本体の写し〈Android は複製〉を .bak.new → .bak」にして（§2.7。hard link は Android の SELinux が拒んだ）もう一度 100 回: **save.json が無い瞬間 0 回・`RecoveredFrom` は 100 回とも None・起動の回数は 1→100 で一度も減らない**（Save の途中で殺せた回 6。うち .bak.new が残った回 4）。壊した save.json からは `Backup` で起動し、壊れた本体は `.corrupt-…` へ退避 |
| AC-11 | `features` を書いていないプロジェクトの APK には、W1 の権限・サービスが 1 つも入らない（`aapt2 dump permissions` / `badging`）。例外: `VIBRATE`（normal 権限。W1-6 から触感のために main に常設。ゲームにも使う）と常設の `PlatformProvider`・`PlatformEntry`。**満たした**（W1-2 の aapt2: `features: []` の APK は `INTERNET`〈デバッグ版〉と androidx の 1 つだけ。W1-7 で足した見張りの受信機も機能 `alarm` の断片だけに入る〈`AndroidPipelineTests` 161 件〉） |
| AC-12 | 鳴動画面の最初のフレームが、フルスクリーン通知から 3 秒以内に出る（冷えた起動・Pixel 6a）。出るまでも音は鳴っている（前提: §4 の X-1。起動の初期化の同期を解く）。**実機 W1-4b（2026-09-27。§2.9.2 の T1）: 満たした**（フルスクリーン通知の起動 → エンジンの最初のフレームが 3 回の中央値 1.72 s・最大 1.82 s。その間も音は鳴っている）。X-1 の同期の初期化は解いていないままで収まった（空のシーン・スクリプトの型 1 つ。実アプリの鳴動画面では測り直す）。**実機 W1-7（§2.9.2 の T1）**: フルスクリーン通知 → 最初のフレームは、OS の更新（Android 17）の直後でパイプラインキャッシュが使えなかった 1 回目が **4.30 s（超えた）**、キャッシュを作り直した 2 回目が 0.85 s。その間も音は鳴っていた。更新・アプリの入れ替えの直後の最初の鳴動は X-1 と合わせて backlog |
| AC-13 | デスクトップの Play で同じスクリプトが動き（予約は模擬のタイマー、通知はログ、権限は許可）、デバッグ命令で「いま鳴った」を起こせる。**満たした**（W1-3〜W1-8 の各回に PC の Play で PlatformSmoke が OK〈docs/android.md §25.12.9・§25.16.8 など〉。W1-7 は模擬を変えていない） |
| AC-14 | `docs/scripting_api.md`（と html）・`docs/android.md` の新しい節・`docs/project_system.md` の `android` 節が更新され、Play の要件チェックが新しい権限を扱う。**満たした**（W1-1〜W1-8 で各節を足し、Play の要件チェックは W1-2 の `permission_policies`。W1-7 で docs/android.md §14.2・§25.11.3・§25.12・§25.16、scripting_api.md §7.7 と html の書き出しの順序を直した。前景サービスの種類の要件チェックは backlog のまま） |

### 2.9 実機での確かめ方（手順の骨子）

```bash
# 予約の確認（setAlarmClock なら「alarm clock」の欄に出る）
adb shell dumpsys alarm | grep -A3 <applicationId>
# Doze を強制する／戻す
adb shell dumpsys deviceidle force-idle
adb shell dumpsys deviceidle unforce
# 前景サービスとプロセス
adb shell dumpsys activity services <applicationId>
adb shell ps -A | grep <applicationId>          # :seed_platform が居るか
# メインのプロセスだけを殺す（:seed_platform は残るはず）。am kill は前面のプロセスを殺さないので、デバッグ版の run-as で kill する
adb shell run-as <applicationId> kill -9 <メインのプロセスの pid>
# 最近のタスクから消す（スワイプと同じ removeTask。W1-0 で確認）
adb shell am stack list                          # 鳴動画面の taskId を調べる
adb shell am stack remove <taskId>
# デバッグ版の受信機へ命令する（暗黙の放送はマニフェストの受信機に届かないので -n を付ける。§2.6）。SEED の受信機の名前は DebugPlatformReceiver
# （W1-4b で SCHEDULE / CANCEL_ALL / STOP_RINGING / GET_RINGING / LIST を足した。予約は必ず max_ring_minutes 1。docs/android.md §25.12.8）
adb shell am broadcast -n <applicationId>/com.seedengine.runtime.platform.DebugPlatformReceiver -a com.seedengine.runtime.platform.SCHEDULE --ei seconds 90 --es id t1 --ei max_ring_minutes 1
# 特別なアクセスを拒否して試す（Android 14+）
adb shell appops set <applicationId> SCHEDULE_EXACT_ALARM deny
adb shell appops set <applicationId> USE_FULL_SCREEN_INTENT deny
# ロック・画面オフ
adb shell input keyevent KEYCODE_POWER
adb shell dumpsys window | grep -i keyguard
# 再起動
adb reboot
# ログ
adb logcat -s SEED SEEDPlatform
# 実時間を待つ試験では、試験の間ずっとファイルへ流す（main バッファは 256 KiB で約 5 分で消える。§2.9.1 の F-5）
adb logcat -v epoch -b main,system,crash,events > logcat.txt
```

- 確かめ用のシーン（例 `templates/scenes/platform_probe.scene`＋スクリプト）を用意し、「1 分後に予約」「いま止める」「通知を出す」「権限を求める」をボタンで出す。結果は画面と logcat（`[SEED PLATFORM]`）に出す。
- SeedAndroid の `run` と `logcat` で回し、`--scene` で確かめ用のシーンから起動する（android.md §20.10）。
- 実時間を待つ試験（Doze・再起動・60 分の安全弁）は、`MaxRingMinutes` を短くした設定で行い、60 分は 1 回だけ通しで確かめる。
- Java の純粋な部分（予約の控えの JSON・記録の読み書き・次に張り直す対象の選び方）は JVM の単体テストで固める（Gradle の `testDebugUnitTest`）。
- W1-0 のスクリプト（`runtime/android/spikes/platform_spike/scripts/`。logcat を流し続ける・目印を待つ・dumpsys を記録する・利用者が操作中なら待つ・後片付け）は、パッケージ名と受信機の名前を SEED の APK に向け直せば W1-7 の確認に使える。

### 2.9.1 W1-0 スパイクの結果（2026-09-27、Pixel 6a / Android 16）

Java だけの使い捨てアプリ（applicationId `com.seedengine.platformspike`。コードと手順は `runtime/android/spikes/platform_spike/`）で、§2.2 の骨格（`:seed_platform` の `PlatformProvider`・`AlarmReceiver`・`RingService`・`BootReceiver`、メインプロセスの鳴動画面 `RingActivity`＝GameActivity の代役、exported=false の別名 `RingEntry`）を実機で測った。端末は Pixel 6a（Android 16・`CP1A.260405.005`）、APK は targetSdk 36・minSdk 29、鳴動音は -60 dBFS の短いビープ（端末の音量は変えない）。時間は予定時刻（`setAlarmClock` に渡した時刻）からの差で、端末の時計で測った。証拠は `runtime/android/spikes/platform_spike/results/summary.md` の節（表の右の列。生ログは私物端末の他のアプリの情報を含むのでリポジトリに入れていない）。エミュレータでは測っていない。

| # | 項目 | 結果・数値 | 証拠（summary.md の節） |
|---|---|---|---|
| 1 | ロック画面の上に出るまで（画面オフ・ロック中。予約の後に両プロセスを `am kill` し、プロセスが無い状態から） | **成功**。予定時刻から: 配信 +1 ms → `:seed_platform` の起動 +55 ms → `AlarmReceiver` +357 ms → `startForeground` +404 ms → SystemUI がフルスクリーン通知を起動 +427 ms → **音 +733 ms** → 鳴動画面の onCreate +764 ms → **最初のフレーム +952 ms**（音から 208 ms。システムの `Displayed +518ms`）。遷移は `KEYGUARD_OCCLUDE` で、最初のフレームの時点で `keyguard_locked=true`（ロック画面の上。ロックは解除しない）。起動の Intent の部品名が別名 `RingEntry` なので `trusted=true`。1 回目（ログから再構成）は `KEYGUARD_OCCLUDE` の要求 +704 ms・`wm_activity_launch_time` 787 ms。両プロセスが温かいとき（06 の B）は音 +154 ms・最初のフレーム +224 ms | 「01・02」「01a」「06」 |
| 2 | 最近のタスクから消した後（鳴動中に鳴動画面のタスクを `am stack remove`＝最近のタスクから消すのと同じ removeTask） | **音は続いた**。消して 3 秒後・13 秒後とも `:seed_platform`（pid 4828）の `USAGE_ALARM` の再生が `state:started`、消した後の心拍 4 回すべて `playing=true`。`RingService.onTaskRemoved` は呼ばれる（止めない）。メインプロセスの鳴動画面には `onDestroy`（`finishing=true`）が来た。SEED の `MainActivity` は `onDestroy` で `Process.killProcess` するので、同じプロセスに置いた鳴動はここで止まる（コードからの推論） | 「01・02」 |
| 3 | Doze の下の時刻精度 | **無効（要再試験）**。`force-idle` で `mState=IDLE` に入れた後、発火の約 4 分前に利用者が端末を使い始め、発火時は INACTIVE だった。そのときの配信は +1 ms、表示はヘッドアップ通知 | 「03（1 回目）」 |
| 4 | 強制停止の後 | 予約は消える（`dumpsys alarm` の保留 2 → 0。PendingIntent も取り消される）。停止状態から出ると（今回は adb の明示の放送で出た）、exported=false の `BootReceiver` に `LOCKED_BOOT_COMPLETED` と `BOOT_COMPLETED` が届き、端末保護ストレージの控えから `MainActivity` の起動より前に張り直せた | 「04」 |
| 5 | プロセス間の往復（各 10 回の中央値。温まった 2 回の範囲） | `ContentResolver.call`: ping 0.64〜0.79 ms・256 バイトの byte[] 0.70〜0.87 ms。`ContentProviderClient` を持ったまま: 0.36〜0.38 ms・0.50〜0.51 ms。AIDL（256 バイト）: 0.24〜0.26 ms（bind 3.5〜18.8 ms）。Messenger: 0.35〜0.65 ms。**冷えた最初の `ContentResolver.call`**（`:seed_platform` の起動込み）は **123 ms**。両プロセスを落とした直後の 1 回目の計測はどれも 2〜4 倍遅い（ping 2.8 ms）。`:seed_platform` → メインの放送は 5 秒待たされた（下の F-3） | 「05」 |
| 6 | 前景サービスの種類と起動の条件 | `mediaPlayback`: 予約経由で 3 回とも許可（`Background started FGS: Allowed … code:ALARM_MANAGER_ALARM_CLOCK`）。`systemExempted`: 予約経由で許可（`types=0x00000400`）。予約を経ない背面の受信機からは `ForegroundServiceStartNotAllowedException`（`mAllowStartForeground false`）で拒否 | 「06」「01・02」「01a」「03（1 回目）」 |
| 7 | 再起動の後の張り直し | 未実施（`adb reboot` は利用者の許可が要る）。手順は `scripts/t7_reboot_procedure.sh` | — |
| 8 | 安全弁 | `max_ring_s` での自動停止は 3 回（60 s・20 s・20 s）とも時間どおり。止まった知らせ（自パッケージ宛ての放送）が前面の鳴動画面へ届くまで 5 ms | 「01a」「06」「03（1 回目）」 |
| 付 | 権限（API 36） | `USE_EXACT_ALARM` はインストール時に許可され `canScheduleExactAlarms()` が true（appops 不要）。`USE_FULL_SCREEN_INTENT` は adb でのインストールで既定で許可（`canUseFullScreenIntent()` が true）。`POST_NOTIFICATIONS` だけ実行時の許可が要る（`pm grant` の前は `areNotificationsEnabled()` が false）。lint（AGP 9.1.0）は `SCHEDULE_EXACT_ALARM`（`maxSdkVersion="32"`）と `USE_EXACT_ALARM` の併記を指摘しない | 「00」「lint」 |

**未実施とその理由**

| 項目 | 理由 | 次に行う所 |
|---|---|---|
| ~~Doze の下の時刻精度（§2.2 の (4)・AC-1 の後半）~~ | 1 回目は、発火の前に利用者が端末を使い始めて Doze を抜けたので無効。`scripts/t3_doze.sh` は、使い始めたら予約を取り消して中止するよう直した | **済（W1-4b の T3。§2.9.2）**: SEED の APK で測り直した（配信の受信 +0.24 s・音 +0.54 s・最初のフレーム +2.13 s）。発火の時点は IDLE ではなく、その理由は Android の仕組み（`min_time_to_alarm`）と分かった。1 回目が INACTIVE だったのも、利用者の操作より先にこの仕組みで IDLE を抜けていた見込み（推論） |
| 再起動の後の張り直しと Direct Boot（§2.2 の (5)・AC-4・E-10） | `adb reboot` は利用者の許可が要る。`scripts/t7_reboot_procedure.sh --reboot-permitted` で、ロックを解除しないまま張り直され鳴るか（`LOCKED_BOOT_COMPLETED`・`user_unlocked=false`）まで見る | **未実施のまま**（W1-4b でも見送った。1 回の許可は得たが、試験の日は利用者が外出先で端末を使っていて、再起動で PIN の入力が要るため。手順は §2.9.2 の T4） |
| ~~実際の GameActivity をフルスクリーン通知から冷えた状態で出す計測（§2.2 の (2)・AC-12）~~ | スパイクの鳴動画面は Java だけの Activity。SEED の冷えた起動（.NET の展開・CLR・GPU・シーン。X-1）を含む本当の危険はこちらにある | **済（W1-4b の T1。§2.9.2）**: 画面オフ・ロック中の冷えた起動で、エンジンの最初のフレームが予定時刻 +2.03 s（中央値）・フルスクリーン通知から 1.72 s |

**途中で見つけたこと**

| # | 見つけたこと | 扱い |
|---|---|---|
| F-1 | **Android 17 の背面の音の制限**。Android 16 の実機でも、画面の無い鳴動中（タスクを消した後・ヘッドアップ通知のとき）に `AudioHardening background playback would be muted for com.seedengine.platformspike (10423), level: full` が記録された（今は記録だけで、音は止まらない）。公式では Android 17 は全アプリが対象で、targetSdk 37 では背面の前景サービスに「使用中」の権能が要るが、**正確なアラームの権限を持ち `USAGE_ALARM` で鳴らす**なら免除される（§2.6） | 鳴動の音は `USAGE_ALARM` に固定し、正確なアラームの権限を保つ（E-03・X-7）。音量の変更（`ForceVolume`・`KeepVolume`）と v2 の読み上げが免除に入るかは Android 17 で確かめる |
| F-2 | 端末の使用中は、フルスクリーン通知が画面を出さず**ヘッドアップ通知**になる（`sysui_heads_up_status=1`。安全弁で止まるまで出たまま） | 通知の本文のタップ（`contentIntent`。`PlatformEntry` 行き）から鳴動画面へ行けるようにする（W1-4） |
| F-3 | `:seed_platform` → メインプロセスの自パッケージ宛ての放送は、受け手のプロセスが別の放送（`goAsync` で処理中）を終えるまで **5.0 秒**待たされた（`dumpsys activity broadcasts history` の `+5s5ms since enq`）。処理中の放送が無い前面の受け手には 5 ms で届いた | エンジンへの知らせは放送ではなく Binder のコールバックにする（E-02・§2.2） |
| F-4 | `adb install`（初めてのパッケージ）が、ロック画面の裏に出た Play Protect の確認（アプリをスキャンに送信するか）で約 43 分止まった（利用者は操作しておらず、確認の画面が出たまま完了した） | 自動の実機試験では、インストールに上限時間を付け、止まったら端末で確認に答えるよう案内する。SeedAndroid の `install` でも初回に起こりうる（未確認） |
| F-5 | 端末の logcat の main バッファは 256 KiB で、約 5 分で古い行が消える（1 回目の鳴動の目印を失い、system・events のバッファから再構成した） | 実時間を待つ試験では、試験の間ずっと `adb logcat -v epoch -b main,system,crash,events` をファイルへ流す（スパイクの `scripts/common.sh` の `start_stream`。§2.9） |
| F-6 | 端末のシェル（mksh）の算術は 32 ビットで、`$(( $(date +%s%N) / 1000000 ))` が桁あふれした（待ち時間の計算が狂い、1 回目の鳴動は安全弁まで放置された） | 時刻の計算はホスト側（64 ビットの bash）で行う（dotnet_host スパイクと同じ注意） |
| F-7 | `setAlarmClock` の配信には **10 秒の一時許可**が付く（`dumpsys alarm` の `temporaryAppAllowlistDuration=10000`・理由 `ALARM_MANAGER_ALARM_CLOCK`）。前面で重いアプリが動いている使用中の端末では、発火から前景サービスの許可まで 1.79 s かかった | `AlarmReceiver` は重い処理をせず、真っ先に `startForegroundService` する（10 秒を過ぎると背面の起動の制限に当たる見込み。推論）。W1-4 |
| F-8 | §2.6 の誤り 2 件（Play 以外のインストールでのフルスクリーン通知の既定・adb の暗黙の放送） | §2.6 を直した |

### 2.9.2 W1-4b の実機の結果（2026-09-27、Pixel 6a / Android 16）

Wake or Pay のデバッグ版 APK（`com.wakeorpay.seed`・arm64-v8a）を Pixel 6a（Android 16・`CP1A.260405.005`）に入れ、**スクリプトの無い既定の開始シーン**
（`scenes/Main.scene`。空）で鳴動を測った（`PlatformSmoke.scene` は予約と停止をスクリプトがするので使わない）。予約・停止は W1-4b でデバッグ版の受信機
`DebugPlatformReceiver` に足した命令（`SCHEDULE`・`CANCEL_ALL`・`STOP_RINGING`・`GET_RINGING`・`LIST`。docs/android.md §25.12.8）で行い、
時間は予定時刻（`trigger_at_utc_ms`）からの差を端末の時計（`logcat -v epoch`）で測った。予約はすべて `max_ring_minutes` 1 で、鳴り始めから 20 秒以内に
`STOP_RINGING` で止めた（T1 は約 5 秒）。**T3 からは利用者の依頼で音を最小にした**（`force_volume` 0＝STREAM_ALARM の最小の段階 1/7・振動なし・漸増 60 秒。
止めた後に STREAM_ALARM が試験の前の値〈speaker 5〉に戻ったことを `dumpsys audio` で確かめた）。W1-0 のスパイク（`com.seedengine.platformspike`）は
予約・プロセスが無いことを確かめてから試験の前にアンインストールした。計測のスクリプトと証拠（logcat・dumpsys）はリポジトリの外
（`C:\Users\k023g\.claude\jobs\434062fd\tmp\wop_w1_4b\` の `scripts/`・`results/`。生ログは私物端末の他のアプリの情報を含むので入れない）。エミュレータでは測っていない。

| 試験 | 結果 | 数値・観察 | 証拠（`results/` の下） |
|---|---|---|---|
| T1 ロック画面からの冷えた起動（AC-1・AC-12・X-1） | **満たした**（3 回とも） | 画面オフ（`Dozing`）・ロック中・両プロセスを `am kill` した状態から、中央値で**音 +261 ms**（AudioFlinger のトラックの開始 +368 ms）・フルスクリーン通知の起動 +308 ms・**エンジンの最初のフレーム +2030 ms**（最大 +2102 ms）・通知から最初のフレームまで 1722 ms。鳴動中は `isKeyguardShowing=true`・`mKeyguardOccluded=true`（ロックは解けずに上に出た）・遷移は `KEYGUARD_OCCLUDE`・起動理由 `alarm`（起動の部品名は別名 `PlatformEntry`。GameActivity は `libSEED.so` を見つけて読んだ）。前景サービスは `Background started FGS: Allowed`（`uidState: RCVR`） | `t1_run1`・`t1_run3`・`t1_run4`（`result.env`・`stream.txt`・`2_ringing_*`）。`t1_run2` は画面が点いたロック中の参考 |
| T2 最近のタスクから消す（AC-2） | **音は続いた** | 鳴り始め +4.2 s に `am stack remove` → メインプロセスは `MainActivity.onDestroy(isFinishing=true)` から 0.4 s で終わり、`:seed_platform` と `USAGE_ALARM` の `state:started` は消して 3.3 s 後・13.4 s 後とも続いた。`RingService.onTaskRemoved` は「鳴らし続けます」。+18.9 s の `STOP_RINGING`（受信機が新しいメインプロセスを起こした）で `stopped:true`・`ring_stopped(stopped)` | `t2_task_remove` |
| T5 鳴動中に `:seed_platform` を `kill -9` | **黙って止まった**（直していない。設計案は下。→ **W1-7 で直し、見張りで戻ることを確かめた**。下の「W1-7 の実機の結果」） | 鳴り始め +4.4 s に kill → 約 0.1 s でプロセスの死・`am_foreground_service_stop`・鳴動の通知の取り消し（`notification_canceled`）・AudioFlinger のトラックの終了。10 s 待っても作り直されない（`START_NOT_STICKY`。`dumpsys activity services` は空）。メインプロセス（ロック画面の上の MainActivity）は生き残った（unstable な client）。次の命令で `:seed_platform` は作り直されたが `RingRegistry` は空で `stop_ringing` は `stopped:false`、`ring_stopped` も `missed` も記録されない | `t5_kill_platform` |
| T6 `PlatformEntry` 経由の `onNewIntent` | **既存の MainActivity に届いた**（経路は利用者のタップ） | MainActivity がロック画面の上にいる（画面点灯）ときに 12 秒後の予約が鳴ると、フルスクリーン通知は起動されず**ヘッドアップ通知**になった（`sysui_heads_up_status 1`）。その場にいた利用者が「開く」を押した（+4.1 s `notification_action_clicked`）→ 指紋の確認（alternate bouncer。+5.6 s で認証・解除）→ `START … PlatformEntry … result code=3`（先頭の既存の Activity へ配達）→ `起動後に届いた Intent の理由: notification_action t6/open` → エンジンに `platform.launch`。タスクは 1 つのまま・ActivityRecord も pid も同じ（Activity は積まれない）。adb の `am start -n …/PlatformEntry --es …LAUNCH '{…}'` は `SecurityException: Permission Denial … not exported from uid 10424` で拒否（adb から起動理由を偽装できない。直後の `START u0 … from uid 2000 … result code=0` の行は出るが、Intent は届かず起動理由のログも無い） | `t6_platform_entry` |
| T3 Doze（`force-idle`。AC-1 の後半・W1-0 の残り） | **時刻どおりに鳴った**（発火の時点は IDLE ではない） | `battery unplug` → `force-idle` で `mState=IDLE` → 30 s 後に DeviceIdleController が自分で ACTIVE → `QUICK_DOZE_DELAY` へ移った（`device_idle: [0,alarm]`。`setAlarmClock` の予約が `min_time_to_alarm`〈この端末は `+1h0m0s`〉より近いと深い Doze に留まらない Android の仕組み。AOSP の記憶と実機の記録から。他の wake-from-idle の予約はこの間に無かった）。発火は `device_idle_wake_from_idle`・受信 +240 ms・音 +535 ms（トラック +689 ms。T1 より遅い分には `force_volume` で音量を 5 → 1 にする処理が入っている。内訳は測っていない）・最初のフレーム +2128 ms。止めて 5 に戻った。`unforce`・`battery reset` で戻した | `t3_doze`（`idle_timeline.txt`・`deviceidle_log_excerpt.txt`） |
| T4 再起動と Direct Boot（AC-4・E-10） | **未実施**（→ **W1-7 で実施**。下の「W1-7 の実機の結果」） | 利用者は外出先で端末を使っていて（T6 で指紋で解除した）、再起動すると PIN の入力が要り、解除まで他のアプリの通知も止まるため、指示の「迷えば未実施」に従った。手順は下 | `scripts/t4_reboot.sh` |

T1 の段階ごとの時刻（予定時刻からの ms。3 回は画面オフ・ロック中。2 回目は前の回の目覚ましが点けた画面のまま〈この端末は充電中に画面を消さない設定〉で、参考）:

| 段階 | 1 回目 | 3 回目 | 4 回目 | **中央値** | 参考: 2 回目（画面点灯・ロック中） | 参考: T2 の鳴動 |
|---|---|---|---|---|---|---|
| `:seed_platform` の起動（`am_proc_start`） | 65 | 38 | 39 | **39** | 57 | 56 |
| `PlatformProvider.onCreate` | 191 | 163 | 174 | **174** | 184 | 169 |
| `AlarmReceiver.onReceive` の先頭（受信） | 199 | 170 | 181 | **181** | 191 | 182 |
| `startForegroundService` | 210 | 177 | 187 | **187** | 203 | 192 |
| `startForeground` の完了 | 231 | 206 | 216 | **216** | 226 | 219 |
| 音（`RingAudio` の `MediaPlayer.start`） | 261 | 312 | 252 | **261** | 278 | 246 |
| 音（AudioFlinger のトラックの開始 `AT::add`） | 366 | 424 | 368 | **368** | 366 | 312 |
| フルスクリーン通知の起動（`START u0 … PlatformEntry`） | 308 | 311 | 279 | **308** | 269 | 279 |
| メインプロセスの起動 | 327 | 326 | 314 | **326** | 281 | 294 |
| `MainActivity.onCreate`（起動理由 `alarm`・`KEYGUARD_OCCLUDE` の要求） | 651 | 804 | 1080 | **804** | 735 | 551 |
| `Displayed`（システム。起動時間 +806 / +864 / +1200 ms） | 1107 | 1155 | 1465 | **1155** | 1089 | 897 |
| エンジンの描画面の作成（`[SEED SURFACE] created`） | 1153 | 1184 | 1487 | **1184** | 1107 | 914 |
| **エンジンの最初のフレーム**（`[SEED FRAME 0] end`） | 2030 | 1980 | 2102 | **2030** | 1732 | 1548 |
| フルスクリーン通知 → 最初のフレーム | 1722 | 1669 | 1823 | **1722** | 1463 | 1269 |

エンジンの中の内訳（1・3・4 回目）: `android_main` の開始 +688 / +836 / +1112 ms、同梱 .NET の準備 72 / 51 / 40 ms（展開済みの確認だけ。
展開は無い）、CLR の起動（ランタイム起動）139 / 57 / 124 ms、**DrawContext の作成 695 / 625 / 524 ms**（パイプラインキャッシュ 656 KiB を読んだ上で）・
描画パイプラインの生成の合計 794 / 699 / 576 ms、最初のフレームの処理 64 / 49 / 23 ms。シーンは空（アクター 0）で、スクリプトの型は 1 つ（`PlatformSmoke`。
付けたアクターは無い）。W1-0 の Java だけの鳴動画面（+0.95 s）との差の約 1 秒は、メインプロセスの起動から `onCreate` まで（0.3〜0.8 s）とパイプラインの生成（約 0.6 s）。

**T5 の設計案（W1-4b では直していない → W1-7 で (b) のとおり入れた。見張りは `setAlarmClock` ではなく `setExactAndAllowWhileIdle`〈ステータスバーの印・「次のアラーム」に出さない。配信に前景サービスの一時許可が付くことを実機の `dumpsys alarm` と T5 で確かめた〉。docs/android.md §25.12.11）**: ① 鳴らし始めに「鳴動中の予約・鳴り始め・安全弁の時刻・`force_volume` の前の音量」を端末保護ストレージ（`seed_platform/ringing.json`。
`DurableFile`）へ書き、止めたら消す。② 作り直しの道は 2 つ。(a) `START_STICKY` にして、作り直された `onStartCommand(null)` で ①から続きを鳴らす
（システムの作り直しは遅れ〈1 s から倍々〉が入るうえ、背面からの前景サービスの起動の制限に当たるかは未確認）。(b) 鳴動中は数秒先に「見張りの予約」
（`setAlarmClock` か `setExactAndAllowWhileIdle`）を張り直し続け、止めたら取り消す。プロセスが死ぬと見張りが発火し、`AlarmReceiver` が ①を見て鳴動を戻す
（`setAlarmClock` の配信が前景サービスの起動を許されることは T1〜T3 で確かめた。`setExactAndAllowWhileIdle` でも許されるかは未確認）。(b) を推す
（`setAlarmClock` なら既に確かめた例外の中で動く。見張りがステータスバーの目覚ましの印に出る点は要検討）。③ 戻せなかったとき（許可が無い等）は
`alarm.ring_stopped{reason:"error"}` を記録してアプリに知らせる。④ `:seed_platform` の起動時の照合（`AlarmStartup`）で ①の残りを見つけたら、
`force_volume` の前の音量へ戻す（G-6）。どちらも実機で T5 をもう一度行って確かめる（音量を変える予約では行わない）。

**途中で見つけたこと（W1-4b）**

| # | 見つけたこと | 扱い |
|---|---|---|
| G-1 | 冷えた起動の約 2 秒のうち、描画パイプラインの生成（DrawContext 0.52〜0.70 s）と、メインプロセスの起動から `onCreate` まで（0.32〜0.77 s）が大きい。.NET の準備と CLR の起動は合わせて 0.1〜0.2 s。3 秒には収まったが、実アプリの鳴動画面（シーンとスクリプト）に使える余りは約 1 秒 | X-1 の根拠として backlog へ。W1-7 で実アプリの鳴動画面で測り直す |
| G-2 | システムの `Displayed`（窓の最初の描画。+1.1 s）からエンジンの最初のフレーム（+2.0 s）まで約 0.9 s、窓は出ているがエンジンの絵がまだ無い（その間に見えるのは窓の背景のはず。画面は見ていない。推論） | 鳴動画面の色に合わせた窓の背景・スプラッシュを backlog へ |
| G-3 | 既定の音（2.22 s の WAV）を `MediaPlayer` のループで鳴らすと、AudioFlinger のトラックが約 2.4 s ごとに止まり（`AT::remove … I`）約 65〜70 ms で再開する（`AT::add … A`）。ループの継ぎ目で途切れている見込み（聴いて確かめていない） | 途切れの無いループ（`setNextMediaPlayer`・`AudioTrack` の `setLoopPoints`・音源の作り方）を backlog へ。**W1-7 で対応**（`setNextMediaPlayer` の連鎖。途切れ 0） |
| G-4 | アプリが前面（ロック画面の上・画面点灯）のときに次の予約が鳴ると、フルスクリーン通知は起動されずヘッドアップ通知になる（T6。F-2 と同じ判断）。`onNewIntent`（`platform.launch`）は来ないので、前面のアプリは `platform.alarm.fired` と `GetRinging` で鳴動を知る。通知の「開く」はロック中は解除（指紋・PIN）を求めた（`dismissKeyguardThenExecute` → alternate bouncer） | アプリの作り方の注意として backlog へ。「開く」・本文のタップを解除なしで鳴動画面へ出せるか（別名をマニフェストで showWhenLocked にする等）は W1-7 |
| G-5 | 鳴動の通知には、ロック中はシステムが `NO_CLEAR` を付けていた（`dumpsys notification` の `flags`。元の `originalFlags` には無い） | backlog「鳴動の通知をスワイプで消せる」の手がかり（解除後に消せるかは未確認） |
| G-6 | `:seed_platform` が鳴動中に死ぬと、`force_volume` で変えた STREAM_ALARM の音量が元に戻らない見込み（`AlarmStreamVolume.restore` は `RingAudio` の停止の経路でしか呼ばれない。コードを読んだだけ。利用者の音量を守るため実機では試していない） | T5 の設計案の④。backlog へ。**W1-7 で対応**（`ringing.json` の volume の欄。背面から戻せないときはアプリが前面に出たとき・次の鳴動で戻る。Android 17 の AudioHardening） |
| G-7 | 停止状態から出たとき（`adb install -r` の後の最初の放送）、`BootReceiver` に `LOCKED_BOOT_COMPLETED`・`BOOT_COMPLETED` が 2 回ずつ（計 4 回）届いた | backlog の「`rescheduled`（boot）が 2 回記録されうる」に追記。**W1-7 で対応**（`BootRearmGate`。4 回届いても記録は 1 回まで） |
| G-8 | `AudioHardening background playback would be muted … level: full` が T2 の鳴り始め +364 ms（画面が出る前）に 1 回記録された（音は続いた。F-1 と同じ） | X-7 のまま |
| G-9 | 試験の道具の注意: (a) `adb shell am broadcast … --es title "W1-4b 計測"` は端末のシェルが空白で分けて題が切れる（`title` と `body` に空白を入れない）。(b) この端末は充電中に画面を消さない設定（`stay_on_while_plugged_in=15`。設定は変えていない）で、目覚ましが点けた画面は消えない。次の回の前にロック中を確かめて `input keyevent KEYCODE_SLEEP` で消した。(c) Git Bash で関数を `&` で起こした `adb logcat` は `kill` で止まらず 3 本漏れた（スクリプトを直し、漏れて書き足された行は切り落とした）。(d) エンジンの居ないメインプロセス（受信機だけで起きたプロセス）も呼び鈴を登録するので、そこで取り出された記録は捨てられる（受け取りの確認〈ack〉が無い間の割り切り） | (a)(b)(d) は docs/android.md §25.12.8 と受信機の説明に書いた |

**T4（再起動）の手順（W1-4b では未実施。W1-7 で `runtime/android/tools/platform_device_tests/t4_reboot.sh` で行った。解除前は受信機が届かず
adb も使えなかったので、止めるのは安全弁〈1 分〉に任せた）**:

```bash
# 前提: USB で接続・画面オフ・ロック中・利用者が使っていない・adb reboot の許可（1 回）。音は最小。スクリプトなら次の 1 行（リポジトリの外の scripts/t4_reboot.sh）
bash scripts/t4_reboot.sh --reboot-permitted t4 240 t4_reboot
# 手で行う場合（Git Bash。MSYS_NO_PATHCONV=1）
APP=com.wakeorpay.seed; R=$APP/com.seedengine.runtime.platform.DebugPlatformReceiver; A=com.seedengine.runtime.platform
adb shell dumpsys audio | grep -A8 '^- STREAM_ALARM:'                 # 試験の前の音量を控える
adb shell am broadcast -f 0x20 -n $R -a $A.SCHEDULE --ei seconds 240 --es id t4 --ei max_ring_minutes 1 --ef force_volume 0.0 --ez vibrate false --ef fade_in_seconds 60
adb shell run-as $APP cat /data/user_de/0/$APP/files/seed_platform/alarms.json   # 控え（端末保護ストレージ）
adb shell dumpsys alarm | grep -B1 -A10 "Alarm{.* $APP}"                        # 保留中の予約
adb reboot; adb wait-for-device; until [ "$(adb shell getprop sys.boot_completed | tr -d '\r')" = 1 ]; do sleep 2; done
adb logcat -v epoch -b main,system,crash,events -T 1.000 > after_reboot.txt &    # 起動の頭から全部。ロックは解除しない
adb shell am get-started-user-state 0                                           # RUNNING_LOCKED のはず
# 見るもの: BootReceiver の LOCKED_BOOT_COMPLETED の張り直しと dumpsys alarm の予約・発火の AlarmReceiver / RingService / 音（USAGE_ALARM）・
#   MainActivity（directBootAware でない）が出ないこと・PlatformProvider（directBootAware でない）が解除まで作られないこと（推論）
adb shell am broadcast -f 0x20 -n $R -a $A.STOP_RINGING --es id t4   # 受信機は directBootAware でないので解除前は届かない見込み。届かなければ ↓
adb shell am force-stop $APP                                          # 予約も消える。force_volume の音量は戻らないので ↓ で確かめる
adb shell dumpsys audio | grep -A8 '^- STREAM_ALARM:'                  # 控えと違えば: adb shell cmd media_session volume --stream 4 --set <控えの値>
```

**端末の最終状態（12:09）**: このアプリの予約 0（`dumpsys alarm`・`alarm.list` が空）・鳴動なし（`get_ringing` が null）・`am force-stop` 済みでプロセスなし・
`deviceidle unforce`（`mForceIdle=false`・ACTIVE）・`battery reset`（USB 給電の実際の値）・STREAM_ALARM は試験の前と同じ（speaker 5）・
スパイクのアプリは削除済み・画面は消灯しロック中（`Dozing`・`isKeyguardShowing=true`）。端末には W1-4b の受信機を足したデバッグ版の APK（11:59 に入れたもの）が残っている。

#### W1-7 の実機の結果（2026-09-27 14:26〜。Pixel 6a。T4 の再起動で Android 16 → **Android 17** に更新された）

W1-8 の APK で T4 を行い、見つけた不具合を直した APK で残りを行った。予約はすべて最小の音（`force_volume` 0・振動なし・漸増 60 秒・安全弁 1 分）で、
鳴動は 20 秒以内に止めた（T5 は殺してから見張りが戻すまでの無音を含む）。試験の前後で STREAM_ALARM を比べた（基準 speaker 5）。スクリプトは
`runtime/android/tools/platform_device_tests/`（README）、証拠はリポジトリの外（`C:\Users\k023g\.claude\jobs\434062fd\tmp\wop_w1_7\results\`）。
エミュレータでは確かめていない（起動しない条件）。

| 試験 | 結果 | 数値・観察 | 証拠（`results/` の下） |
|---|---|---|---|
| T4 再起動と Direct Boot（AC-4） | **満たした** | 14:28:10 に `adb reboot`（予定の 235 s 前）→ `boot_progress_enable_screen` +82 s → 14:30:02 `RUNNING_LOCKED`・`LOCKED_BOOT_COMPLETED` → `BootReceiver` が張り直し（`alarms.rescheduled(boot)` count 1）→ 14:32:07.063 に発火（予定から +26 ms）→ 前景サービス `Allowed`（`uidState: RCVR`・`ALARM_MANAGER_ALARM_CLOCK`）+30 ms・音量 5 → 1 +65 ms・前景サービスの開始 +69 ms・通知 +72 ms・フルスクリーン通知の `START … PlatformEntry` は `result code=-92`（`MainActivity` は directBootAware でないので画面は出ない。メインプロセスも起きなかった）→ 利用者の耳で最小の音を確認・`AppOps` の行が約 2.44 s ごと（ループの周期）→ 14:33:07.124 に安全弁で停止（`ring_stopped(timeout)`）・音量 5 に戻った。**ロック解除まで adb が `unauthorized`** だったので、Direct Boot の間は見られず、利用者が 14:34:58 に解除した後に端末保護ストレージの `journal.json`（seq 19〜23）と logcat のバッファ（events・system は残り、main は巻き戻っていた）で確かめた。起動の直後は端末の時計が約 75 s 進んでいて `TIME_SET` で戻った（`rescheduled(time_changed)` が戻る前と後の 2 件）。解除後の `BOOT_COMPLETED` は控えが空で記録なし | `t4_reboot/`（`result.env`・`0_*`）・`t4_reboot/post_unlock/`（`journal.json`・`logcat_dump_all.txt`） |
| T5 `:seed_platform` を `kill -9`（AC-3。直した後。2 回） | **戻った** | 鳴り始め +5 s に kill → 見張りの予約が 19.8 s / 15.3 s 後に発火（`RingWatchdogReceiver` の `am_proc_start`）→ 照合は「見張りの予約があるので任せます」→ 「鳴動を戻しました（鳴らし直し 1 回目）」→ 前景サービス `Allowed`（`ALARM_MANAGER_WHILE_IDLE`）→ 音量を控えの元の値 5 から 1 へ → 音が戻る（無音 20.1 s / 15.4 s）→ `STOP_RINGING` で `stopped:true`・音量 5・`ringing.json` と見張りが消えた。見張りの `dumpsys alarm`: `ELAPSED_WAKEUP`・`exactAllowReason=policy_permission`・`idle-options` の `temporaryAppAllowlistType=0`・`Duration=10000`・`ReasonCode=302` | `t5_kill_run1/`・`t5_kill_run2/`（`timeline.txt`・`1_watchdog_alarm_before_kill.txt`・`*_ringing_json_*.txt`） |
| T5 強制停止（直した後） | **ring_stopped(error)・音量は前面で戻る** | 強制停止で見張りも消える → 音量 1 のまま → デバッグ受信機で背面から起こすと `ring_stopped(error)` を記録、音量の戻しは **AudioHardening に無視**（`volume control for api 100 ignored … level: partial`）→ 控えを残す → MainActivity を開くと（`LeftoverVolumeNudge`）約 0.1 s で 5 に戻り控えが消えた。直す前の回（`t5_forcestop`）は控えを消してしまい、音量が約 78 s の間 1 のままだった（`cmd audio set-volume 4 5` で戻した。`cmd media_session volume --set` は効かなかった）。停止状態から出たときの起動の放送 4 回は 1 回目だけ張り直し、残り 3 回は記録なし | `t5_forcestop/`・`t5_forcestop_retry2/`（`timeline.txt`） |
| T7 ループの継ぎ目（G-3） | **途切れ 0** | 連鎖（`setNextMediaPlayer`）で 12.8 s 鳴らして、AudioFlinger のトラックの記録は `add`（A）と止めたときの `remove`（T）の 2 行だけ（2 回）。直す前（T4 の鳴動・`setLooping`）は 21 s の間に `remove … I` → `add … A` が 8 回（59〜68 ms・中央値 66 ms） | `t7_loop_seam_chain/`・`t7_loop_seam_chain2/`（`track_log.txt`・`audio_flinger_after_stop.txt`） |
| T8 エンジンを落とす（AC-9） | **鳴り続けた** | MainActivity（エンジン）を起こしてから予約 → 鳴り始め +3 s にメインプロセスを `kill -9` → `:seed_platform` は同じ pid・`USAGE_ALARM` の再生は 4 s 後・8 s 後とも続いた → `STOP_RINGING`（冷: 新しいメインプロセス）で止まり音量 5 | `t8_engine_kill/` |
| AC-10 SaveData の `kill -9` × 100 | **直して満たした** | W1-S の書き出し: 100 回中 1 回（74 回目）に save.json が無い状態（`.bak` と完全な `.tmp`）→ 次の起動は `Backup`。hard link の方式: Android の SELinux が `link` を拒み（`avc: denied { link }`）全部 rename へ戻った（この回は隙間に当たらず 0 件）。複製の方式（今の実装）: **save.json が無い瞬間 0 回・`RecoveredFrom` は 100 回とも None・起動の回数は減らない**（Save の途中で殺せた回 6・うち .bak.new が残った回 4）。壊した save.json → `Backup`・`.corrupt-…`。2.4 MB の Save は約 230 → 290 ms | `ac10_save_kill/`・`ac10_save_kill_hardlink/`・`ac10_save_kill_copy/`（`runs.tsv`・`corrupt_restart.txt`） |
| AC-6 偽装（デバッグ版） | **Alarm にならない** | `am start -n …/MainActivity --es com.seedengine.runtime.platform.extra.LAUNCH '{"kind":"alarm",…}'` → 「起動理由: launcher」（2 回） | `t5_forcestop_retry/`・`t5_forcestop_retry2/`（`result.env` の `spoofed_launch_reason`） |
| デバッグ受信機の保護 | **adb からは届く** | `dumpsys package android` で DUMP は `prot=signature\|privileged\|development`・`com.android.shell` は `granted=true`。付けた後も `LIST`・`SCHEDULE`・`STOP_RINGING`・`GET_RINGING`・`CANCEL_ALL` はすべて答えた（他のアプリからの拒否は送り手が無いので未確認） | 各試験の `stream.txt` の `[debug] alarm.*` |
| T1 冷えた起動の回帰（AC-1・AC-12。直した後） | **音は満たした・画面は更新の直後だけ超えた** | 1 回目（16:13 に利用者がロックした後。画面オフ〈`Dozing`〉・ロック中・両プロセスなし）: 配信 +221 ms・`startForeground` +263 ms・**音**（AudioFlinger のトラックの開始）**+514 ms**・フルスクリーン通知 +309 ms・`onCreate`（起動理由 `alarm`）+728 ms・`Displayed` +1204 ms・**最初のフレーム +4608 ms**（`[SEED PIPELINE CACHE] 読込 656 KiB → 採用後 0 KiB`＝Android 17 への更新でキャッシュが使えず、DrawContext 3249 ms）。キャッシュを作り直して保存させた後（静かな鳴動 → エンジンの最初のフレーム → 止めてホームへ → `保存 656 KiB`）の 2 回目（画面点灯・ロック中〈画面は利用者しか消せない〉・両プロセスなし）: 配信 +193 ms・**音 +340 ms**・フルスクリーン通知 +274 ms・`onCreate` +415 ms・`Displayed` +562 ms・**最初のフレーム +1124 ms**（DrawContext 504 ms・キャッシュ 656 KiB を採用）。鳴動中は `isKeyguardShowing=true`・`mKeyguardOccluded=true`。どちらも STREAM_ALARM は 5 に戻った | `t1_regression/`・`warm_pipeline_cache/`・`t1_regression2/` |

**W1-7 で見つけたこと**（backlog の「W1-7 で見つけたこと」）: ① Android 17 の AudioHardening（X-7）は、前景サービスも見える画面も無いプロセスからの音量の変更を無視する
（鳴動中の強制停止・再起動の後、`force_volume` で下げた音量はアプリを開くか次の鳴動まで戻らない）、② 見張りが発火するまで最長 20 秒は無音、③ 再起動をまたいだ鳴動は
鳴らし直さない、④ 再起動の直後に端末の時計が約 75 s 進んでいた、⑤ Direct Boot の間は adb が使えない、⑥ Android のアプリは hard link を作れない（SELinux）、⑦ T4 の再起動で保留中の OS の更新が当たり端末が
Android 17 になった（その後の試験はすべて Android 17。W1-4b までの数値は Android 16）、⑧ OS の更新（とアプリの更新）の直後は描画のパイプラインキャッシュが
使えず、最初の鳴動の画面が約 4.6 s かかる（AC-12 の 3 s を超える。音は間に合う）、⑨ 鳴らす前の `ringing.json` への書き込みで音の開始が少し遅れた見込み
（T1 の 1 回目 +514 ms。W1-4b は +368 ms。2 回目は +340 ms で差は小さい）。

#### W1-7 の手作業の確認（2026-09-28 01:53〜02:12。Pixel 6a / Android 17。利用者の手を借りて）

W1-7 の 16:04 の APK をそのまま使った（ビルドもコードの変更もしていない）。利用者への操作は 1 回に 1 つずつ頼み、頼む前にスクリーンショットで画面を確かめた。
予約はすべて最小の音（`force_volume` 0・振動なし・漸増 60 秒・安全弁 1 分）で、確かめたらすぐ `STOP_RINGING` した（鳴っていた時間は 3〜9 s）。
スクリプトの側で `App.LaunchReason` を読むため、デバッグ版の上書き層（`files/assets/scenes/Main.scene`）に `SensorSmoke.scene` の中身（PlatformSmoke を
センサーだけの形で付けたアクタ。音・通知・権限・画面に触らない）を置き、開始シーンを差し替えた（最後に消した）。STREAM_ALARM は試験の前後とも speaker 5。
証拠はリポジトリの外（`C:\Users\k023g\.claude\jobs\434062fd\tmp\wop_w1_7\results_manual\` の `stream.txt`・`shots/`）。

| # | 状態と操作 | 結果 | 観察（予定時刻からの時間） |
|---|---|---|---|
| M1 | アプリが前面（生きている）・ロック解除中 →「開く」をタップ（AC-7） | **`notification_action` が届いた** | ヘッドアップ通知 +0.17 s → `notification_action_clicked` +2.69 s → `START … PlatformEntry … LAUNCH_SINGLE_TASK` → `起動後に届いた Intent の理由: notification_action ac7a/open` → `platform.launch` を読み込み済みのエンジンへ（捨てた記録なし） |
| M2 | 同じ → 本文をタップ（AC-7） | **`alarm` が届いた** | ヘッドアップ通知は +4.3 s で畳まれ、利用者は +6.4 s に本文をタップ（`notification_clicked`）→ `起動後に届いた Intent の理由: alarm ac7b`。`alarm` なのでロック画面の上に出す印が上がった（その後の `dumpsys window` で `mKeyguardOccluded=true`） |
| M3 | 鳴動中に `am stack remove` でタスクを消す → 通知をタップ（AC-2 の後半・AC-7） | **音は続き、冷えた起動でアプリへ戻れた** | `RingService: タスクが消されましたが鳴らし続けます` → `MainActivity.onDestroy` → メインプロセスが終わる（`USAGE_ALARM` の再生は続く）→ 利用者が「開く」をタップ（本文を頼んだがボタンだった）→ メインプロセスの起動 → `起動理由: notification_action ac2/open` → スクリプトの `App.LaunchReason` = `notification_action ac2/open（Kind=NotificationAction・予定 …）` |
| M4 | メインプロセスだけを `run-as kill -9`（`:seed_platform` は残す）→ 本文をタップ（AC-7） | **冷えた起動で `alarm` を読めた** | 発火のときエンジンは居ない（`呼び鈴 相手なし`）→ ヘッドアップ通知 → `notification_clicked` +2.66 s → メインプロセスの起動 → `起動理由: alarm ac7c` → スクリプトの `App.LaunchReason` = `alarm ac7c（Kind=Alarm・予定 …）` |
| M5 | 普通に起動したアプリを前面にしたまま、利用者が電源ボタンで消して点け直す（AC-5） | **ロック画面が出た** | `isKeyguardShowing=true`・`mKeyguardOccluded=false`・前面は `NotificationShade`（ロック画面）。スクリーンショットでも指紋のアイコンのあるロック画面 |
| M6 | ロック中（画面点灯）に 1 回目を鳴らす → 2 回目の通知をタップ（G-4 の記録） | **フルスクリーン通知で上に出て、2 回目のタップは解除を求めた** | 1 回目: `sysui_fullscreen_notification` → `PlatformEntry` へ `onNewIntent`（`alarm`・+139 ms）→ ロック画面の上（`setOccluded`）。2 回目: アプリがロック画面の上にいるのでヘッドアップ通知 → タップ（本文）で `dismissKeyguardThenExecute` → 指紋の alternate bouncer → 認証 → `START … PlatformEntry` → `alarm ac7l2`。W1-4b の T6 の「開く」と同じく、本文のタップも解除が要る |
| M7 | 通知の設定画面で「すべての通知」をオフ → 戻る（AC-8） | **Android がアプリを止め、`PermissionChanged` は届かない** | `POST_NOTIFICATIONS` が `granted=false`・直後に `Killing …:seed_platform … PermissionHelper`・`Killing …com.wakeorpay.seed … PermissionHelper`。戻るとメインプロセスが起動し直し（起動理由 `launcher`）、前回の状態が無いので比べる相手が無い。→ **2026-09-28 に修正**: 前回の状態をメインプロセスの SharedPreferences に保存し、起動し直した最初の onResume でも比べて `granted → denied` を流す（docs/android.md §25.14.5。JVM で 14 項目。**直した後の実機の確認は未実施**。手順は §25.14.7） |
| M8 | もう一度設定画面でオン → 戻る（AC-8） | **`PermissionChanged` が届いた** | 同じプロセスのまま（許可では止められない）`権限 post_notifications の状態が変わりました: denied → granted`。`POST_NOTIFICATIONS` は `granted=true`（フラグも試験の前と同じ）・アプリの通知は `importance=DEFAULT`・チャネル `seed_platform_alarm` は `mImportance=4` のまま |

**端末の最終状態（02:12）**: `am force-stop` 済みでプロセスなし・このアプリの予約 0（見張りも 0）・鳴動なし・`ringing.json` なし・STREAM_ALARM speaker 5（試験の前と同じ）・
通知は許可（オンに戻った）・上書き層のシーンは消した（`files/assets` なし）・ロック解除でホーム画面。

### 2.10 段階と見積もり

| 段階 | 内容 | 規模（段階A の 1 段階≒1〜2 時間のエージェント作業＋実機確認、を単位にした目安） |
|---|---|---|
| W1-0 | スパイク: 別プロセスの前景サービス・フルスクリーン通知から GameActivity・`ContentResolver.call`・Doze・再起動（§2.2 の 5 点）。JNI の手段（今の流儀の延長か jni クレートか）を決める。**済（2026-09-27。§2.9.1）**: 別プロセスの鳴動・最近のタスク・強制停止・プロセス間の呼び出し・前景サービスの種類を確かめ、E-01〜E-03・E-10 を決めた。**残り**: ~~Doze の再試験~~（W1-4b の T3 で済。§2.9.2）、再起動と Direct Boot の試験（W1-4b でも見送り。§2.9.2 の T4 に手順）。実 GameActivity の冷えた起動の計測は W1-4 の頭へ移した（W1-4b の T1 で済）。スパイクのアプリは 2026-09-27 に端末から削除した | 1〜2（済）＋残り 0.5 |
| W1-1 | 橋渡し: `SeedPlatform.invoke`・`nativeOnPlatformEvent`・イベントキュー・`ScriptHostApi` の新カテゴリ・C# の `SEED.Platform` の骨組み・デスクトップの模擬。**採用（E-02）**: プロセスの間は `ContentProviderClient` を持ち続けて `call`、`:seed_platform` からの知らせは Binder のコールバック（`call` の `putBinder` で登録）、JNI は `jni` クレート 0.22（Rust 側はここで初めて試すので、最初に `invoke` の往復を 1 本通す）。最初の呼び出しは `:seed_platform` の起動で約 120 ms 待つので、描画のスレッドから外す。**済（2026-09-27。実機の確認は残り。docs/android.md §25）**: JNI は 2 本＋登録 1 本（`invoke`・`nativeOnPlatformEvent`・`nativeRegisterPlatformBridge(Class)`）。jni 0.22.4 は Cargo.lock の版をそのまま使え（新しい依存なし）、受け手は `EnvUnowned`/`JClass`/`JByteArray` を引数に取って `with_env`、呼ぶ側は `attach_current_thread`（ローカルフレームと Java の例外の後始末を任せられる）で、Android 向けのコンパイルは最初の 1 回で通った。分かったこと: ① client は **unstable** で取る（安定な取得は `:seed_platform` の死でゲームのプロセスまで片付けられる。AOSP の説明）。② 「最初の呼び出しを描画のスレッドから外す」は、つながっていない呼び出しが背面で接続を始めて**すぐ `connecting` で失敗**し、つながったら `platform.connected` のイベントを送る形にした（スクリプトは待って呼び直す）。③ 知らせは「呼び鈴（oneway・未読あり）→ メインが `platform.poll_events` で取り出す」にし、記録を正本にした（取りこぼしても次の呼び鈴か接続で取れる）。④ スパイクには `putBinder` のコールバックの実装が無かった（W1-0 は放送で測った）ので、ここで新しく書いた。PC の模擬で ping 0.04〜0.11 ms（初回 15 ms は JIT）、試験イベントが次のフレームでスクリプトへ届いた。**実機の往復・呼び鈴は未確認**（端末が USB に無かった） | 2 |
| W1-2 | 機能の opt-in: `android.features` など新キー → SeedAndroid → Gradle → マニフェストの断片、`appCategory`・`system_bars`、Play の要件チェック。断片の差し込み方（AGP の variant API か library module か）をここで確かめる（W1-0 では試していない）。**済（2026-09-27。docs/android.md §25.10）**: 新キー 4 つ（`features`・`deep_links`・`system_bars`・`app_category`）をエディタのモデルとプロジェクト設定ウィンドウ（機能のチェックボックス・コンボ・ディープリンクの一覧。判断は WPF 非依存の `AndroidPlatformSettingsEditor`）に足し、SeedAndroid が APK の工程の Gradle の前に `runtime/android/app/src/seedFeatures/`（断片のマニフェストと `res/values/seed_platform.xml`。追跡しない・空でも必ず書く）を生成する。機能 → 権限の対応は機能の表 `runtime/android/platform_features.json`（データ。W1-3・W1-4 は行を足すだけ）。差し込み方は **AGP の variant API**（E-04）。`appCategory` は `-Pseed.appCategory`。システムバーは生成した bool を `SystemBarsController` が読む。Play の要件チェックに機能と権限の一致・権限ごとの申告の注意を足した。Wake or Pay の実ビルドを aapt2 で確かめた（権限 9・appCategory=7・intent-filter・bool=true、features を空にした写しでは権限なし・game・false）。**実機は未確認**（端末が USB に無かった）。`PlatformProvider` は main に常設のまま | 1〜2（済） |
| W1-3 | 目覚まし: `AlarmStore`・`AlarmScheduler`（setAlarmClock）・`AlarmReceiver`・`BootReceiver`（時刻・タイムゾーン・更新・権限の変化）・音源の書き出し。`BootReceiver` は**強制停止からの復帰も兼ねる**（Android 15+ は停止状態から出たときに `BOOT_COMPLETED` を届ける。実機では `LOCKED_BOOT_COMPLETED` も届いた）。張り直しは必ず `setAlarmClock`（`BOOT_COMPLETED` から直接鳴らさない。E-10）。**済（2026-09-27。実機の確認は残り。docs/android.md §25.11）**: `:seed_platform` の `platform/service/alarm/`（`AlarmModule` の `alarm.schedule`／`cancel`／`cancel_all`／`list`／`can_schedule_exact`、控え `AlarmStore`〈端末保護ストレージの `seed_platform/alarms.json`・一時ファイル → fsync → rename → フォルダの fsync〉、`AlarmScheduler`〈要求コードは定数・予約ごとの区別は data の URI `seedalarm://alarm/<ID>`・extras の予定時刻で古い配信を捨てる〉、組み合わせの `AlarmBook`、振り分けの `AlarmRearmPlan`、`AlarmReceiver`〈発火 → `platform.alarm.fired` を記録 → 控えから消す〉、`BootReceiver`〈6 つの放送。まだ先は張り直し・過ぎたものは鳴らさずに `platform.alarm.missed`、控えがあれば `platform.alarms.rescheduled`〉）。受信機はどちらも exported=false・`:seed_platform`・directBootAware で、機能の表の `alarm` に足した。記録（`EventJournal`）も端末保護ストレージへ永続化し、エンジンが居ない間の記録が次の接続で届くようにした（backlog の W1-1 の割り切りの半分）。音源は、メインプロセスの糊が初回に `platform.paths` で書き出し先を聞き、エンジンの `sound_export` が `sounds/<FNV-1a 64bit>.<拡張子>` へ書いて `sound_path` にする。デスクトップの模擬は壁時計で `platform.alarm.fired` を流し、Play の区切りで予約を消す。C# は `Alarms`・`AlarmRequest`・`ScheduledAlarm`・`AlarmFiredEvent`・`AlarmMissedEvent`・`AlarmsRescheduledEvent`（`CancelAll` は §2.3 の案の void ではなく bool を返す）。PC の Play で 5 秒後の予約が 11 ms 遅れで届き控えが空になった。W1-0 のスパイクの控えが使っていた `InputStream.readAllBytes` は API 33 からで minSdk 29 では落ちるため `Files.readAllBytes` にした。**W1-4 へ持ち越す**: `RingService` の起動（`AlarmReceiver.onReceive` の先頭の印）・showIntent を `PlatformEntry` 行きに・ロック解除の前に鳴ったときの扱い（W1-9 と）・既定の音・記録の ack と onResume の取り直し。Android 10〜14 の強制停止の後の保険（起動のたびの張り直し）は backlog | 2（済） |
| W1-4 | **頭で、実 GameActivity をフルスクリーン通知から冷えた状態で出す計測**（AC-12・X-1。W1-0 から移した）。鳴動: `RingService`（音・バイブ・WakeLock・フォーカス喪失で止めない・安全弁）・フルスクリーン通知・`PlatformEntry`・起動理由・onCreate での showWhenLocked。W1-0 からの見直し: `AlarmReceiver` は真っ先に `startForegroundService`（一時許可は 10 秒）し、**控えの fsync はその後**。**音の開始を早める**（冷えたプロセスでは `startForeground` から音まで約 340 ms。`MediaPlayer` の準備を前倒し）。端末の使用中はヘッドアップ通知になるので、**本文のタップから鳴動画面へ**行けるようにする。音は `USAGE_ALARM` 固定（X-7）。**4a 済（2026-09-27。実機の計測を除く部分。docs/android.md §25.12）・4b 済（2026-09-27。実機の計測。§2.9.2。残件は W1-7）**: 4a は `platform/service/alarm/ring/`（`RingService`〈mediaPlayback・`:seed_platform`・directBootAware〉・状態の正本 `RingRegistry`〈static。モジュールとサービスは同じプロセスなので bindService を使わない〉・`RingAudio`〈USAGE_ALARM 固定・ループ・漸増・専用のスレッドで準備して音を前倒し〉・`AlarmStreamVolume`〈force/keep_volume〉・`RingVibration`・`RingNotification`〈チャネル `seed_platform_alarm`・フルスクリーン通知・本文のタップ・「開く」〉・`RingWakeLock`）、`AlarmReceiver` の先頭の `startForegroundService`（例外は `alarm.missed(start_failed)`）と「鳴動へ渡す → `fired` を記録」の順、待ち行列（`alarm.queued`）・安全弁（`ring_stopped(timeout)`）・`alarm.get_ringing` / `stop_ringing`、既定の音（`res/raw/seed_alarm_default.wav`）、main に常設の `activity-alias PlatformEntry`（exported=false。**GameActivity が起動の部品の meta-data から lib_name を読むので別名にも置いた**）と起動理由（`LaunchReason`。onCreate の super の前・onNewIntent で `platform.launch`。最近のタスクからの開き直しは launcher）、メインプロセスで答える命令（`platform.launch_reason`・`window.set_show_when_locked`）、`:seed_platform` の起動時の照合（`AlarmStartup`。W1-3 の持ち越し）、デスクトップの模擬、C# の `Alarms.GetRinging` / `StopRinging`・`App.LaunchReason`・`Window.SetShowWhenLocked`。`launchMode` は既に `singleTask` で変えていない。4b: 実 GameActivity の冷えた起動の計測（AC-12）・最近のタスクから消しても鳴る（AC-2）・ヘッドアップ通知から・`START_NOT_STICKY` の振る舞いを実機で。**4b の結果（§2.9.2）**: 画面オフ・ロック中の冷えた起動で音 +0.26 s・エンジンの最初のフレーム +2.03 s（中央値。AC-1・AC-12 を満たした）、`force-idle` の下でも音 +0.54 s・最初のフレーム +2.13 s、タスクを消しても音は続いた（AC-2）、`:seed_platform` を殺すと黙って止まり作り直されない（直さず、設計案を §2.9.2 に）、別名の `onNewIntent` は既存の MainActivity に届き Activity は積まれない（利用者の「開く」のタップで確かめた）・adb からの別名の起動は拒否。再起動の試験は見送った。**残件（W1-7）**: 再起動と Direct Boot・`:seed_platform` の死からの復帰と音量の戻し・ループの継ぎ目・前面のときのヘッドアップ通知と「開く」の解除・戻る／ホーム／スワイプ・安全弁の実機・60 分 | 2〜3（4a・4b 済） |
| W1-5 | 通知と権限: チャネル・常駐とボタン・トランポリン無しの起動・実行時権限と結果・特別なアクセスの状態と設定画面。**済（2026-09-27。実機の確認は残り。docs/android.md §25.13・§25.14）**: 通知は `:seed_platform` の `service/notification/`（`NotificationModule` の `notification.ensure_channel`／`show`／`cancel`／`are_enabled`。文字列の ID をそのまま `notify(tag, 7300, n)` の tag にし int の ID の表を持たない。`BigTextStyle`・常駐・種類〈alarm / reminder / status / event / progress → `CATEGORY_*`〉・ボタン最大 3。本文のタップ〈`notification_tap`〉とボタン〈`notification_action`〉は W1-4a の `PlatformEntryIntents` で Activity を直接開く＝トランポリン無し。通知が無効なら `notifications_disabled`、チャネルが無ければ `channel_not_found`）。PendingIntent の同一性は、要求コードを用途ごとの定数（5・6）のまま **`Intent.setIdentifier`（API 29 = minSdk）に「用途・通知の ID・ボタンの ID」を長さ付きで並べる**形にした（ハッシュの要求コードは衝突しうる。identifier が `filterEquals` と `PendingIntentRecord.Key` に入ることは AOSP のソースで確かめた）。機能 `notifications` は権限だけで、Java は `POST_NOTIFICATIONS` の宣言の有無（`DeclaredPermissions`）で判定する（`alarm` だけの APK でも通知は使える）。権限はメインプロセスの `local/`（`permission.check`／`request`／`open_settings`）と `platform/permission/`（判定表・「はっきり拒否された」の覚え〈rationale だけでは永続の拒否と「一度も求めていない・画面を外側で閉じた」を見分けられないため〉・`requestPermissions` と `onRequestPermissionsResult`・設定の画面と onResume での結果・onResume ごとの `permission_changed`）。`MainActivity` に `onResume` と `onRequestPermissionsResult` の受け口。デスクトップの模擬（通知はログと一覧、権限は常に許可・`request` はすぐ結果）、C# の `Notifications`・`NotificationRequest`・`NotificationAction`・`NotificationImportance`・`Permissions`・`PermissionKind`・`PermissionStatus`・`PermissionResultEvent`・`PermissionChangedEvent`（`EnsureChannel`・`Cancel`・`OpenSettings` は案の void ではなく bool、列挙の先頭に `Unknown`）。PC の Play で通知の表示と 3 秒後の消去・権限の要求と結果を確かめた。**実機は未確認**（端末が USB に無かった） | 2（済） |
| W1-6 | 画面とアプリ: `Window.*`（動的な showWhenLocked・画面を点けたまま・システムバー）・`MoveTaskToBack`・`OpenUrl`・`Haptics`・ディープリンク。**済（2026-09-27。実機の確認は残り。docs/android.md §25.15）**: メインプロセスの `local/` に 7 命令（`window.set_keep_screen_on` / `set_system_bars_visible`・`app.move_task_to_back` / `open_url` / `open_app_settings`・`haptics.tap` / `vibrate`）と共通の形 `WindowToggleCommand`（W1-4a の `set_show_when_locked` も乗せ替え）。システムバーは `SystemBarsController` に「今の出し方」を持たせて `MainActivity`（`window/SystemBarsHost`）から切り替え、フォーカスが戻ったときの隠し直しも今の状態に従う。安全領域は WindowInsets → `ScreenReporter` の既存の経路で追従する（GameActivity の insets の受け口を `javap` で読んで確かめた。直す箇所は無かった）。`OpenUrl` は URL の規則（Java の `UrlPolicy` と Rust の `url_rules.rs`。file / content / javascript を断る）→ アプリの Context の `startActivity`（ACTION_VIEW・NEW_TASK・エンジンのスレッドで同期）→ `ActivityNotFoundException` で `no_handler`。**`resolveActivity` と `<queries>` は使わない**（公式の「startActivity はパッケージの可視性を必要としない」。resolveActivity は可視性で絞られ独自の scheme で誤って no_handler になりうる）。`Haptics` は `EFFECT_CLICK`（USAGE_TOUCH）と `createOneShot`（USAGE_MEDIA。API 33+）で、振動子は W1-4a の `RingVibration` と共通の `DeviceVibrator`。**`VIBRATE` を main に常設**し機能 `alarm` から外した。ディープリンクは `LaunchReason` が PlatformEntry 以外の VIEW＋data を `deep_link`・`uri` にする（起動理由の JSON・C# の `LaunchInfo.Uri`）。忘れ対策: ランチャー・最近のタスクからの開き直し（onNewIntent の launcher）で showWhenLocked・turnScreenOn を下ろす。デスクトップの模擬は画面・触感を記録、`OpenUrl` は同じ規則で判定して http / https / mailto だけを PC の既定のアプリで開く（`ShellExecuteW`。`cmd /c start` はシェルの解釈で危ないので使わない。`SEED_PLATFORM_SIM_NO_OPEN=1` なら開かない）、単体起動の `--deep-link=<URI>` で `deep_link`。Rust の単体テスト 100 件・JVM で URL の規則 29 件・`AndroidPipelineTests` 161 件・PC の Play（W1-6 の確かめと W1-3〜W1-5 の確かめがすべて OK）・APK の aapt2（`VIBRATE` が features が空の APK にも main から入る・`deep_links` の intent-filter）と dexdump を確かめた。**実機（Pixel 6a）でも確かめた**: バーの出し入れ（`InsetsSource statusBars visible=false/true`・窓の `FULLSCREEN`・`KEEP_SCREEN_ON` の出し入れ）、`OpenUrl` の断る URL と `no_handler`、触感は `usage TOUCH / MEDIA` で振動子に届いた（端末の設定で触感が切られていて `ignored_for_settings`）、ディープリンク（起動中の `platform.launch`・冷えた起動の `deep_link`）、ディープリンクで呼んだ `MoveTaskToBack`（タスクが残り前面がランチャー）と開き直しの忘れ対策。安全領域はこの端末では切り欠きとステータスバーが同じ高さで値が変わらず、追従は未確認。バーの文字色は持ち越し（backlog） | 1（済） |
| W1-S | 保存の耐久性（§2.7）。**済（2026-09-27。§2.7 の「実装」の表。W1-7 で手順 2 を「本体の写し〈Android は複製〉を .bak.new → .bak」に変えた）**: 書き出しは .tmp → sync → .bak へ rename → 本体へ rename → フォルダの sync（Android）、読み込みは本体 → .bak → 空、壊れた本体は `.corrupt-<時刻>` に 1 つ、`SaveData.RecoveredFrom`（None / Backup / Lost）、`SaveData.Batch`、文字列を渡す FFI の共通の入れ物 `Utf8Arg`（1 KB 超は ArrayPool）。単体テスト 55 件・PC の Play の SaveSmoke・Android のビルドまで。実機の `kill -9` の繰り返し（AC-10）は W1-7 | 1 |
| W1-7 | 通しの確認（AC-1〜14）・docs・backlog。**済（2026-09-27。§2.8 の表・§2.9.2 の「W1-7 の実機の結果」・docs/android.md §25.12.11）**: 再起動と Direct Boot の試験（T4。解除前に張り直して鳴る）、見つけた不具合の修正（`:seed_platform` の死からの鳴動の復元〈見張りの予約・`ringing.json`〉・`force_volume` の音量の控えと戻し〈Android 17 の AudioHardening で背面から戻せないときはアプリが前面に出たとき〉・継ぎ目の無いループ〈`setNextMediaPlayer`〉・起動の放送の重なり〈`BOOT_COUNT`〉・デバッグ受信機の保護〈DUMP〉・SaveData の書き出しで本体が消える隙間〈本体の写しで .bak を作る〉）、AC の通し確認、実機の試験のスクリプトの整理（`runtime/android/tools/platform_device_tests/`） | 2 |
| （任意）W1-8 | センサー（重力を除いた加速度）。アプリの「振る」を v1 に残すなら（U-04 で残すと決定）。**済（2026-09-27。実機の確認は残り。docs/android.md §25.16）**: メインプロセスの `platform/sensor/`（`SensorFeeds`〈窓口・センサーのスレッド SEEDSensor・前面か〉・`SensorFeed`〈出どころの選択・`registerListener`・止める・読む・前面の出入り〉・`SensorKind`／`SensorSource`〈`TYPE_LINEAR_ACCELERATION` → 無ければ `TYPE_ACCELEROMETER`＋`GravityFilter`〈一次の低域通過・時定数 0.25 秒・最初の標本で見積もりを始めて偽の振りを出さない〉→ 無ければ `not_supported`〉・`FeedListener`・`SampleAccumulator`〈最新・前回の read からの最大の大きさと数・世代〉）と `local/` の 3 命令（`sensor.start{kind, rate_hz}`〈既定 50 Hz＝SENSOR_DELAY_GAME・上限 200〉・`stop{kind}`・`read{kind}` → `{x, y, z, timestamp_ms, peak_magnitude, sample_count}`）。`sensor` を「メインプロセスだけのモジュール」にし、表に無い `sensor.*`（模擬だけの `sim_inject`）は `:seed_platform` を起こさず `unknown_method`。onPause で登録を外し onResume で登録し直す（`MainActivity`）。`SensorEvent.timestamp`（elapsedRealtimeNanos の時計）は read で UTC の epoch ミリ秒へ換算。main のマニフェストに `uses-feature android.hardware.sensor.accelerometer required=false`（権限なし）。デスクトップの模擬は値 0・`sim_inject` で標本を入れる・Play の区切りで止まる。C# は `Sensors`・`SensorKind`・`SensorSample`（`GetSource`・`SimulateSample` を足した）。名前は `wire::sensor` と `PlatformContract` に置き突き合わせ（文字列 21 組・数 3 組）。Rust の単体テスト 110 件（新規 10）・javac -Xlint:all（98 ファイル。新しい注意なし）・JVM で `SampleAccumulator`／`GravityFilter` 11 項目・PC の Play（`PlatformSmoke`／センサーだけの `SensorSmoke`／模擬の振りの `ShakeSmoke`）・APK の aapt2（`uses-feature-not-required`）と dexdump を確かめた。**実機は未確認**: 端末がロック・Dozing のままで、起動したエンジンが最初のフレームの前に suspended になりスクリプトが走らなかった（`dumpsys sensorservice` に登録なし）。静置と振るの確認は利用者が戻ってから（§25.16.7） | 0.5〜1（済） |
| （任意）W1-9 | Direct Boot（再起動後・ロック解除前の鳴動） | 1〜2 |

**合計の目安: 15〜20 段階相当 ≒ エージェント作業で 3〜4 日＋実機での確認の待ち時間。** 根拠: Android 対応の段階0〜D-4（2026-09-24 17:00 〜 09-26 07:30、コミット 15 本前後）は、1 段階が 1〜2 時間の作業＋実機確認で進んだ（`git log`）。W1 は 1 段階の中身がそれと同程度（Java＋Rust＋C#＋docs＋実機）で、ただし**実時間を待つ試験**（Doze・再起動・60 分）と**別プロセスという初めての構成**の分だけ上振れを見込む。W1-0 は 2026-09-27 に済み（残り 0.5 段階）、別プロセスの構成が成り立つことは確かめた。

## 3. W2: UI 部品群

### 3.1 いまあるもの・無いもの（2026-09-27 に W0 でコードを読んで確かめた）

| 部品・機能 | 状態 | 根拠 |
|---|---|---|
| レイアウト（縦・横・グリッドの自動配置） | **無し**。`CanvasTransform` の位置とアンカーを 1 つずつ指定するだけ → **W2-1b で `CanvasStack`・`CanvasWrap`・`CanvasGrid`・`CanvasLayoutItem`**（`canvas_layout/containers/`） | `runtime/src/engine/components/canvas_transform.rs` |
| アンカーの伝わり方 | アンカーは親の `CanvasComponent` の領域に対する比率。**`CanvasComponent` を持たない親の子ではアンカーが効かない**。2D ノードのレイアウト計算が 5 か所に重複している | `app/canvas_collect.rs:62,106,126`、backlog「2D ノードのレイアウト計算が 5 か所に重複コピーされている」 |
| 解像度への追従 | ルートキャンバスの `auto_scale`（基準 1920×1080）。**縦画面では縦横別々の倍率が掛かり形が崩れる**。基準より大きいキャンバスは縮小しない | `canvas_component.rs:130-265`、backlog（Android 節「縦画面でキャンバスの自動スケールが縦横別々に掛かる」・「キャンバスの `auto_scale` が…縮小しない」） |
| DPI・dp | 換算は無い（`Screen.DPI` を返すだけ）→ **W2-1b でルートキャンバスの単位 dp**（`CanvasComponent.unit`。1 dp = 表示倍率の画素） | `Screen.cs:116-127`・`canvas_layout/units.rs` |
| 安全領域 | 値は取れる（`Screen.SafeArea`）。**キャンバスへ自動では効かない** → **W2-1b で `CanvasSafeAreaComponent`**（辺ごとに縮める。回転・システムバーに追従） | scripting_api §7.12、backlog「キャンバス UI へ安全領域を自動で反映する仕組みが無い」 |
| クリップ（はみ出しの切り取り・シザー） | **無し**（Draw・スプライト・テキストのどれにも）→ **W2-1a で矩形の切り抜きを本番化**（`CanvasClipComponent`。スプライト・テキスト・2D パーティクル・Draw の図形・当たり判定） | `primitive2d/`・`ui_draw_pass.rs`・`canvas_layout/clip.rs` |
| スクロール・慣性 | **無し**（前提のクリップも無い）→ **W2-3 で `CanvasScrollComponent`**（慣性・跳ね返り・スナップ・入れ子・ScrollTo・見える範囲の外を飛ばす）と `SEED.UI.ListView`（行の再利用）・`SwipeActions`（スワイプの操作） | `engine/core/canvas_scroll/`・docs/ui_scroll_list.md |
| 当たり判定 | Sprite / SkinnedSprite の `raycast_target` だけ（**Text は対象外**）。最前面の 1 つに `OnPointer*`。**指0 の 1 本だけ**。押下の取り消し（スクロールが始まったら押下をやめる）は無い → **W2-2 で CanvasGesture を付けたノードは指ごと・押下の取り消しつき・最小 48 dp**（`OnPointer*` は従来のまま） | `pick_2d.rs:46-49,91-96`、`pointer_events.rs`、backlog「キャンバス UI のポインタイベントは指0 の 1 本だけ」 |
| ジェスチャー | **無し**（長押し・フリック・ピンチ・ダブルタップ）。タッチに時刻・押下時間・圧力が無い → **W2-2 でジェスチャーアリーナ**（`CanvasGestureComponent`。タップ・長押し・ドラッグ・フリック・押下の取り消し・指ごとの捕捉・複数指。時刻つきの指の記録。ピンチ・ダブルタップは無い） | backlog「ジェスチャ…の組み込み API が無い」・`input/gesture/`・docs/input_gestures.md |
| 文字入力・IME | **無し**（Windows も Android も）。winit の `WindowEvent::Ime` / 文字の受け取りを配線していない。Android は GameActivity の IME 処理を邪魔しないだけで、確定文字列を受け取る経路が無い | `core/input/mod.rs`（処理する `WindowEvent` に Ime が無い）、`MainActivity.java:139` |
| フォーカス・クリップボード | **無し** | grep |
| 文字 | SDF（任意サイズ）、フォント指定（組み込みは **M PLUS Rounded 1c Regular**。Flutter 版と同じ書体）、枠と折り返し（日本語は 1 文字単位＋簡易禁則）、太さ・影・縁取り、インライン画像、スロット。**スクリプトから寸法を測れない**。グリフのアトラスは 4096² で約 2,500 字、**あふれると追い出さずに描かない** | `Text.cs`、`font/atlas.rs:17-19,183-194`、`font/mod.rs:59-74` |
| 図形 | 角丸矩形・線・円弧・リング・折れ線・多角形・ベジエ。**グラデーション・9 スライス・画像の描画（Draw から）は無い**。1 フレーム 4,096 図形 → **W2-4 でスプライトの形と塗り**（角丸・楕円・弧・縁・グラデーション・9 スライス・影。`Sprite.Shape` など。Draw は変えていない） | `Draw.cs`、`batch2d.rs:19`、docs/ui_components.md |
| 描画の順と回数 | ゾーン → レイヤー → 種別（スプライト → 図形 → パーティクル → テキスト）。同じ種別が続く区間を 1 回の描画にまとめる（種別を交互に重ねると回数が増える） | `ui_draw_order.rs:24-156` |
| 描画のコスト | **UI と提示は描画スケールに関係なく画面の解像度で描く**（Pixel 6a の mobile で GPU 10.5 ms のうち約 4.5 ms が UI・提示などの固定分） | `renderer/mod.rs:353-357`、backlog「固定分 約 4.5 ms」 |
| 描き続け | 前面では毎フレーム描く（`target_fps` 既定 60）。背面では止まる | project_settings、android.md §14 |
| プレハブ | `.actor` から `GameObject.Instantiate` できる（スクリプトから生成したものはプレハブのリンク `prefab_source` を持たない） | `script_scene_ops.rs:100-141`、`scene.rs:777` |
| データファイル | `Assets.ReadText` で JSON 等を読める（型への自動変換は無い。`System.Text.Json` などでスクリプトが解釈） | `Assets.cs` |
| テーマに使えそうな仕組み | `AudioDictionary`（キー → 値の表をコンポーネントとして持ち、インスペクタで編集）と同じ形の辞書コンポーネント | `AudioDictionary.cs` |
| 時間待ち | コルーチンは無い（`Update` で経過時間を数える） | scripting_api §9 |

→ **W2 はほぼ新規**。土台として使えるのは、キャンバスの座標系・スプライト・テキスト・図形・`OnPointer*`・タッチの状態・`Screen`・プレハブの生成・データファイルの読み込み。

### 3.2 設計の柱

| # | 柱 | 内容 |
|---|---|---|
| W2-P1 | **ECS で分ける** | 状態はコンポーネント（データ）、振る舞いはシステム。**レイアウト・クリップ・当たり判定・ジェスチャーの調停・文字入力（IME）・描画の要否は Rust のシステム**（性能とエディタでの編集のため）。**部品の振る舞い（ボタンの状態・スライダ・スクロールの物理・一覧の再利用・ホイール・タブ・シート・グラフ）は C# の `SEED.UI` 名前空間**（SEEDScripting に同梱。エンジンを更新すれば全プロジェクトに届く）。見た目はプレハブ（`templates/ui/`）とテーマのデータで差し替える。**W2-3 で、スクロールの物理（慣性・跳ね返り・スナップ・入れ子）は Rust へ寄せた**（位置がレイアウトの走査の入力で、見える範囲の外を飛ばす判定と「動いている」の申告・60 fps の慣性を FFI の往復なしで回すため。一覧の再利用・スワイプは C#。docs/ui_scroll_list.md §2） |
| W2-P2 | **dp で組む** | レイアウトの単位は dp（= px ÷ (DPI ÷ 160)。Android の reference_dpi 160 と同じ）。指で押す部品は最小 48dp。`auto_scale`（縦横別の倍率）に頼らない。デスクトップの Play では「縦長の端末を模した大きさ」のウィンドウで同じ dp になる |
| W2-P3 | **ジェスチャーの調停** | タップ・長押し（500ms）・ドラッグ（しきい値 8dp）・フリックを 1 か所（ジェスチャーアリーナ）で決める。スクロールが始まったら子の押下を**取り消す**。縦スクロールの中の横スクロール（グラフ）は、最初の動きの向きで持ち主を決める |
| W2-P4 | **戻るの段** | 戻る（Escape）は「最前面の覆い（ダイアログ → シート） → 画面のスタック → 最上位なら `Platform.App.MoveTaskToBack`」の順に 1 つだけが受ける。画面は戻るを無視することもできる（鳴動画面） |
| W2-P5 | **安全領域は部品** | `SafeAreaPanel` が中身を `Screen.SafeArea` の内側に収める（回転直後の 1 フレームのずれは次のフレームで直す）。システムバーを表示する設定（W1 の `system_bars`）と組み合わせる |
| W2-P6 | **データ駆動のテーマ** | 色・書体と太さ・文字の大きさ・角丸・余白・動きの時間を**トークン**（例 `color.surface`・`radius.island`）で持つテーマ JSON。部品はトークンを参照し、実行中に差し替えられる（アプリのテーマ交換） |
| W2-P7 | **描かなくてよいときは描かない** | 入力もアニメーションも値の変化も無いフレームは描かない（または大きく間引く）。部品は「動いている」ことをエンジンへ知らせ、時計の表示のような定期の更新は間隔を指定して描かせる。アプリは画面の大半の時間が止まっているので、電池に効く |
| W2-P8 | **文字は日本語で困らない** | グリフのアトラスが一杯になったら古いものを追い出す（または頁を増やす）。スクリプトから文字の寸法を測れる（`Text.Measure`）。太さの違う書体（400/500/700）を同時に使える |

### 3.3 部品の一覧と要件

「使う画面」は Wake or Pay の画面（アプリ仕様 §3）。汎用の部品として作り、アプリ固有の見た目はプレハブとテーマで作る。

| 部品 | 必要な振る舞い | 状態 | Wake or Pay で使う画面 |
|---|---|---|---|
| **レイアウト**（`UiRect`・`Stack`・`Wrap`・`Grid`） | 縦・横に並べる（間隔・余白・揃え）、中身に合わせる／親に合わせる、折り返し（チップ 2 段）、格子（月の 3×4、庭の 8×6）。アンカーの伝わり方を「どの親でも効く」に揃える | — | 全画面 |
| **スクロール**（`ScrollView`） | クリップ、縦／横、慣性（指を離した速度から減速）、端の跳ね返りか引き伸ばし、スクロールバー（任意）、入れ子（縦の中の横）、プログラムからのスクロール、スナップ（ホイール用） | 静止・ドラッグ中・慣性中 | 一覧・編集画面・アクティビティ・プロフィール・オプション・種屋 |
| **一覧**（`ListView`） | 行のプレハブ＋データの結び付け、画面外の行の再利用、行の高さの違い、**左スワイプで操作ボタン（削除）**、長押しのメニュー | — | アラーム一覧・連絡ログ・ペナルティ履歴のログ・種屋 |
| **ボタン** | 押した見た目・無効・長押し・触感（W1 の `Haptics`）、当たり判定を見た目より広げる（最小 48dp）、二重押しの防止（処理中は無効） | 通常・押下・無効 | 全画面 |
| **トグル（スイッチ）** | ON/OFF、つまみの動き | ON・OFF・無効 | 一覧の行・基本設定・覚悟 |
| **スライダ＋数値欄** | つまみのドラッグ、範囲・刻み、数値欄と同期（数値でない入力は無視・範囲外はクランプ） | — | 猶予・スヌーズ・ペナルティ・上限金額 |
| **選択**（ラジオ・セグメント・チップ） | 1 つ選ぶ／複数選ぶ、選べない項目（灰色） | 選択・非選択・無効 | 曜日・起床確認方法・人質・サウンド・加算モード・編集のカテゴリ |
| **時刻ホイール** | 時（0–23）と分（0–59）の 2 列、慣性とスナップ、端をつなげる（ループ）、選択中の強調、刻みの触感。**操作で画面全体を作り直さない** | — | アラーム編集 |
| **文字入力**（`TextField`） | 1 行・複数行、**日本語の変換中の文字（未確定）を表示**、数字だけのキーボード、カーソル、「完了」、キーボードが入力欄を隠さない（入力欄を上へずらす）、コピーと貼り付けを禁止できる（起床確認の文字入力） | 通常・フォーカス・無効・エラー | 起床確認の文字入力・計算、名前の変更、上限金額の最大値、（v2）連絡帳・メッセージ |
| **タブ**（`TabBar`） | 下タブ 4 つ、選択の強調、タブごとに画面の状態を保つ、同じタブを押したら先頭へスクロール | — | シェル |
| **ダイアログ** | 確認（「やめる」「上げる」など 2 択）、1 行の入力つき、背景の暗幕、戻るで閉じる | — | 削除・未保存・上限の引き上げ・名前 |
| **シート**（上から・下から） | グラブバー、フリックで閉じる、固定の頭（スクロールしない）＋中身だけスクロール、「閉じる」を固定で置く | 開く途中・開いた・閉じる途中 | プロフィール・オプション（上から） |
| **画面のスタック**（`Navigator`） | 全画面のルートの積み下ろし（入る・出る動き）、タブの外の画面（鳴動・結果・編集・模様替え・種屋）、戻るの段 | — | シェル |
| **グラフ** | 折れ線（点・平均の横線とラベル・点のタップで吹き出し・欠けた日は点を打たずに線をつなぐ）、棒（積み上げ 2 色・凡例・棒のタップで日を選ぶ・空の高さまで当たり判定）、軸（時刻 `H:mm`・日付 `M/d` の間引き）、横スクロールとピンチ（1〜6 倍） | — | アクティビティ・ペナルティ履歴・起床時間の全期間 |
| **進捗** | 輪（長押し・振る）、横棒（経験値。中に Lv の文字） | — | 鳴動画面・ヘッダー・プロフィール |
| **形と塗り** | 角丸の島（塗り＋枠）、**2 色のグラデーション**、模様（点・縞）、円形の切り抜き（アイコン）、9 スライス | — | 島・ネームプレート・背景・アイコン |
| **ドラッグ＆ドロップ** | 棚からつかむ、置ける／置けない場所の表示、戻す | — | 庭の模様替え |
| **テーマ** | トークン（色・書体・大きさ・角丸・余白・時間）、実行中の切り替え | — | テーマ交換 |
| **安全領域** | 中身を安全領域の内側へ | — | 全画面 |
| **トースト**（任意） | 短い知らせ（下から出て消える） | — | 保存・エラー |

### 3.4 既存の仕組みとの接続点

| 作るもの | つなぐ先（候補） |
|---|---|
| レイアウト・クリップ | `runtime/src/engine/components/canvas_transform.rs`・`canvas_component.rs`、`app/canvas_collect.rs`（重複している 5 か所を 1 つの関数へ寄せてから足す）、描画は `renderer/ui_draw_pass.rs`・`primitive2d/pass.rs`・`batch2d.rs`（シザー矩形を描画のランに持たせる） |
| 当たり判定・ジェスチャー | `app/pick_2d.rs`（クリップの外は当たらない・Text も対象にできる）、`app/pointer_events.rs`（指ごとの捕捉・取り消し）、`core/input/touch/`（時刻を持たせる） |
| 文字入力 | Windows: winit の `Window::set_ime_allowed`・`WindowEvent::Ime`（`core/input/mod.rs` とウィンドウの生成）。Android: android-activity の GameActivity 向けの文字入力（ソフトキーボードの表示・`TextInputState`）を `winit::platform::android::activity` 経由で使う（winit 自体は Android の IME を扱わない。**記憶に基づく・W2-0 で確かめる**） |
| 文字の寸法 | `font/text_layout.rs` の `measure_text_box` などを FFI で公開 |
| グリフの追い出し | `font/atlas.rs` |
| 描かない判断 | `app_base/app/frame_pacing.rs`（`target_fps`）とイベントループの `ControlFlow` |
| C# の部品 | `scripting/src/Api/UI/`（新設）。FFI の新カテゴリは `host_api.rs` の `ScriptHostApi` と `ScriptHost.cs` を同時に変える（`.claude/rules/scripting-api.md`） |
| プレハブ・テーマの見本 | `templates/ui/`（テンプレートライブラリ。docs/template_library.md）。プロジェクトへコピーして使う |
| 戻る | `core/input/key_remap.rs`（戻る → Escape）の上に「戻るの段」を置く |

### 3.5 受け入れ基準（W2 の完了の条件）

| # | 基準 |
|---|---|
| UC-1 | `templates/ui/` の見本シーン（全部品のギャラリー）が、デスクトップ（縦長のウィンドウ）と Pixel 6a で同じ dp の大きさ・配置で出る |
| UC-2 | 100 行の一覧を指でフリックすると慣性で流れ、端で止まる（跳ね返る）。スクロール中は Pixel 6a で 60 fps を保つ（`SEED.Time.Fps` と GPU の計測） |
| UC-3 | 一覧の行のボタンを押してからスクロールすると、ボタンは反応しない（押下が取り消される）。縦の一覧の中の横スクロールのグラフが、斜めの指の動きで取り違えない |
| UC-4 | 時刻ホイールで 23:59 → 0:00 がつながり、指を離すと最寄りの値にスナップする。値の変化で画面の他の部分が描き直されない（計測で確かめる） |
| UC-5 | 文字入力: Android のキーボードでひらがな → 漢字に変換して確定できる（変換中の文字が見える）。数字だけのキーボードが出せる。入力欄がキーボードに隠れない。Windows の IME でも同じ |
| UC-6 | 戻る: ダイアログ → シート → 画面のスタック → 最上位で背面へ、の順に 1 回ずつ効く |
| UC-7 | ステータスバーとナビゲーションバーを表示した状態で、切り欠き・ジェスチャーバーに部品が重ならない（4 方向の回転と縦固定の両方） |
| UC-8 | 押せる部品の当たり判定が 48dp 以上（ギャラリーで自動検査） |
| UC-9 | テーマの JSON を差し替えると、実行中に全部品の色・書体が変わる |
| UC-10 | 何も触らず何も動いていない画面では、描画の回数が落ちる（Pixel 6a で消費電力か GPU の稼働で確かめる）。時計の表示だけ 1 秒ごとに更新される |
| UC-11 | 常用漢字をすべて含む文を表示しても文字が欠けない（アトラスの追い出し） |
| UC-12 | `docs/scripting_api.md`（と html）に `SEED.UI` の節、`docs/` に UI の正典（部品の使い方・テーマの書式）、テンプレートの見本 |

### 3.6 検証の方法

- **ギャラリーのシーン**（`templates/ui/scenes/ui_gallery.scene`）: 全部品を並べ、状態（押下・無効・エラー）を切り替えるボタンを置く。デスクトップの Play と SeedAndroid の `run` で回す。
- **自動テスト**: レイアウトの計算（dp・安全領域・折り返し・格子）、スクロールの物理（速度からの減速・スナップ）、ジェスチャーの判定（合成したタッチ列から）、グラフの座標の計算は、**描画と切り離した純粋な関数**にして単体テストにする（Rust の `cargo test` と C# のコンソールテスト。エディタの `editor/tests/*` と同じ流儀）。
- **実機の計測**: SEED の fps 表示と GPU のタイムスタンプ（android.md §22.6）。スクロール中・静止中の両方。
- **文字入力**: 実機で日本語のキーボード（Gboard）での変換、数字キーボード、キーボードの表示・非表示での配置のずれ。
- **エディタの MCP のスクリーンショット**（PC）で見た目の差分を確かめる（docs/editor_mcp.md）。

### 3.7 段階と見積もり

| 段階 | 内容 | 規模（段階A の 1 段階≒1〜2 時間、を単位にした目安） |
|---|---|---|
| W2-0 | スパイク: Android の文字入力（GameActivity の IME を winit の外から使えるか）、クリップの描画の実装方針、描かないときのイベントループ | 1〜2 |
| W2-1 | 土台: dp・レイアウト（`Stack`・`Wrap`・`Grid`・アンカーの伝わり方の統一と重複の整理）・安全領域・クリップ・当たり判定のクリップ対応 | 3〜4 |
| W2-2 | 入力: ジェスチャーアリーナ（タップ・長押し・ドラッグ・フリック・押下の取り消し・指ごとの捕捉）、タッチの時刻 | 2 |
| W2-3 | スクロールと一覧（慣性・跳ね返り・入れ子・再利用・スワイプの操作） | 2〜3 |
| W2-4 | 基本の部品（ボタン・トグル・スライダ＋数値欄・選択・進捗・形と塗り〈グラデーション・9 スライス・円の切り抜き〉） | 2〜3 |
| W2-5 | 時刻ホイール | 1 |
| W2-6 | 文字入力と IME（Windows・Android）、文字の寸法、グリフの追い出し | 3〜4 |
| W2-7 | 画面の組み立て（タブ・画面のスタック・ダイアログ・シート・戻るの段・トースト） | 2〜3 |
| W2-8 | グラフ（折れ線・棒・軸・吹き出し・パンとズーム） | 2 |
| W2-9 | テーマ（トークン・切り替え）と `templates/ui/` の見本・ギャラリー | 1〜2 |
| W2-10 | 描かなくてよいときは描かない（イベントループ・部品からの通知） | 1〜2 |
| W2-11 | 通しの確認（UC-1〜12）・docs・backlog | 2 |

**合計の目安: 22〜32 段階相当 ≒ エージェント作業で 5〜8 日。** 根拠: 部品の数が多いうえ、**クリップ・文字入力（IME）・レイアウトの土台**という描画と入力の基盤から作る必要がある（W1 のように「Android の決まった API を包む」作業ではない）。特に IME（Android は winit の外の経路になる見込み）と、レイアウト計算の重複の整理（backlog にある 5 か所）が上振れしやすい。W3 と並べて進めるなら、W2-1〜W2-4 と W2-7 ができた時点でアプリの画面づくりを始められる（グラフ・ホイール・文字入力は後から差し替え）。
→ **W2-0 の結果で見直した段階と見積もりは §3.8.5**。

### 3.8 W2-0 スパイクの結果（2026-09-27）

W2 でいちばん不確かな 3 点（Android の文字入力・切り抜きの描画・描かないときのイベントループ）を、**本番の振る舞いを変えない試作**で試した。
試作は既定で無効で、起動の指定 `ui_spike` があるときだけ動く（PC は `SEED.exe --ui-spike=<指定>` か環境変数 `SEED_UI_SPIKE`、Android はデバッグ版の APK を
`am start … --es seed.ui_spike '<指定>'`。書式は `runtime/src/engine/core/ui_spike/config.rs` の `idle=<N>`・`wake_ms=<M>`・`clip=<ノード名>`・`ime`。
`clip=` は 2026-09-27 の W2-1a で本番の `CanvasClipComponent` に置き換えて外した。複数の根は `clip=A;clip=B` のように項目を分けて書く形だった。
`idle=`・`wake_ms=` は 2026-09-28 の W2-10a で本番の `render_policy`〈docs/redraw_policy.md〉に置き換えて外した。残るのは `ime` だけ）。
試験用のプロジェクト `UiSpike` は作業フォルダの使い捨て（WakeOrPay の `.seedproj`・`project_settings.json` を写し、アプリ ID `com.seedengine.uispike`・機能なし・
540×1200 のキャンバスに切り抜きの試験の UI。リポジトリには入れていない）。PC は Windows 11・`runtime/target/debug/SEED.exe`（RTX 3060）。
実機（Pixel 6a）は 2026-09-27 には adb が `unauthorized` のまま戻らず、**翌 2026-09-28 に、W2-0 のときに作った APK の控え（`com.seedengine.uispike`）を
入れて確かめた（§3.8.7）**。手順は §3.8.6。表の「根拠」の **実行** は動かして確かめたもの、**読んだ** はソース（`~/.cargo/registry` の android-activity 0.6.1・winit 0.30.13・
同梱の GameActivity／GameTextInput 4.4.0 の C++、Gradle のキャッシュの games-activity 4.4.0 の `javap -c`）を読んで確かめたもの。

#### 3.8.1 Android の文字入力（E-06）

| # | 試したこと | 結果 | 根拠 |
|---|---|---|---|
| I-1 | winit が文字入力のイベントをどう扱うか | **読み捨てる**。winit の `handle_input_event` は MotionEvent と KeyEvent だけを扱い、`TextEvent`（状態が変わった）と `TextAction`（完了・次へ）は `warn!("Unknown android_activity input event …")` で捨てる。android-activity はこの 2 つを glue の「フラグ 1 つ」（`textInputState`・`pendingEditorAction`）から `input_events_iter` で 1 度だけ取り出す作りなので、winit が先に取った後は SEED から見えない。状態は `text_input_state()` で後から読めるが、**アクションは取り戻せない**。winit の `warn!` は `tracing`（`Cargo.lock` で log 機能なし）なので logcat へは出ない＝入力した文字列がログへ漏れることは無い | 読んだ: winit `platform_impl/android/mod.rs` の 311-481 行、android-activity `game_activity/mod.rs` の 1068-1101 行・`android_native_app_glue.c` の 677-737 行 |
| I-2 | winit の IME の API | Android では `set_ime_allowed(true)` が `show_soft_input(true)`（SHOW_IMPLICIT）を呼ぶだけで、`set_ime_cursor_area`・`set_ime_purpose` は何もしない。`WindowEvent::Ime` は出さない。**winit の API では Android の文字入力は組めない** | 読んだ: winit `platform_impl/android/mod.rs` の 916-926 行 |
| I-3 | winit の外から、複製した `AndroidApp` で API を呼べるか | **呼べた（実機）**。`AndroidApp` は `Clone`（中身は `Arc`）で、`android_main` で複製して試作のスレッドへ渡し、元を `with_android_app` へ渡した。`set_ime_editor_info` → `show_soft_input` でキーボードが出て、`hide_soft_input` で消え、`set_text_input_state` が IME の本文を差し替えた。winit のタッチ・キーと取り合わず、描画も 59.3 fps のままだった（§3.8.7 の R-1〜R-9） | 実行（実機） |
| I-4 | android-activity の操作がどのスレッドで効くか | `show_soft_input`・`hide_soft_input`・`set_text_input_state`・`set_ime_editor_info` は GameActivity の UI スレッドの作業の列（work pipe）へ積まれ、**UI スレッドで順に**実行される（どのスレッドから呼んでもよい）。`set_ime_editor_info` は EditorInfo を書き換えるだけで、IME が読み直すのは次の `restartInput`（＝`show_soft_input` の中の `setSoftKeyboardActive(true)`）。**EditorInfo → 表示の順に呼ぶ** | 読んだ: `GameActivity.cpp` の 340-367・415-440・825-829 行、`javap`（`InputConnection.setSoftKeyboardActive`・`GameActivity.setImeEditorInfoFields`） |
| I-5 | 完了・次へのアクションの経路 | IME のアクションのボタンは `InputConnection.performEditorAction(action)` → action が 0 以外なら `GameActivity.onEditorAction(action)` → ネイティブ（I-1 で winit が捨てる）。1 行の入力でのハードウェアの Enter は `processKeyEvent` が `EditorInfo.actionId` のアクションにする。**Java の `onEditorAction` を上書きすれば確実に取れる** | 読んだ: `javap`（`InputConnection.performEditorAction`・`sendEditorAction`・`processKeyEvent`） |
| I-6 | 添字の単位 | GameTextInput は Java の `State` の添字（`String` の添字＝**UTF-16 の単位**）を変換せずにネイティブへ渡す（本文は修正 UTF-8）。android-activity の `TextSpan` の説明と `text_input_state()` の丸めはバイト数を前提にしていて食い違う。`set_text_input_state` の添字もそのまま Java へ渡る。**SEED は UTF-16 の添字として扱う** | 読んだ: `gametextinput.cpp` の 335-352 行、android-activity `game_activity/mod.rs` の 714-742 行。**実機で確認**: 「あいう」は UTF-8 9 バイトで selection 3..3・変換中 0..3（§3.8.7 の R-4） |
| I-7 | ネイティブから状態を読むときのスレッド | GameTextInput の `getState` は状態の構造体をロックの中で写すが、本文は固定長のバッファを指したままで、UI スレッドがロックの外で上書きする。**UI スレッド以外で読むと途中の本文を読みうる**。試作は `AndroidApp::run_on_java_main_thread` で UI スレッドで読んだ。本番は Java の `stateChanged` の上書きで受け取る（読む側が要らない） | 読んだ: `gametextinput.cpp` の 40-80・225-275 行 |
| I-8 | キーの取り合い | ソフトキーボードが有効な間は、描画面（SurfaceView）の `OnKeyListener`（GameTextInput の `InputConnection.onKey`）が先にキーを見て、文字・削除・左右・Home/End・1 行の Enter（アクション）の**押下**を本文へ入れる（エンジンへ届かない）。それ以外（**戻る**など）と離す操作はそのまま GameActivity → winit の `KeyboardInput` へ届く。無効な間は今と同じ | 読んだ: `javap`（`InputConnection.onKey`・`processKeyEvent`） |
| I-9 | ソフトキーボードの表示・高さ | 表示の変化（`onSoftwareKeyboardVisibilityChanged`）は glue の `APP_CMD_SOFTWARE_KB_VIS_CHANGED` になるが android-activity が捨てる（イベントにしない）。IME の範囲は GameActivity の `onImeInsetsChanged` がログを出すだけ。GameTextInput は窓を `setDecorFitsSystemWindows(false)` にする（IME で窓は縮まない）。**高さは Java の WindowInsets（`Type.ime()`）から取り、入力欄をずらすのはエンジン側** | 読んだ: android-activity `game_activity/mod.rs` の 578-584 行、`javap`（`GameActivity.onImeInsetsChanged`・`InputConnection` の構築子） |
| I-10 | 眠っているイベントループを起こすか | glue の文字入力とアクションの受け口は `notifyInput`（`ALooper_wake`）でイベントループを起こす。ただし winit は WindowEvent を出さないので、W2-10 の「描かない」ではエンジン側が「文字入力があった」を見て再描画を要求する必要がある | 読んだ: `android_native_app_glue.c` の 530-541・677-685 行 |
| I-11 | PC（Windows）の今 | winit はウィンドウを作るときに IME を切り離し（`ImmAssociateContextEx(IACE_CHILDREN)`）、`set_ime_allowed(true)` で既定の文脈へ戻す。`Ime::Enabled` は許可された窓の `WM_IME_STARTCOMPOSITION` でだけ出る。**SEED は `set_ime_allowed` を呼ばず `WindowEvent::Ime` も扱わない＝今の Play の窓では日本語の IME を使えず、`Ime::Enabled` も来ない**。候補窓の位置は `set_ime_cursor_area`（窓のクライアント座標。`CFS_EXCLUDE` で矩形を避け、変換中の文字は矩形の下）。試作の `ime` で許可と位置の指定まで動かした（落ちない・ログが出る）。日本語の実入力は行っていない | 読んだ: winit `platform_impl/windows/window.rs` の 1161 行・`ime.rs` の 115-150 行・`event_loop.rs` の 1513-1607 行、grep（SEED に `set_ime_allowed` が無い）。実行: `--ui-spike=ime` |
| I-12 | 一度も本文が入っていないときの `text_input_state()` | **実機で落ちた**（`ime` の試作の最初の起動）。GameTextInput の状態は、本文が 1 度も入るまで本文のポインタが null・長さ 0。android-activity 0.6.1 の `map_input_state_to_text_event_callback` がそれを `slice::from_raw_parts(null, 0)` に渡し、デバッグ版では Rust の前提条件の検査で panic（巻き戻せない panic で abort）、配布版では未定義動作。試作は起動の前に `clear`（`set_text_input_state("")`）を積んで避けた。winit の読み取り（文字入力のイベントが来た後の `take`）は、そのときには本文が入っているので当たらない。**本番ではネイティブから `text_input_state()` を読まない**（I-7 の決定をこの点でも裏づける） | 実行（実機の logcat の `[SEED PANIC] … android-activity-0.6.1/src/game_activity/mod.rs:717:13: unsafe precondition(s) violated: slice::from_raw_parts …`）・読んだ（同 714-742 行、`gametextinput.cpp` の `currentState_ = {}`） |

#### 3.8.2 切り抜き（クリップ）の描画

試作: 名前で指定したキャンバスノード（`clip=<名前>`。矩形はそのノードの最初の有効なスプライト）に入ったら、その矩形（ワールド座標の 4 隅）を表へ積み、
子孫のスプライト・テキスト（インライン画像を含む）へ表の番号を持たせる（`renderer/ui_clip.rs` の `UiClipCollector`）。描画順のランを番号の変わり目で分け
（`split_run_by_clip`）、ランごとに 4 隅を射影した AABB と祖先の交差を画素の矩形にして `set_scissor_rect` する（`scissor_px`。描画先の外は切り詰め、端は外側へ丸める。
描き終えたら全体へ戻す）。シェーダー・パイプライン・頂点の形は変えていない。scissor を張るのはシーンのスクリーンスペースのオーバーレイパスだけ。

| # | 試したこと | 結果・数値 | 根拠 |
|---|---|---|---|
| C-1 | 400×300 の枠（ClipBox）の中の子（左右にはみ出す 500×80 の赤・上下にはみ出す 80×420 の緑・右へはみ出す文字）と、入れ子の枠（InnerClip 200×120。自身も ClipBox の右下へはみ出す）の中の 300×60 の黄 | **画素単位で切れた**。赤 x=20〜519 → 70〜469、緑 y=140〜559 → 200〜499、黄 x=280〜539 → 320〜469（InnerClip と ClipBox の交差）。切り抜きの外の同じレイヤーの紫の帯と題の文字は切れない | 実行: PC のスクリーンショット（IPC の `SCREENSHOT:game`）を PIL で測った |
| C-2 | 描画呼び出しの増え方 | 試験のシーン（切り抜き 2 つ）で、2D のラン **3 → 8 本**（切り抜きあり 4 本。3 本は切り抜きなしのときのレイヤーと種別の並びから数えた値、8 本はログ）、スプライトの描画呼び出し **2 → 5 回**（`[PERF] sprites=6枚/2draws → 5draws`。どちらも計測）。増えるのは「レイヤーと種別で並べた列の中で、切り抜きの番号が変わる回数」だけ（同じ切り抜きの中の同じ種別は従来どおり 1 本）。`set_scissor_rect` は Vulkan の動的な状態で安い | 実行: `[SEED UI SPIKE] clip: …` のログと `[PERF]` |
| C-3 | 既定の描画が変わらないか | 指定なし（切り抜きなし）の同じシーンの画面が、変更前の `SEED.exe`（`runtime/target/develop`・2026-09-26 のビルド）と**画素一致**。切り抜きが無ければランは分かれず、scissor にも触らない | 実行: 2 つのスクリーンショットの差分 0 画素 |
| C-4 | 単体テスト | `ui_clip` 6 件（根の判定・入れ子の親・交差・NDC → 画素・外側への丸め・ランの分割）と既存の `ui_draw_order`・`canvas_collect` などが通る | 実行: `cargo test --lib`（対象を絞った 109 件） |
| C-5 | 試作の制限 | 2D パーティクル・スクリプトの図形（`SEED.Draw`）・メインパスで描くもの（背景ゾーン・エディタのビュー）・3D ワールドキャンバスは切り抜かない。回転したノードは 4 隅の AABB になる（正確には切れない）。当たり判定は切り抜きを見ない | 読んだ（試作のコード） |

#### 3.8.3 描かなくてよいときは描かない（X-2・W2-10 の前提）

今のイベントループ（読んだ）: 前面では `ControlFlow::Poll`（`surface_lifecycle.rs` の `ACTIVE_CONTROL_FLOW`）で、`handle_redraw_requested` の末尾で `pace_frame`
（`target_fps` まで待つ）→ `request_redraw` を毎フレーム繰り返す。背面（`suspended`）だけ `ControlFlow::Wait`。winit 0.30.13 の Android の `request_redraw` は
フラグを立ててルーパーを起こし、同じ周回の終わりで `RedrawRequested` を配る（`platform_impl/android/mod.rs` の 110-128・340-356・543-608 行）。

試作（`idle=<N>`・`wake_ms=<M>`。`engine/core/ui_spike/idle_redraw.rs` の純粋な判定と `app/ui_spike_hooks.rs`）: フレームの末尾の要求を 1 か所
（`request_next_frame`）にまとめ、WindowEvent（入力・大きさ・フォーカス）の無いフレームが N 回続いたら要求をやめて `ControlFlow::Wait`（`wake_ms` があれば
`WaitUntil` で M ミリ秒ごとに 1 フレーム）にする。WindowEvent が来たら `request_redraw` して `Poll` へ戻す。意図して止めている間はフレームの凍結の見張り
（`[PLAY_WD]`）を黙らせる。エディタに埋め込んだ実行と Edit では使わない。

| # | 試したこと | 結果・数値 | 根拠 |
|---|---|---|---|
| D-1 | PC: `idle=30,wake_ms=1000` | 30 フレームの後に止まり、以後は **約 1,016 ms ごとに 1 フレーム**（`[PLAY_HB] gap=1016〜1018ms`）。`[PLAY_WD]` は出ない。IPC の命令（`SCREENSHOT`・`STOP`）はフレームの中でしか処理されないので、次に起きるまで（最大 1 秒）遅れた | 実行: PC の SEED.exe のログ（`[SEED UI SPIKE] idle: …`・`[PLAY_HB]`） |
| D-2 | 実機: 止めたときの fps・CPU・GPU と、入力での再開 | **PC と同じに動いた（実機。§3.8.7 の R-10〜R-13）**。止めている間は 0 fps・GPU の仕事なし・CPU はプロセス全体で 1 コアの 82.9% → 6.0%。タップで同じ周回のうちに再開した（入力からフレームの頭まで 0.07〜4.78 ms） | 実行（実機） |
| D-3 | 止めている間も CPU を使い続けるもの（W2-10 で扱う） | 読んだ範囲で、フレームと独立に回るのは 3D・2D の物理のスレッド（Play の最初のフレームの末尾で起動）、生存確認（3 秒ごと）、フレームの凍結の見張り（0.5 秒ごと）、音声の出力（rodio／AAudio）。物理のスレッドは描画を止めても自分の周期で進むので、W2-10 では止めている間は物理も止める（背面の `background_gate` と同じ扱い）かを決める。**実機でも、止めている間の CPU 6.0% の大半（5.5%）は名前の付いていない 2 本のスレッド**（logcat の名前は `android_main` を受け継いだもの。物理の 3D・2D と見られる。推論） | 読んだ: `physics/thread.rs`・`physics/background_pause.rs`・`frame_renderer.rs` の末尾・`heartbeat.rs`・`play_diag.rs`（音声は android.md §16） |

#### 3.8.4 決定

**E-06（Android の文字入力）: (a) GameActivity の文字入力を使う。形は「操作は android-activity の API、知らせは MainActivity の上書き」。**

- **操作（エンジン → IME）**: runtime/android/native が `android_main` で受け取った `AndroidApp` を複製して持ち（winit へは元を渡す。同じ Activity を指す）、
  `set_ime_editor_info`（入力の種類・アクション・`IME_FLAG_NO_FULLSCREEN`）→ `show_soft_input(false)`、`hide_soft_input(false)`、エンジンが本文を変えたとき
  （消去・プログラムからの設定）は `set_text_input_state` を呼ぶ。どれも UI スレッドの作業の列に順に積まれるので、エンジンのスレッドから呼んでよい（I-4）。
- **知らせ（IME → エンジン）**: `MainActivity` が `stateChanged`・`onEditorAction`・`onSoftwareKeyboardVisibilityChanged` を上書きし（super を先に呼ぶ）、
  `onApplyWindowInsets` で IME の高さ（`WindowInsetsCompat.Type.ime()` の bottom）を取り、今の JNI の流儀（Java → native・UTF-8 の byte[]・数値。§1.2）で
  エンジンの置き場へ渡す。winit が捨てる `TextEvent`・`TextAction` には頼らない（I-1）。ネイティブから `text_input_state()` を読むこともしない（I-7）。
  受けた JNI はイベントループを起こす（W2-10。I-10）。
- **本文の持ち主**: Android は IME（GameTextInput の Editable）が本文・選択・変換中の区間を持ち、エンジンは受けた状態で写しを丸ごと置き換える。
  Windows は winit の `Ime::Preedit`（変換中の文字とカーソルのバイト位置）・`Ime::Commit` をエンジンの本文へ当てる（入力欄にフォーカスが来たら
  `set_ime_allowed(true)` と `set_ime_cursor_area`、外れたら `false`）。エンジンの入力欄の状態（本文・選択・変換中の区間）を 1 つの形にそろえ、
  プラットフォームの差は受け口で吸収する。添字は受け口で UTF-8 の境界へそろえる（Android は UTF-16 で届く。I-6）。
- (b) Java の `EditText` を重ねる案は採らない（見た目がゲームの UI と揃わない・描画面の上の View の位置合わせと重なりの管理が要る）。
- **実機で確定（2026-09-28。§3.8.7）**: キーボードの表示・非表示、日本語のかな入力の変換中の区間（`setComposingText`）と確定（`commitText`）、
  「かんじ」→「漢字」の候補の選択、完了のアクション（`onEditorAction` 6）、数字のキーボード、IME の高さ（979 px。描画面の大きさは変わらない）、
  戻るでキーボードが閉じた後の戻る（Escape）が、この形で取れた。確かめた IME は利用者の端末の Simeji 23.6.1（Gboard は未確認）。
- 実機で分かった注意: 一度も本文が入っていないときに `text_input_state()` を読むと落ちる（I-12）。ネイティブから `set_text_input_state` を呼ぶと、
  Java の `stateChanged` にも同じ状態が返ってくる（エンジンが変えた本文の写しも同じ受け口で揃う）。数字のキーボードでも IME の側で
  かなへ切り替えられるので、数字だけの欄はエンジンの側でも数字以外を捨てる（§3.3 のスライダ＋数値欄のとおり）。

**切り抜き: 軸に沿った矩形は GPU の scissor（C-1〜C-3）、角丸・円はシェーダーの SDF。**

- 矩形: 試作の形（領域の表・ランの分割・`set_scissor_rect`）を W2-1 で本番にする。根は名前ではなくコンポーネント（レイアウトの矩形＋「子を切り抜く」）。
  **入れ子の深さに上限は設けない**（祖先の鎖で AABB の交差を取るだけ。表の番号は u16）。メインパスで描くとき（エディタのビュー）はパスのビューポートの内側へ交差させる。
- 角丸・円（アイコン・カード）: スプライトのシェーダーに角丸の矩形の SDF を足す（インスタンスごとに角の半径。円は短い辺の半分。境界は `fwidth` でぼかす）。
  **子を角丸で切るのは、いちばん内側の角丸の祖先 1 つだけ**を、フレームバッファの画素座標の SDF で行う（スクリーンスペースで回転が無ければ逆行列が要らない）。
  外側の角丸の祖先は矩形として scissor で切る（W2-4）。
- 当たり判定（`pick_2d`）も同じ領域の表を使う（押した点がすべての祖先の切り抜きの内側のときだけ当たる）。レイアウト・描画・当たり判定の座標の計算は
  W2-1 で 1 つの走査にまとめ（backlog「2D ノードのレイアウト計算が 5 か所に重複」）、その出力に切り抜きの番号を入れる。
- 回転したノード・3D ワールドキャンバス・任意の形はこの方式では正確に切れない。要るときはオーバーレイパスに既にあるステンシル（`overlay_depth_view` は
  ステンシル付き）で行う（今は作らない）。
- 描画呼び出しは「レイヤーと種別で並べた列の中で切り抜きの番号が変わる回数」だけ増える（C-2）。スクロール領域 1 つの画面で数本〜十数本の見込み（推論）。
  行ごとに切り抜きを作らない（一覧全体で 1 つ）。画面外の行は描画アイテムにしない（W2-3 の行の再利用）。

**描かない（W2-10）: 次のフレームの要求をフレームの末尾の 1 か所で決め、止めている間は `ControlFlow::Wait`（次の予定があれば `WaitUntil`）。**

- 「描く理由」（どれかがあれば次のフレームを要求する）: 入力の WindowEvent、JNI で届くプラットフォームのイベント（受け口がルーパーを起こす）、
  文字入力（I-10）、IPC（読み取りのスレッドが EventLoopProxy で起こす）、スクリプトの要求（1 フレーム・時刻を指定）、動いているもの
  （アニメーション・スクロールの慣性・画面の遷移）の「動いている」の申告。部品（C# の `SEED.UI`）は動いている間だけ申告する。
- **スクリプトの `Update` は止まっている間は呼ばない**（フレームを回さない）。時間で何かをするスクリプト（時計の表示・タイマー）は次に起きる時刻を申告する
  （`WaitUntil`）。目覚まし・`SEED.Platform` のイベントは今どおり待ち行列に残り、起きた最初のフレームで配る（落とさない。JNI の受け口が起こすので遅れない）。
  センサーを読む画面・鳴動画面のように毎フレーム動かしたい画面は「動いている」を持ち続ける。
- 止まっていた時間はゲームの時間へ入れない（背面から戻ったときと同じく dt を切り詰める）。フレームの凍結の見張りは、意図して止めている間は黙る（試作と同じ）。
- 既定: アプリのプロジェクト設定で有効にする（ゲームは今のまま毎フレーム描く）。エディタに埋め込んだ実行では IPC が起こせるようになるまで無効。
- → **2026-09-28 の W2-10a で実装**（docs/redraw_policy.md）。IPC の読み取りのスレッドが起こすようになったので、エディタに埋め込んだ Play でも
  on_demand を使う（Edit とエディタの PAUSE〈デバッグカメラ〉は常に毎フレーム描く）。埋め込んだ Play での動きは未確認（W2-10）。
  実キーボードのキーの押しっぱなしは理由にしない（Windows の日本語キーボードの「半角/全角」キーが押されたまま残るため。redraw_policy.md §3）。

#### 3.8.5 W2-1〜W2-11 の見直し

| 段階 | W2-0 を受けて変えたこと | 規模（旧 → 新） |
|---|---|---|
| W2-1 | **済**（2026-09-27〜28。W2-1a・W2-1b）。**W2-1a（済）**: 2D ノードのレイアウト計算を `engine/core/canvas_layout`（純関数 `resolve` と木を 1 回たどる `CanvasLayoutPass`・表 `CanvasLayoutTable`）へ一本化し、描画・キャンバス枠・ID 描画・当たり判定・2D 物理はこの表を読むだけにした（旧 5 か所とランダムな木 3,000 個でビット単位の一致を確認。WarashibeFishing の画面と当たり判定は不変）。切り抜きを本番化: `CanvasClipComponent`（インスペクタ・スクリプト `SEED.CanvasClip.Enabled`）、スプライト・テキスト・2D パーティクル・`SEED.Draw` の図形を scissor で切り、メインパスはビューポートと交差、当たり判定も切り抜きの内側だけ（docs/canvas_camera_rework.md §6）。**W2-1b（済）**: レイアウトのコンテナ `CanvasStack`・`CanvasWrap`・`CanvasGrid` と子の側の指定 `CanvasLayoutItem`（伸ばす重み・大きさの指定と上下限・揃えの上書き・無視させる・親に合わせる）を `CanvasLayoutPass` の中で測って並べる（2 段の計算を 1 回の走査の中で。測る回数はノード数に比例）。コンテナの配置は子のアンカー・位置より優先し、伸ばした軸は子の Sprite も矩形いっぱいに描く。dp はルートキャンバスの `CanvasComponent.unit`（px 既定・dp）。安全領域の部品 `CanvasSafeAreaComponent`（辺ごと。フレームごとの画面の写しから読むので回転・システムバーに追従）。エディタの GPU の ID 描画も切り抜く。3D ワールドキャンバスの子の ID・枠の走査も表を読む形へ寄せた。インスペクタ・シリアライズ・IPC・スクリプト（`SEED.CanvasStack` など）まで通した。PC の検証用に `SEED_SIM_SAFE_AREA`・`SEED_SIM_SCALE_FACTOR`（docs/canvas_camera_rework.md §6.3〜6.5） | 3〜4 → 3〜4（1a で約 2、1b で約 2） |
| W2-2 | **済**（2026-09-28）。ジェスチャーアリーナを `engine/core/input/gesture/`（純ロジック）に置いた: `CanvasGestureComponent`（タップ・長押し・ドラッグ・フリックの旗・ドラッグの軸・押下の見た目・最小のヒット領域 48 dp）を付けたノードだけが、指ごとに押した位置の経路（子 → 親。W2-1 のレイアウトの表・切り抜き）で競い、最初に勝ちを申し出た 1 つが勝つ（ドラッグ 8 dp・長押し 500ms・離したときの最初のタップ）。勝ったドラッグは指を捕捉、押下の見た目は PressDown / PressCancel / PressUp（スクロールの中は 100ms 待つ・入れ子は内側だけ）、複数指・外へ出た・アプリが背面へで取り消し、フリックは直近 100ms・20 標本の最小二乗。判定は入力イベントの時刻（`Input` が時刻つきで記録。PC は受け取った時刻・注入は予定の時刻。**Android は winit が MotionEvent の時刻を渡さないので受け取った時刻で代用**）。閾値は名前付きの定数と `project_settings.json` の `"gestures"`。スクリプトは `SEEDScript.OnGesture*`（`SEED.GestureEvent`）・`SEED.CanvasGesture`、インスペクタ・シリアライズ・IPC まで通した。付けていないノードの `OnPointer*` は不変（WarashibeFishing の 56 点のクリックが一致）、両方あるノードは `OnPointerDown/Up/Click` をジェスチャーへ譲る。「動いている」の申告（`GestureArenaSet::activity`）を W2-10a 向けに用意。正典は docs/input_gestures.md。単体テスト（合成の指の列）と PC の注入で確認、実機は未実施 | 2 → 2 |
| **W2-10a（新）** | **済**（2026-09-28。正典は docs/redraw_policy.md）。判定を `engine/core/redraw/`（純ロジック）に置いた: フレームの末尾で「描く理由」（入力・押している指とボタン・注入の再生・ジェスチャー〈`GestureArenaSet::activity`〉・文字入力・動いているもの〈Animator の再生・パーティクル・読み込み中のモデル・物理のボディの速度〉・スクリプト・IPC・`SEED.Platform` のイベント・画面・音声フォーカス・撮影・区切り・予定の時刻）を集め、理由の無いフレームが `render_idle_frames`（既定 10）回続いたら次のフレームを要求せず `ControlFlow::Wait`（予定〈`RequestAfter`・ジェスチャーの期限・模擬の目覚ましと鳴動の安全弁〉があれば `WaitUntil`）。起こす口は `EventLoopProxy`（`redraw::wake`。IPC の `read_loop`・Android の JNI〈`nativeOnPlatformEvent`・`nativeOnScreenChanged`・`nativeOnAudioFocusChanged`・新しい `RedrawWaker.nativeRequestRedraw`〈MainActivity の文字入力の受け口〉〉）と `user_event`。起きた最初のフレームの dt は 1/60 秒で切り詰め（`Clock::limit_next_delta`）、止めている間は `Update` を呼ばない。有効にするのはプロジェクト設定 `render_policy: "on_demand"`（**既定 `continuous` は今までどおり毎フレーム描く**）とスクリプト `SEED.Redraw.Policy`。スクリプトの API `SEED.Redraw`（`Request`〈どのスレッドからでも〉・`RequestAfter`・`KeepAlive`・`SetContinuous`・`Policy`）。止めている間に物理の結果の待ち行列が伸び続けないよう上限 120 件（物理のスレッドは止めない＝W2-10）。W2-0 の試作 `idle=`・`wake_ms=` は外した。PC で止まる（12 秒で Update 10 回・CPU 2.3〜5.3% ⇔ continuous 18.6〜23.6%）・IPC が遅れない（`SCRIPT_DEBUG` 0.0〜0.6 ms・`SCREENSHOT` 69〜80 ms ⇔ continuous 70〜78 ms）・`Request`/`KeepAlive`/`RequestAfter`・押している間・模擬のイベントと目覚ましで起きる、を確かめ、WarashibeFishing の複製の 56 点のクリックと図鑑の画素が変更前と一致。**実機は未実施**（手順は docs/redraw_policy.md §9） | 1 |
| W2-3 | **済**（2026-09-28。正典は docs/ui_scroll_list.md）。スクロールの本体を `engine/core/canvas_scroll/`（純ロジック）と `CanvasScrollComponent`（設定・保存する）＋`CanvasScrollState`（実行中の状態・保存しない）に置いた: 窓（CanvasScroll を付けたノード）は CanvasGesture 無しで向きのドラッグとフリックでアリーナに参加し、レイアウトの走査が子へ渡す文脈を位置だけ平行移動する（窓がコンテナならスクロールの軸は箱の長さを決めずに並べる）。慣性・跳ね返り・ばね・スナップは Flutter の式と定数（Clamp = ClampingScrollSimulation = Android OverScroller の spline で 1,000 dp/秒 → 194 dp・0.46 秒、Bounce = BouncingScrollSimulation の 0.135 の減衰と端の外の摩擦 0.52(1−x/V)² を刻みに依らない閉じた式で、ばねは質量 0.5・硬さ 100・減衰比 1.1、Page = PageScrollPhysics、Interval = 止まる位置に最も近い倍数へ FrictionSimulation.through）。同じ向きの入れ子は内側から外側へ残りを渡す（`hand_off_to_parent`）、慣性・ScrollTo の途中に触れると止めてその指を中の行へ渡さない（Flutter の IgnorePointer と同じ）。切り抜きと一緒に付けると、窓の切り抜き + 250 の外の部分木を描画アイテム・ID 描画・当たり判定・ジェスチャーから外す（1,000 行の一覧の位置 20,000 で描画アイテムは スプライト 1,019・テキスト 999 → 20・16、UI の描画順の統合・GPU の積み込み 31.9 → 0.67 ms。debug）。スクリプトは `SEED.CanvasScroll`（位置の読み書き・ScrollTo）・`OnScrollStart/OnScroll/OnScrollEnd`・`GameObject.CancelGestures`、C# の `SEED.UI.ListView`（見える行だけをプレハブから作って使い回す。1,000 件で作った行は 18 行・フレーム 4 ms）・`SwipeActions`（ItemTouchHelper の 120 dp/秒・半分の閾値・250ms）。動いている間は W2-10a の motion。W2-2 の申し送り（押している行が消えると PressCancel の届け先が無い）は CancelGestures で手当て。インスペクタ・シリアライズ・IPC・docs まで通した。**実機は未実施**（手順は ui_scroll_list.md §11） | 2〜3 → 2〜3 |
| W2-4 | **済**（2026-09-28。正典は docs/ui_components.md）。形と塗り: SpriteComponent に `shape`（矩形の四隅ごとの角丸・楕円・弧〈リング〉・縁の線）・`fill`（単色・線形 2〜4 色と角度・放射）・`nine_slice`（枠の 4 辺・辺と中央の伸ばす／繰り返す〈回数を丸める〉）・`shadow`（SDF のぼかしの影）を足し、欄が既定でないスプライトだけを別のパイプライン（`sprite_shape.wgsl`。パラメータはストレージバッファ・インスタンスは行列 + 番号）で描く。**既定の欄のスプライトは従来の経路のまま**（同じ頂点・同じバッチ・同じ描画コマンド。WarashibeFishing の図鑑の画素・56 点のクリックが変更の前後で一致）。アンチエイリアスは画素の幅（dpdx・dpdy）。CanvasClip に形（角丸・楕円・スプライトの形に合わせる）を足し、いちばん内側の形のある領域だけを「ワールド → 領域のローカル」の写像で SDF で切る（スプライトだけ。外側と文字・図形は外接矩形の scissor）。当たり判定（ポインタイベント・ジェスチャー）も形と切り抜きの形に合わせ、最小のヒット領域まで形ごと広げる（円のボタンの角は押せない）。部品は `SEED.UI` のスクリプト（`UiWidget` の派生: Button・Toggle・Checkbox・Slider・NumberField・SegmentedControl・ChipGroup・RadioGroup〈SelectItem〉・ProgressBar・ProgressRing）で、状態 → 見た目は `Looks/` の純関数、値はテーマのトークン（`UiTokens`・埋め込みの `default_theme.json`・`UiTheme`）。見本は `templates/ui/`（プレハブ 13・ギャラリーのシーン・`UiGalleryDemo.cs`）。インスペクタ（関係のない欄は隠す）・シリアライズ（既定は保存しない）・IPC・スクリプト API（`Sprite.Shape` など・`CanvasClip.Shape`）・docs まで通した。PC のギャラリーで見た目と IPC の当たり判定（丸いボタンの角・丸い切り抜きの外・無効・選べない項目が反応しない）を確かめた。**実機は未実施**（手順は ui_components.md §9） | 2〜3 → 2.5〜3.5 |
| W2-5 | **済**（2026-09-28。正典は docs/ui_components.md §11）。C# の部品 `SEED.UI.WheelPicker`（ホイールの列）と `SEED.UI.TimeWheel`（時・分〈と午前/午後〉の列。値は `TimeOnly`）。スクロールは W2-3 の CanvasScroll の Interval のスナップ（間隔 = 行の高さ。先頭の余白 =（窓 − 行）/ 2 で「位置 = 行 × 行の高さ」が行の中央）を**そのまま使い、Rust は変えていない**。行の曲面の見た目は Flutter の ListWheelViewport と円柱の投影の行列を行の中心で展開した純関数（`WheelLook`。映る位置・縦の縮み・横の縮み・中央の拡大・帯の外の濃さ 0.447 × 面の傾き。値は CupertinoDatePicker の直径の比 1.07・遠近 0.003・詰め具合 1.25・拡大 2.35/2.1。2026-09-28 に Flutter の master のソースで確認し、単体テストで 4×4 の行列の積と照合）。端をつなげる循環は ListView の使い回しで（項目を約 20 万単位ぶんくり返し、真ん中の周から始めて止まるたびに遠ければ真ん中へ見えない飛び方）。中央の行が変わるたびの SelectionChanged と軽い触感（`Haptics.Tap`。指とその慣性の間だけ・1 フレーム 1 回）、止まったときの Settled、選べない行（灰色・止まると最も近い選べる行へ 200ms で戻る）、止まっている列の行のタップでその行へ（動いている間のタップは止めるだけ）、キーボード（最後に触れたホイールの ↑↓・時刻ホイールの ←→）、スクリプトからの値（動きあり・なし）。時刻ホイールは 24/12 時間の切り替え・分の刻み（1 時間を割り切る数・最も近い刻みへ丸める）・12 時間表記の午前/午後の連動（Flutter の region/amPm の方式）。テーマのトークン 7 つ（`size.wheel_item` 32・`text.wheel` 21・`radius.wheel_band` 8・`size.wheel_band_inset` 9・`opacity.wheel_dim` 0.447・`motion.wheel` 0.3・`motion.wheel_correct` 0.2）。見本 `templates/ui/prefabs/wheel_row.actor`・`wheel_picker.actor`・`time_wheel.actor` とギャラリーの段（24 時間・12 時間 5 分刻み・選べない行のある数のホイール）。PC のギャラリーで IPC の入力の注入（ドラッグ・フリック・タップ・キー）とデバッグの命令で、循環（59 → 00・23 → 0）・連動・スクリプトの値・選べない行・タップ・無効・真ん中の周へ戻る・テーマの差し替え・止まったら on_demand で描画が止まる・フリックの前後で他の部品の作り直し 0 回（UC-4）を確かめ、WarashibeFishing の複製の 56 点のクリックと図鑑の画素が変更前と一致。**実機は未実施**（手順は ui_components.md §11.9） | 1 → 1 |
| W2-6 | 経路が決まった（E-06）ので 3 つに分ける: **W2-6a** 受け口（MainActivity の上書き・JNI・Windows の `Ime`・エンジンの入力欄の状態・添字の変換・スクリプトの API）1.5〜2、**W2-6b** 入力欄の部品（カーソル・選択・変換中の下線・キーボードを避ける・数字のキーボード・貼り付けの禁止）1〜1.5、**W2-6c** 文字の寸法・グリフの追い出し 1 | 3〜4 → 3.5〜4.5 |
| W2-7 | **済**（2026-09-28。正典は docs/ui_navigation.md）。C# の `SEED.UI`: 画面のスタック `ScreenStack`（積む・下ろす・置き換える・根まで・やり直し。押し込み＝右から・覆う＝上から・フェード＝幕を通して。時間と曲線はテーマの `motion.*`。画面の枠〈背景は端まで・中身は安全領域・遮る板〉を 2 フレームで作り、落ち着いたら不透明な画面の下を隠す＝描かない・入力を受けない。`KeepState = false` は手放して作り直す）、画面の土台 `UiScreen`（Enter・Shown・Hidden・Exit・OnBackPressed）、下のタブ `TabHost`・`TabBar`・`TabItem`（タブごとのスタックを隠して保つ・もう一度押すと根へ）、重ねる面 `ModalHost` と `Dialog`（ボタン 1〜3・結果 `Task<DialogResult>`・幕のタップ・閉じないダイアログ）・`BottomSheet`（縦の CanvasScroll の Interval のスナップで半分・全体の段、W2-2 の速度で払って閉じる、中身の一覧の端の残りを受ける入れ子）・`TopSheet`（上からの覆い。Flutter 版と同じく下へ払って閉じる）、トースト `ToastHost`・`Toast`（3 つまで・待ち・実時間・横へ払う）、戻るの段 `BackDispatcher`（フォーカス → ダイアログ → シート → 覆い → スタック・タブを内側から → 最初のタブ → `App.MoveTaskToBack`。独自の層を足せる）、フォーカスの範囲 `UiFocus`・`FocusScope`（`WheelFocus` を寄せた。W2-6 の入力欄は `IFocusable`・`IBackConsumer`）。**Rust**: 重なる画面の前後と画面の大きさを知らない出入りのため、`CanvasLayoutItem` に実行中だけの `translate`・`translate_fraction`・`layer_bias`（保存しない。レイアウト後の平行移動と部分木のレイヤーの底上げ。描画・pick_2d・ジェスチャーの遮り・ID 描画が同じ値で並べる。canvas_camera_rework.md §6.7）と `Screen.DpScale` を足した（既定の 0 では従来と同じ計算。WarashibeFishing の図鑑の画素とクリックは不変）。見本 `templates/ui/scenes/ui_navigation.scene`。実機の戻るキーは未確認（手順は ui_navigation.md §12） | 2〜3 |
| W2-8 | **済**（2026-09-28。正典は docs/ui_charts.md）。C# の `SEED.UI` の `LineChart`（点の印・滑らかな曲線〈単調な 3 次補間 = Fritsch–Carlson。点と点の間で行き過ぎない〉・線の下の塗り〈上から下へ透明へのグラデーション〉・平均の基準線と文字・欠けた値は点を打たずにつなぐ）・`BarChart`（縦・横・積み上げ・先だけ角丸・合計 0 の列も最低の高さ・選んだ列の強調）と共通の土台 `ChartView`（軸〈目盛りは数 = 1・2・5 × 10^n、時刻 = 15〜720 分、日付 = 1〜365 日の候補から区間の上限以下の最初のもの。日付の目盛りはデータの最初の日から＝パンで跳ねない〉・書式〈H:mm・HH:mm・M/d・3 桁区切り〉・範囲の自動〈時刻は上下 30 分・最小 2 時間・0〜24 時へ幅を保ってずらす〉・吹き出し〈タップ・長押しで横 24 dp 以内の最寄りの点・列は空の高さまで〉・パンとズーム〈横のドラッグ・払う = FrictionSimulation の慣性・2 本指のピンチ・± のボタン。1〜6 倍・端で止まる。パンできないときはドラッグを親へ渡す〉）。純粋な計算は `Charts/Model/`（`editor/tests/UiComponentsTests` の 19 件）。**Rust**: W2-2 のアリーナに 2 本指のピンチ（`gesture/pinch.rs`。`CanvasGesture.pinch`・`OnGesturePinchStart/Update/End`・`GestureEvent.Scale`。外側の一覧が取っている指では始めない）、`SEED.Draw` に見た目の拡張（`DrawStyle`: 画面の 1 画素のアンチエイリアス・線形グラデーション）・線の下の塗り `Draw.Area`・拡張つきの図形の軽い三角形分割（365 点の折れ線の三角形 15,986 → 4,757。最適化なしの build で 1 フレーム 19.2 → 6.4 ms、滑らか＋塗りは 92.1 → 7.4 ms）・図形へ `layer_bias` の底上げ（積んだ画面の中のグラフが背景の下に隠れない）。既存の `SEED.Draw` の見た目・頂点は変えていない（WarashibeFishing の図鑑の画素と 56 点のクリックが変更前と一致）。見本 `templates/ui/scenes/ui_charts.scene`（起床時間の 30 日・全期間 365 日・月ごとの積み上げ・ペナルティ履歴 365 日・横の棒）。PC で吹き出し・パン・慣性・± を IPC の注入で確かめた。ピンチは単体テストだけ（PC の注入は 1 本の指）。**実機は未実施**（手順は ui_charts.md §10） | 2 → 2.5 |
| W2-9 | **済**（2026-09-28。正典は docs/ui_theme.md）。C# の `SEED.UI` のテーマ: `UiTheme.Load(path)`（プロジェクトのアセットの JSON・`Assets.ReadText`）・`Apply(theme, animate)`・`Current`・`Changed`（切り替えごとに 1 回）。**切り替えたら登録簿（`UiRegistry`）の全部品の `ApplyLook` をその場で呼び直す**（隠れているタブ・積まれた画面の部品も。任意で色を行き先の `motion.theme` 秒で補間＝sRGB の成分ごと、数・書体は最初から行き先）。**継承と一部だけの上書き**（`extends` は既定・assets://・相対パス。鎖を根〈組み込みの既定のテーマ〉から葉へ重ね、無いトークンは基 → 既定へ落ちる。読めない基・壊れた基・輪・深すぎは警告して既定へつなぐ）。**知らない名前・型の誤りは警告して既定の値**（例外で止めない。アプリ独自は `app` のグループ）。**トークンの表を 1 か所**（`UiTokenCatalog`: 名前・型・使う部品。既定値は `default_theme.json`。docs/ui_theme.md §8 の表は両方から作り、テストが反射で定数・表・既定のテーマの揃いと docs の一致を確かめる。153 行）。**明暗**: テーマの `brightness` と `light` / `dark` の節（既定のテーマは暗い方＋`light` の節＝両方）、選び方 `UiBrightnessMode`（テーマのまま〈既定〉・端末に従う・強制）、端末の明暗は `SEED.Platform` の `app.ui_mode`（Android はメインプロセスの `local/UiModeCommand`＝`Configuration.uiMode`、変化は `MainActivity.onConfigurationChanged` → `platform.ui_mode_changed`。PC は Windows の「既定のアプリ モード」のレジストリと `WindowEvent::ThemeChanged`、模擬の差し替え `app.sim_set_ui_mode`）。書体のトークン（`font.family`・`font.weight`・`font.weight_title`。部品の文字は `UiTextStyle`）、部品でない飾りの結び付け `ThemeStyle`。**Wake or Pay のテーマの写し方**: `brightness`＋`seed_color`（種の色から主の色・選択の色を WCAG のコントラストの約束で作る `UiSeedColors`）＋`fixed` は `app.kakugo.*`（ui_theme.md §6。W3 でこの規則のファイルを作る）。見本のテーマ `templates/ui/themes/`（forest・sunrise・forest_round〈継承〉）とギャラリーの仕上げ（上にテーマの帯・縦のスクロールのページに一覧〈W2-3〉・画面の組み立て〈W2-7〉・グラフ〈W2-8〉の段を足した）。単体テスト 16 件（C#）・Rust の模擬 4 件。PC のギャラリーでテーマごとの見た目・明暗・継承・書体・補間・警告を確かめ、WarashibeFishing の複製の図鑑の画素と 56 点のクリックが変更前と一致。**実機は未実施**（ui_theme.md §11） | 1〜2 → 2 |
| W2-10 | 残り: 部品と画面ができた後に通しで詰める（dt の切り詰め・止める判定のフレーム数・時計の表示の 1 秒ごとの更新・UC-10 の計測）。W2-10a からの持ち越し: 止めている間の物理のスレッドを止めるか（背面の `background_pause` と同じ扱い。実機の止めている間の CPU 6% の大半）・止めていた後の最初のフレームの重さ（R-13）・シェーダーの時間で動くもの・ファイルの保存の検知（1 秒ごとの更新時刻の確認）は申告しない・エディタに埋め込んだ Play での確認・エディタの画面の `render_policy` の欄・実機での確認（docs/redraw_policy.md §7・§9） | 1〜2 → 1 |
| W2-11 | 変わらず | 2 |

**合計の目安（W2-0 を除く）: 21〜28 → 23〜29 段階相当**（§3.7 の表の W2-1〜W2-11 の和との比較。W2-10a と W2-4・W2-6 の分だけ増えた）。
上振れしやすい所は、レイアウトの重複の整理（W2-1）と、日本語の変換中の表示（W2-6b）に移った。IME の経路の不確かさ（E-06）は解けた。

**最初に着手すべき段階: W2-1（土台）**（2026-09-27 に W2-1a として (1)(2)、2026-09-28 に W2-1b として (3)(4) を済ませた。2026-09-28 に W2-2 の入力と W2-10a の「描く理由」、W2-3 のスクロールと一覧も済。2026-09-28 に W2-4 の基本の部品と形と塗り、W2-5 の時刻ホイールも済。2026-09-28 に W2-7 の画面の組み立て、W2-8 のグラフ、W2-9 のテーマも済。次は W2-6）。すべての部品がレイアウト・切り抜き・当たり判定に乗るため。順は (1) 2D ノードのレイアウトの計算を 1 つの走査に寄せ、
その出力（ノードの矩形・切り抜きの番号・レイヤー）を描画と当たり判定で共有する → (2) 切り抜きのコンポーネント（試作の本番化）→ (3) `UiRect`・`Stack`・`Wrap`・`Grid`・
dp → (4) 安全領域の部品。**W2-6a（文字入力の受け口）は触る場所（Java・JNI・入力）が W2-1 と重ならないので、別の worktree で並べて進められる。**

#### 3.8.6 実機での確かめ方（2026-09-28 にこの手順で行った。結果は §3.8.7）

試作は既定で無効のまま残してあるので、デバッグ版の APK を起動オプションで起動すればそのまま確かめられる（Git Bash。`MSYS_NO_PATHCONV=1`）。
前面が自分のアプリ・ランチャー・ロック画面のときだけ操作する。`logcat -c` はしない（起動の直前の端末の時刻を控えて `-T`）。

```bash
S=<シリアル>; APP=com.seedengine.uispike; ACT=$APP/com.seedengine.runtime.MainActivity   # アプリ ID は試験のプロジェクトの設定
dotnet run --project editor/tools/SeedAndroid -- install --project <試験のプロジェクト> --serial $S
# ── 文字入力（ime）: 命令は files/ui_spike/ime_cmd に 1 行（show_text / show_number / show_multiline / hide / set <文字列> / clear / dump）
#    起動の前に clear を積んでおく（一度も本文が入っていない状態で試作が text_input_state() を読むと落ちる。§3.8.1 の I-12）
adb -s $S shell "run-as $APP sh -c 'mkdir -p files/ui_spike && echo clear > files/ui_spike/ime_cmd'"
adb -s $S shell am start -W -n $ACT --es seed.ui_spike ime
adb -s $S shell "run-as $APP sh -c 'mkdir -p files/ui_spike && echo show_text > files/ui_spike/ime_cmd'"   # キーボードが出る
adb -s $S shell input text abc          # 押下が GameTextInput の本文に入る（processKeyEvent の経路）
adb -s $S shell input keyevent 66       # 1 行の Enter → onEditorAction action=6（完了）
#   キーボードのキーを input tap で押すと IME の commitText / setComposingText の経路（英字の変換中の区間も見える）。日本語の変換は人の手で
adb -s $S logcat -d -v threadtime -T "<起動の時刻>" SEED:V SEEDImeSpike:V gti.InputConnection:V '*:S' | grep -E "IME SPIKE|SEEDImeSpike|performEditorAction"
#   見るもの: [SEED IME SPIKE] native state …（UI スレッドで読んだ状態・UTF-8 / UTF-16 / 文字の数）と SEEDImeSpike の stateChanged・onEditorAction・
#   onSoftwareKeyboardVisibilityChanged・WindowInsets ime.bottom。日本語で selection と compose の値が UTF-16 の単位か（I-6）
# ── 描かない（idle）: gpu_timing で [SEED GPU] の 1 フレームの GPU 時間とフレーム数、[SEED HEARTBEAT] の fps
#    （2026-09-28 の W2-10a で idle=・wake_ms= を外した。今の APK では project_settings.json の render_policy: "on_demand" で確かめる。
#     手順は docs/redraw_policy.md §9。下の 3 行は W2-0 の APK の控え〈この試作の入った版〉でだけ動く）
adb -s $S shell am start -W -n $ACT --es seed.gpu_timing 1                                   # 基準（毎フレーム）
adb -s $S shell am start -W -n $ACT --es seed.gpu_timing 1 --es seed.ui_spike idle=30        # 30 フレームの後に止まる
adb -s $S shell am start -W -n $ACT --es seed.gpu_timing 1 --es seed.ui_spike idle=30,wake_ms=1000
#   CPU: run-as で /proc/<pid>/task/*/stat の utime+stime を 15 秒あけて 2 回読む（android.md §14.6 と同じ）
#   入力での再開: 止まっている間に input tap（アプリの空いた所）→ [SEED UI SPIKE] idle: 入力で描画を再開します（Touch）と
#   「入力から最初のフレームの頭まで N ms」、[SEED HEARTBEAT] の fps が戻ること
adb -s $S shell am force-stop $APP
```

#### 3.8.7 実機の結果（2026-09-28、Pixel 6a / Android 17）

W2-0 のときに作った APK の控え（`com.seedengine.uispike`・デバッグ版・arm64-v8a・libSEED.so は debug・描画品質 `mobile`）を `adb install -r` で入れた
（作業ツリーからは作り直していない）。画面は縦 1080×2400・システムバーを出したまま。ソフトキーボードは利用者の端末の **Simeji 23.6.1**
（日本語 12 キー）。日本語の入力と変換は利用者の手で、それ以外は `adb shell input` で行った（前面が試験のアプリのときだけ）。CPU は run-as で
`/proc/<pid>/task/*/stat` の utime+stime を約 18.5 秒あけて 2 回読み、1 コアを 100% とした平均。GPU は `seed.gpu_timing=1` の `[SEED GPU]`（3 秒ごとの 1 フレームの平均）。
証拠の logcat と画面は作業フォルダ（`tmp/w2_0/logcat_*.txt`・`dev_*.png`。私物端末のログなのでリポジトリに入れていない）。

| # | 試したこと | 結果・数値 | 証拠 |
|---|---|---|---|
| R-1 | `ime` の試作の最初の起動 | **落ちた**（I-12）。起動から 0.7 秒、試作の最初の `text_input_state()`（UI スレッド）が `slice::from_raw_parts(null, 0)` で panic → abort。起動の前に `clear` を積んで避けた（以後は落ちない） | `logcat_ime_boot.txt`・`logcat_ime_boot2.txt` |
| R-2 | `show_text`（`TYPE_CLASS_TEXT`・`IME_ACTION_DONE`・全画面の入力欄なし） | 日本語 12 キーのキーボードが出た。命令から `setSoftKeyboardActive(true)` まで 19 ms、IME の高さの知らせまで 488 ms（`onImeInsetsChanged bottom=979`・`onSoftwareKeyboardVisibilityChanged true`・WindowInsets `ime.bottom=979`）。**描画面は 1080×2400 のまま**（`Resized` なし・安全領域も変わらない）＝キーボードは画面の上に重なり、ずらすのはエンジン | `dev_kb_text.png`・`logcat_ime_show.txt` |
| R-3 | `input text abc` → `input keyevent 66`（Enter）→ もう 1 回 Enter | キーの押下は IME が先に受けてローマ字の変換をした（「あbc」・変換中 0..3）。1 回目の Enter は変換の確定（変換中 → -1..-1）、2 回目で `performEditorAction 6` → **MainActivity の `onEditorAction(6)`**。エンジンには届かない（winit が捨てる。I-1） | `logcat_ime_ascii.txt`・`logcat_ime_enter2.txt` |
| R-4 | 利用者が「あいう」を入力し、変換せずに改行キー | `setComposingText` のたびに `stateChanged`（「あ」1..1・変換中 0..1 → 「あい」→「あいう」3..3・変換中 0..3）、改行キーで `commitText`（変換中 -1..-1）。その後の改行キー 2 回で `onEditorAction(6)` が 2 回。ネイティブの `text_input_state()`（UI スレッド）は「あいう」を **UTF-8 9 バイト・selection 3..3・変換中 (0, 3)** で返した＝**添字は UTF-16（文字）の単位**（I-6 を確認） | `logcat_ime_user1.txt` |
| R-5 | 利用者が「かんじ」を入力し、候補から「漢字」を選んで確定 | 「かんじ」（変換中 0..3）→ 候補の選択で `commitText: 漢字` → 「漢字」selection 2..2・変換中なし（UTF-8 6 バイト）。この IME は候補を選ぶ間に変換中の本文を候補へ置き換えなかった（候補はキーボードの中に出る）。**変換中の文字・確定の両方が今の受け口で取れる** | `logcat_ime_user2.txt` |
| R-6 | `show_number`（`TYPE_CLASS_NUMBER`） | 数字のキーボード（1〜9・0・*・#）が出た。本文（「漢字」）はそのまま。キーボードの側で「あA」へ切り替えられる | `dev_kb_number.png`・`logcat_ime_number.txt` |
| R-7 | `hide` | 命令から 26 ms で `onSoftwareKeyboardVisibilityChanged false`・`ime.bottom=0` | `logcat_ime_hide.txt` |
| R-8 | キーボードを出したまま戻る（`input keyevent 4`）→ もう 1 回戻る | 1 回目は IME が受けてキーボードを閉じた（エンジンへは届かない。`visible=false`）。2 回目は GameTextInput の `onKey` が素通しし、**エンジンに Escape が届いた**（`[SEED KEY FRAME] Escape:down+up`）＝入力欄にフォーカスがあってもキーボードを閉じた後は戻るの段（W2-P4）が効く | `logcat_ime_back.txt` |
| R-9 | 文字入力の間の描画・ログ | 59.3〜59.7 fps のまま。winit の「知らない入力」の警告は logcat に 0 件（入力した文字列がログへ漏れない） | `logcat_ime_user2.txt` |
| R-10 | 基準（毎フレーム描く。`gpu_timing` だけ） | **59.3 fps・GPU 4.9 ms/フレーム**（トーンマップ 2.1・提示 1.25・クラスタ 0.6・前方 0.35・オーバーレイ 0.33・UI 0.28）＝GPU の仕事は約 290 ms/秒。**CPU 82.9%**（android_main 63.3%・Mali のドライバのスレッド 9.6%。debug の .so） | `logcat_idle_base.txt` |
| R-11 | `idle=30` | 33 フレームで止まり、以後 **0 fps・GPU の仕事なし**（`[SEED GPU]` はフレームが無いので出ない）・**CPU 6.0%**（ほぼ名前の無い 2 本のスレッド＝物理と見られる。D-3）。`[PLAY_WD]` は 0 件 | `logcat_idle_on.txt` |
| R-12 | 止めている間のタップ（`input tap 540 1900`・3 回） | 3 回とも**同じ周回で再開**した（`入力で描画を再開します（Touch）` と同じミリ秒にフレームが始まる）。入力からフレームの頭まで **4.78 ms（40 秒止めた後の 1 回目）・0.07 ms・0.11 ms**。タップごとに 31 フレーム描いて 0.5 秒後にまた止まった（3 秒で 10.3 fps） | `logcat_idle_tap.txt` |
| R-13 | `idle=30,wake_ms=1000` | **1.0 fps・CPU 9.7%**。ただし 1 秒に 1 回だけ描くフレームは重い: CPU のフレーム 25〜54 ms・GPU 6.9〜12.1 ms（毎フレームのとき〈CPU 8.3〜9.8 ms・GPU 4.9 ms〉の CPU 約 3〜6 倍・GPU 約 1.4〜2.5 倍。止まっている間にクロックが下がるためと見られる。推論）。GPU の仕事は約 10 ms/秒（基準の約 30 分の 1） | `logcat_idle_wake.txt` |

- **E-06 は (a) で確定**（R-2〜R-9）。(b) へ戻す理由は見つからなかった。
- **描かない方式も確定**（R-11〜R-13）。数値は debug の .so（CPU の絶対値は配布版より大きい）。止めている間に残る CPU（物理のスレッド）と、
  久しぶりのフレームの重さ（R-13。入力の直後の最初のフレームも同じく重くなりうる）は W2-10 で扱う。
- 未確認: Gboard（利用者の端末は Simeji）、複数行（`show_multiline`）、横画面、配布版（release）の .so での数値、消費電力そのもの。

### 3.9 W2 の実機の結果（2026-09-28 14:05〜14:40、Pixel 6a / Android 17）

**経緯**: 利用者と W2 の手触りの確認（各正典の「実機の確かめ方」: スクロールと一覧・時刻ホイール・戻るの段・グラフのピンチ・明暗の追従・M7）を始めたが、
開発用の APK（SeedAndroid の既定＝libSEED.so が cargo の `dev` プロファイル。SEED クレートは最適化なし）でギャラリーが 24〜29 fps しか出ず、
利用者の判断（「UI だけの表示なら 60 fps で安定させたい。ほかの挙動の改善は fps が直ってから」）で**手触りの確認は止め、fps の計測と原因の特定へ切り替えた**。
コードは変えていない（計測だけ）。

**条件**: 作業フォルダの試験のプロジェクト UiDevice（`templates/ui` の写し＋W2-3 の `UiList.scene`。`com.seedengine.uidevice`・`render_policy: on_demand`・
描画品質 `mobile`・`system_bars: visible`・`android.features: [notifications]`。3 つの見本のシーンを `scenes[]` に登録して pak に入れ、`am start --es seed.scene` で
切り替えた）。計測は `--es seed.gpu_timing 1` の `[SEED GPU]`、`[SEED HEARTBEAT]`、`[PERF f=…]`、IPC（TCP。§21 の接続トークン）の `PROFILE_DUMP:8`
（スコープの木の平均・最大と 1 フレームごとの CPU 時間。最初の 240 フレーム）。スクロールは `input swipe`（縦 1,200 px・180 ms を上へ 3 回・下へ 3 回を 4 周。
ギャラリー・グラフは x=150 px のページ、ナビゲーションは「一覧」のタブ〈`SCRIPT_DEBUG:nav,tab,1`〉の中央）。同じ APK の中身で .so だけを
`dev`・`develop`（opt-level 1。PC の Play の既定の構成。`cargo ndk … build --profile develop` で手で作り `--skip-rust` で詰めた）・`release`（`--release`）に替えた。
`dumpsys gfxinfo` は SurfaceView を数えず（Total frames rendered: 0）、`dumpsys SurfaceFlinger --latency <BLAST の層>` は画面の周期（16,666,667 ns＝60 Hz）の行しか
返さなかったので使っていない。

| シーン | .so | スクロール中の fps（3 秒ごと） | 1 フレームの CPU 平均 / 中央 / 90% / 最大（ms） | 16.7 ms 超 | GPU 合計（ms） |
|---|---|---|---|---|---|
| ギャラリー | dev | **23.0〜26.0** | 38.0 / 35.8 / 40.5 / 231 | 208/208 | 7.4〜7.5 |
| ギャラリー | develop | 59.7 | 8.7 / 8.1 / 10.7 / 26.2 | 5/240 | 7.7〜7.9 |
| ギャラリー | release | **59.7** | 8.5 / 8.5 / 10.6 / 17.9 | 1/240 | 7.8〜7.9 |
| ナビゲーション（一覧のタブ） | dev | 59.3 | 13.4 / 13.7 / 15.3 / 16.9 | 3/240 | 6.9 |
| ナビゲーション | develop | 59.3〜59.7 | 7.4 / 6.4 / 9.3 / 12.4 | 0/240 | 7.4 |
| ナビゲーション | release | 59.7 | 7.3 / 6.7 / 9.2 / 12.4 | 0/240 | 7.3〜7.5 |
| グラフ | dev | **19.3〜24.3** | 41.5 / 41.5 / 43.4 / 45.8 | 191/191 | 8.2〜8.6 |
| グラフ | develop | 59.7 | 9.7 / 9.5 / 12.5 / 15.6 | 0/240 | 7.4〜7.6 |
| グラフ | release | **59.3〜60.0** | 9.2 / 8.7 / 10.6 / 12.2 | 0/240 | 7.6 |
| グラフ（`chart,only,none` で 5 つとも隠す） | release | 59.3〜59.7 | 7.0 / 7.1 / 8.7 / 12.5 | 0/240 | 5.4〜5.5 |

- **止まる画面は止まっている**: 3 シーン・3 つの .so とも、触らない間は `[SEED REDRAW] 描画を止めます（理由の無いフレームが 10 回…）` の後 `[SEED HEARTBEAT]` が **0.0 fps**
  （静止時の fps 0 は正しい。問題はスクロール中だけ）。起こしてから最初のフレームの頭まで: 入力 0.03〜0.48 ms（117 回）・IPC 0.03〜1.10 ms（33 回）。
- **GPU は律速ではない**（どの .so でも 7.4〜8.6 ms。ui 3.5〜4.1・トーンマップ 0.9〜1.6・提示 0.9〜1.0・オーバーレイ 0.35〜1.1・前方 0.3〜0.5・クラスタ 0.2〜0.4）。
  UI だけのシーンでも 3D の経路（クラスタ・前方・トーンマップ・提示のコピー）が毎フレーム約 3 ms 走る（backlog「固定分 約 4.5 ms」）。

**60 fps に届かなかった原因（数値から上位 3 つ）**

1. **開発用の APK の libSEED.so が最適化なし（`dev`）**。同じシーン・同じ操作で、ギャラリー 38.0 → 8.5 ms・グラフ 41.5 → 9.2 ms（release）、
   `develop`（opt-level 1）でも 8.7・9.7 ms で 59.4〜59.6 fps。ナビゲーションは dev でも 59.3 fps だが 90% が 15.3 ms で余裕が無い。
   場所: `editor/src/Android/Steps/NativeBuildStep.cs`（`cargo ndk … build`、`AndroidRunRequest.OptimizesNative` のときだけ `--release`）。PC の Play は既に
   `[profile.develop]`（ルートの Cargo.toml）が既定。**直し方の案**: 開発用の APK の既定を `--profile develop` にする（ネイティブのデバッグ用に `dev` を選べる
   指定を残す。指紋 `AndroidStepFingerprints.Native` にプロファイルを入れる）。cargo-ndk 4.1.2 は `--profile develop` の .so を jniLibs へ写した（確認済み）。
   見込み: ギャラリー・グラフで 24〜26 fps → 59.4〜59.6 fps（develop の実測）。初回の develop のビルドは依存を含めて 6 分 21 秒（2 回目以降の差分は未計測）。
2. **「描画/UI 描画順の統合・GPU 積み込み」**（`app/frame_renderer.rs` の 5002 行付近 → `renderer/ui_draw_pass.rs` の `UiZoneDraw::build`）。毎フレーム、
   見えている全テキストのレイアウトとグリフの四角形（`CanvasTextRenderer::build_grouped` → `append_item` → `resolve_layout_with_images`）、新しい頂点・添字の
   GPU バッファ（`font/mod.rs` の `build_gpu_batch` の `create_buffer_init`）、全 `SEED.Draw` の三角形分割（`Primitive2dRenderer::push`）を作り直す。
   平均: dev でギャラリー 12.8 ms（34%）・グラフ 30.4 ms（73%）、release で 1.39 ms・3.72 ms（グラフのフレームの 40%）。グラフを隠すと 0.39 ms（GPU も 7.6 → 5.4 ms）
   ＝グラフ 5 つ（`SEED.Draw`・目盛りの文字・枠のスプライト）で約 3.3 ms。**主にスクロールの最初の十数フレームに山**（release 12.9 ms・develop 19.8 ms・dev 209 ms）があり、
   初めて見える文字のグリフのラスタライズと見られる（推論。区間の中を分けて測っていない）。C# 側もグラフは毎フレーム全部の図形を積み直す（release で棒グラフ 3 つ
   0.45〜0.80 ms。隠しても減らない）。**直し方の案**: (a) テキストのレイアウトと局所座標のグリフの四角形を、文字列・書体・大きさ・折り返しの幅が変わるまで覚える、
   (b) 頂点・添字のバッファを使い回す（大きくなるときだけ作り直し `write_buffer`。スプライトの `InstanceStream` と同じ形）、(c) `SEED.Draw` に保持型の描画
   （データ・表示範囲が変わるまで三角形分割を覚える）を足し、グラフはパン・ズーム・データの変化のときだけ積み直す。隠れたグラフは積まない、(d) よく使う文字
   （数字・時刻）のグリフをテーマの読み込み時に先に焼く。見込み: グラフのシーンで release 約 −3 ms/フレーム（隠したときの実測の差）、最初の山の縮小（推論）。
3. **変化に関係なく毎フレーム 2D の木全体をたどる処理**（backlog の既存の項目「2D の全ノードを毎フレームたどる…」）。「物理/2D 同期」「UI/2D スクリーン座標収集」
   「UI/ポインタイベント」「UI/ジェスチャー」「描画/スプライト収集・ソート」「エディタ状態収集」の合計が dev でギャラリー 13.2 ms（35%）・ナビゲーション 6.8 ms（51%）、
   release で 2.1 ms（25%）・1.9 ms（26%）。見本のシーンには 2D 物理のボディが無いのに `physics2d_ops.rs` の `update_physics_2d` が全 2D アクタの文脈の表を
   毎フレーム作り、Android の Play でもエディタ状態の収集が走る。**直し方の案**: 2D 物理のボディ・コライダーが無ければ 2D 同期を飛ばす、描画の表と同じ走査の結果を
   使い回す、木・レイアウトが変わったときだけポインタの表を作り直す、エディタにつながっていなければエディタ状態を集めない。見込み: release で約 −1.5〜2 ms/フレーム（推論）。

- 次に大きい固定分: **「描画/Submit・Present」1.4〜2.5 ms**（どの .so でも同じ＝wgpu・ドライバ側。release のナビゲーションで最大の区間）と
  「描画/BeginFrame(スワップチェーン取得)」の山（最大 7.4 ms）。

**そのほかの発見**

- **画面の組み立ての見本（`ui_navigation.scene`）が実機で真っ黒**: `[Script] Instantiate 失敗 (assets://ui/prefabs/screen_frame.actor): IO error: No such file or directory`。
  `ScreenStack` の既定の枠のプレハブが pak の収録に入らない（backlog の既存の項目「SEED.UI の部品が既定で読むプレハブがパッケージに入らない」を実機で確認）。
  試験ではプロジェクトのスクリプトにパスを書いて収録させて避けた。ダイアログ・シート・覆い・トーストのプレハブはギャラリーのシーンが欄に書いていたので同じ pak に入った。
- **ギャラリー・グラフの見本は 540×1200 dp 固定**で、Pixel 6a（1080×2400 px・2.625 倍＝411×914 dp）では右の約 130 dp（テーマの帯の 4 つ目のボタン・「ゆっくり」・
  数値欄・グラフの右端）が切れ、上の帯・見出しがステータスバーに重なる（安全領域を見ていない）。ナビゲーションの見本は画面に合って収まる。
- 利用者がギャラリーで触った範囲（手触りの評価ではない）: ダイアログ 3 回・下のシート・上の覆いが開いて閉じた（`[UI] modal: … close …`）。根での戻るジェスチャーで
  `CoreBackPreview startBackNavigation` → `onBackNavigationDone backType=4 triggerBack=true` → `[UI] back: move_task_to_back` → `ActivityTaskManager: moveTaskToBack`
  → `[SEED LIFECYCLE] background … suspended` → 2.5 秒後にランチャーから開き直して**同じ pid（1664）で `resumed: サーフェスを再生成しました`**（HOT）。
  backType 4 はアプリの戻るの受け口（callback）へ渡す種類（Android の BackNavigationInfo の値は記憶による）で、予測型の戻るのアニメーションの見た目は確かめていない。
  画面の右端（x=1,032〜1,040 px など）のタップで戻るの取り扱いが始まって取り消される（`triggerBack=false`）ことが 14 回あったが、アプリへの指は奪われていない
  （`edge-swipe is stealing input gesture` は本当の戻る・ホームのジェスチャーの 3 回だけ）。
- 利用者は計測の合間にグラフの見本でも 2 本指のピンチを試した（手触りの感想は聞いていない）: release の .so で `[UI] chart: pinch end zoom=` が 1.053 → 1.406 → 4.84 →
  **6（上限で止まる）**、すぼめて **1 で止まる**（別のグラフでも 2.027 → 4.035 → 1.719 → 1）。ペナルティ履歴の棒のタップで吹き出し（`bar select index=114 … "1/21 0 コイン・0 円"`）。
  dev の .so（19〜24 fps）では 4 回のピンチで倍率がほとんど動かなかった（1 → 1.111）。指の中点の値が固定されるか・縦のスクロールとの取り合いは確かめていない。
- 「端末」を選んだときの `app.ui_mode` の問い合わせは実機で `dark` を返した（`[SEED.UI] 端末の明暗: dark`・`mode System → Dark（端末 Dark）`）。
- Android 17 は前面のアプリを `adb install -r` で入れ替えると SystemUI の `PackageUpdateActivity` がアプリを起動し直す（試験の手順では入れた直後に 1 回起動する）。

**未実施**（手触りの確認を止めたため。release の .so の APK を端末に入れたまま＝次の回はそのまま使える）: スクロールの慣性・跳ね返りの手触りの感想と行のスワイプ、
時刻ホイールの回し心地・循環・触感、戻るの段（ダイアログ → シート → 画面 → 根）の順とナビゲーションの見本での戻る、グラフのピンチの手触り
（中点の固定・縦のスクロールとの取り合い。倍率の範囲と吹き出しは上のとおりログで確認）、
端末のダークモードの切り替えの追従（`ui_mode_changed`）、M7（通知の取り消しの後の `permission_changed`）。端末の設定は利用者が何も変えていない
（ダークモード オン・タップ時のバイブ オフ〈0〉・試験のアプリの通知の許可なし）。

証拠（私物端末のログなのでリポジトリに入れていない）: 作業フォルダ `tmp/w2_dev/` の `logcat_full.txt`（全体）・`perf/<dbg2|dev|rel>_<gallery|nav|charts>/`
（`report.txt`・`profile_dump.json`・`logcat.txt`・`idle.png`・`swipe_end.png`）・`perf/rel_charts_none2/`・`shots/`。駆動と集計は `perf_drive.py`・`perf_report.py`。

## 4. W1・W2 にまたがる要件

| # | 要件 | 理由・根拠 | どこで |
|---|---|---|---|
| X-1 | **冷えた起動を速く・止まらなくする**（起動の初期化と .NET の展開を android_main から外し、ANR を避ける） | 目覚ましでアプリが起きる瞬間は、端末がスリープから戻った直後で負荷が高い。今は初回の展開（0.5〜0.8 秒）・CLR の起動・GPU とシーンの初期化が同期で走り、APK の更新直後に 44 秒かかって ANR の後に落ちた記録がある（backlog「起動の初期化が android_main スレッドで同期に走る」「同梱 .NET の展開と CLR の起動が android_main で同期に走る」）。音は W1 の別プロセスが鳴らすので止まらないが、鳴動画面が出ない | W1 の受け入れ基準 AC-12 の前提。既存の backlog 項目を W1 と同時に進める |
| X-2 | **描かなくてよいときは描かない**（W2-P7） | アプリの画面はほとんど止まっている。今は前面で毎フレーム描き続け、UI と提示だけで Pixel 6a の GPU 約 4.5 ms を毎フレーム使う | W2-10a（**済** 2026-09-28。「描く理由」の API と判定。プロジェクト設定 `render_policy: "on_demand"` で有効。docs/redraw_policy.md）・W2-10（通しで詰める） |
| X-3 | **アプリとしての既定**: ステータスバーを表示、`appCategory` を選べる、戻るの最上位は閉じずに背面へ | 今のテンプレートはゲーム向けの固定（システムバーを常に隠す・`appCategory="game"`・閉じる API が無い） | W1-2（起動時のシステムバーの既定 `system_bars` と `app_category`。**済** 2026-09-27・実機は未確認）・W1-6（実行中の切り替え `Window.SetSystemBarsVisible` と戻るの最上位の `App.MoveTaskToBack`。**済** 2026-09-27・実機は未確認。**バーの文字色は持ち越し**〈backlog〉。戻るの段〈覆い → 画面のスタック → 最上位で背面へ〉は W2-P4 = W2-7 の `BackDispatcher`〈**済** 2026-09-28・実機は未確認〉） |
| X-4 | **保存の耐久性**（§2.7） | お金と履歴 | W1-S（**済** 2026-09-27） |
| X-5 | **デスクトップで作り込める** | 画面づくりの大半はエディタの Play。`SEED.Platform` の模擬（W1-P7）と縦長の Play ウィンドウ（プロジェクト設定 `window_width`/`window_height`） | W1-1・W2-1 |
| X-6 | **docs の同期** | スクリプト API の正典は `docs/scripting_api.md`（と手で同期する html）。Android の節は `docs/android.md` に足す（例 §25「アプリのプラットフォーム機能」）。プロジェクト設定のキーは `docs/project_system.md` | 各段階の完了の条件 |
| X-7 | **Android 17 の背面の音の制限への対応** | Android 17 は、見えている画面も（short service 以外の）前景サービスも無いアプリの音・音声フォーカス・音量の変更を黙って失敗させ、targetSdk 37 では背面の前景サービスに「使用中」の権能を求める。例外は「正確なアラームの権限＋`USAGE_ALARM`」（§2.6。https://developer.android.com/about/versions/17/changes/bg-audio ）。W1-0 の Android 16 の実機でも、画面の無い鳴動中に `AudioHardening … would be muted … level: full` が記録された（§2.9.1 の F-1）。エンジン自身の音（USAGE_GAME）は背面で止めている（android.md §16）ので影響しない | 鳴動（W1-4）は `USAGE_ALARM` に固定し、正確なアラームの権限を保つ。`ForceVolume`・`KeepVolume` の音量の変更と v2 の読み上げ（W5）は Android 17 の実機かエミュレータで確かめる。targetSdk を 37 に上げる前に済ませる |

## 5. 未決事項（エンジン側）

| # | 事項 | 選択肢 | 提案 |
|---|---|---|---|
| E-01 | 鳴動の置き場所 | (a) Java だけの別プロセス `:seed_platform`（§2.2） (b) 同じプロセスのまま、前景サービスがある間は `onDestroy` でプロセスを殺さない | **決定（2026-09-27 W1-0）: (a) 別プロセス**。鳴動中に鳴動画面のタスクを消しても `:seed_platform` の音は続いた（§2.9.1 の 2）。消したとき同じプロセスの Activity に `onDestroy` が来ることも観測したので、`onDestroy` で `Process.killProcess` する今の SEED で (b) にすると止まる（コードからの推論）。(b) は同じプロセスで Activity を作り直せない（winit の EventLoop は 1 回だけ）問題も残る。別プロセスの費用（冷えた状態から音まで +0.73 s、最初の呼び出しの 123 ms）は許容できる |
| E-02 | native → Java の呼び出しの手段 | (a) 今の流儀（jni クレートを足さず、JNIEnv の関数表を番号で呼ぶ）を広げる (b) `jni` クレートを入れる | **決定（2026-09-27 W1-0）**: プロセスの間は **`ContentProvider.call`**（同期・Binder。`ContentProviderClient` を持ち続けて 0.36〜0.51 ms、毎回の `ContentResolver.call` で 0.64〜0.87 ms。AIDL は 0.24〜0.26 ms だがバインドの手続きが要る）。`:seed_platform` → エンジンの知らせは **Binder のコールバック**（`call` の `Bundle.putBinder` で登録）と、前面へ戻ったときの未読の取得（放送は受け手の処理中の放送の後ろで 5 秒待たされた。§2.9.1 の F-3）。最初の呼び出しは `:seed_platform` の起動で 123 ms 同期で待つので、描画のスレッドでは呼ばない。JNI は **(b) `jni` クレート 0.22**（android-activity 0.6.1 経由で既に `Cargo.lock` にあり新しい依存が増えない。attach・GlobalRef・例外の定型を任せられる。cpal・oboe が使う 0.21 とは API の形が違う）。**Rust 側は未試作**（W1-0 は Java だけ。W1-1 の最初に `invoke` の往復を 1 本通して確かめる）。**W1-1（2026-09-27）で Rust 側を実装**: jni 0.22.4 で Android 向けのコンパイルと APK の作成まで通り、PC の模擬で往復とイベントを確かめた。実機での往復は未確認（§2.10） |
| E-03 | 鳴動の前景サービスの種類 | `mediaPlayback` / `systemExempted` | **決定（2026-09-27 W1-0）: `mediaPlayback`＋音は `USAGE_ALARM` 必須**。予約経由の起動が 3 回とも `ALARM_MANAGER_ALARM_CLOCK` で許可された（§2.9.1 の 6）。`USAGE_ALARM` は Android 17 の背面の音の制限の免除の条件（§2.6・X-7）。`systemExempted` は予備（予約経由で動作を確認。Play の申告での扱いは未確認）。`specialUse` は使わない |
| E-04 | 機能ごとのマニフェストの入れ方 | (a) 機能ごとの Gradle のライブラリモジュール（マニフェストのマージで入る） (b) SeedAndroid が生成するマニフェストの断片（生成したソースセット） | ディープリンクのように値がプロジェクトごとに違うものがあるので **(b)**（生成物は `src/seedIcon/res` と同じく追跡しない）。Java のコードは常に APK に入れ、コンポーネントの宣言だけを機能で出し入れする。**決定（2026-09-27 W1-2）: 生成物 `app/src/seedFeatures/` を AGP 9.1.0 の variant API で足す**（`androidComponents.onVariants` で `variant.sources.manifests.addStaticManifestFile("src/seedFeatures/AndroidManifest.xml")` と `variant.sources.res?.addStaticSourceDirectory("src/seedFeatures/res")`）。API は Gradle のキャッシュの `gradle-api-9.1.0.jar` を `javap` で読んで確かめ、実ビルドと aapt2 で、断片の権限が入ること・断片の MainActivity の intent-filter が main の activity に合流すること・res が独立した層になり main の既定値（`seed_system_bars_visible=false`）を上書きできることを確かめた。**`sourceSets.main` の `res.srcDir` に足すと main と同じ層になり `Duplicate resources` で止まる**（実ビルドで確認）ので「生成したソースセットを main に足す」形は退けた。ライブラリモジュール（a）は、モジュール・名前空間・include が増え、`nonTransitiveRClass` でリソースの R が分かれ、値がプロジェクトごとに違う以上は生成が要るので退けた（推論）。詳細は docs/android.md §25.10.2 |
| E-05 | W2 の分担 | Rust（ECS のコンポーネントとシステム）と C#（`SEED.UI`）の割り振り | §3.2 の W2-P1 のとおり: レイアウト・クリップ・当たり判定・ジェスチャー・IME・描画の要否は Rust、部品の振る舞いは C#、見た目はプレハブとテーマ。**W2-0 で細部を決めた（2026-09-27。§3.8.4）**: 切り抜きは描画のランの分割と scissor（Rust の描画）、IME の知らせは Java（MainActivity の上書き）→ JNI → Rust の入力欄の状態、「描く理由」の判定は Rust（部品は C# から「動いている」を申告するだけ）。**W2-3（2026-09-28）: スクロールの物理は Rust**（位置がレイアウトの走査の入力のため。一覧の再利用・スワイプは C#。docs/ui_scroll_list.md §2） |
| E-06 | Android の文字入力 | (a) GameActivity の文字入力（android-activity の API を winit の外から使う） (b) Java の `EditText` を画面の上に重ねる | **決定（2026-09-27 W2-0）: (a)。操作は android-activity の API（複製した `AndroidApp` の `set_ime_editor_info` → `show_soft_input`・`hide_soft_input`・`set_text_input_state`）、知らせは MainActivity の上書き（`stateChanged`・`onEditorAction`・`onSoftwareKeyboardVisibilityChanged`・WindowInsets の IME の高さ）→ JNI**。winit 0.30 は android-activity の `TextEvent`・`TextAction` を読み捨てる（完了などのアクションを取り戻せない）ので、それには頼らない。添字は UTF-16 で届く。根拠は §3.8.1・§3.8.4、**実機での確認は 2026-09-28（§3.8.7。日本語のかな入力・変換・完了・数字のキーボード・IME の高さ・戻る）で確定**。(b) は見た目がゲームの UI と揃わない |
| E-07 | テーマのデータの形 | (a) JSON のアセット（`Assets.ReadText`） (b) `AudioDictionary` と同じ形の辞書コンポーネント（インスペクタで編集） | **(a)**（実行中の差し替え・アプリのテーマ交換・テキストでの差分管理に向く）。エディタでの編集が要るなら (b) を後から足す |
| E-08 | `SEED.UI` の置き場所 | (a) SEEDScripting（エンジンに同梱。更新が全プロジェクトに届く） (b) テンプレートとしてプロジェクトへコピー | 振る舞いは **(a)**、見た目（プレハブ・テーマの見本）は **(b)** |
| E-09 | 画面なしでスクリプトを動かすか（v2 の寝坊連絡の送信を、アプリが死んでいても行うため） | (a) :seed_platform から CLR を画面なしで起動する (b) 送信をネイティブ（Java）に寄せる | W5 で決める。Flutter 版は Dart のバックグラウンド isolate で送っていた |
| E-10 | Direct Boot（再起動後・ロック解除前の鳴動） | やる / やらない | **決定（2026-09-27 W1-0）: W1-9 を維持**（夜中の自動更新の再起動で朝に鳴らないのは目覚ましとして致命的。1〜2 段階）。W1-0 で、exported=false・directBootAware の `BootReceiver` に `LOCKED_BOOT_COMPLETED` が届き、端末保護ストレージの控えから張り直せた（強制停止からの復帰で観測。再起動そのものでは未確認）。`BootReceiver` から直接鳴らさず、**必ず `setAlarmClock` を張り直す**（Android 15+ は `BOOT_COMPLETED` から `mediaPlayback` を起こせず、Android 17 は `BOOT_COMPLETED` から起こした前景サービスの音を抑える。§2.6）。ロック解除前は、資格情報で暗号化された保存先（セーブ・`files/dotnet`）が読めずエンジンの画面を出せない見込みなので、**Java だけの鳴動画面か、音と通知だけ**にする（エンジンの画面は解除の後。推論。再起動の試験で確かめる） |

## 6. 段階と見積もりのまとめ

| 段階 | 規模の目安 | エージェント作業 | 上振れしやすい所 |
|---|---|---|---|
| W1 | 15〜20 段階（＋任意の W1-8・W1-9 で 2〜3）。**2026-09-27 に完了**（W1-0〜W1-8・W1-S・W1-7。任意の W1-9 は残す。§6.1 の最後） | 3〜4 日＋実機での待ち時間 → 実績は 1 日（実機の待ち時間を含む） | 実時間を待つ試験（Doze・再起動・60 分）、起動の速さ（X-1）、Android 17 対応（X-7）。別プロセスの構成は W1-0 で成り立つことを確かめた |
| W2 | 22〜32 段階 | 5〜8 日 | IME（Android は winit の外の経路）、クリップとレイアウトの土台（重複の整理）、グラフの操作感 |

- **段階の単位**: Android 対応の段階0〜D-4 の実績（2026-09-24 17:00 〜 09-26 07:30 の約 38 時間にコミット 15 本前後。1 段階が 1〜2 時間の作業＋実機確認。`git log`）を 1 段階の大きさとした。
- W1 と W2 は**並べて進められる**（触る場所が違う: W1 は Java・JNI・マニフェスト・SaveData、W2 はキャンバス・入力・フォント・描画）。ただし同じ作業ツリーで並行して書き込まない（エージェントを並べるなら別の worktree）。
- アプリ（W3）は、W1-1〜W1-4 と W2-1〜W2-4・W2-7 ができた時点で主要な画面に取りかかれる。純粋ロジックと保存（アプリ仕様 §8 の W3-D・W3-S）は今すぐ始められる。

### 6.1 W1 の最初の一歩（W1-0。2026-09-27 に実施、結果は §2.9.1）

**W1-0 のスパイク**（1〜2 段階）。API を作る前に、いちばん大きな前提（別プロセスの鳴動と、フルスクリーン通知からの冷えた起動）を実機で確かめた。C# や Rust を一行も変えずに adb だけで確かめられ、崩れたときに捨てる量が最小になるため。

1. 当初の案（`runtime/android/app` に試験用のパッケージを足す）はやめ、SEED 本体を変えない**別の使い捨てアプリ**（`runtime/android/spikes/platform_spike/`、applicationId `com.seedengine.platformspike`）にした。本体のマニフェストや `MainActivity.onDestroy` の振る舞いに引きずられずに骨格だけを測るため。予約・停止・計測はデバッグ用の受信機へ `adb shell am broadcast -n …` で命じた（`-n` か `-p` が要る。§2.6）。
2. Pixel 6a（Android 16）で、(a) 画面オフ＋ロック中に時刻どおり鳴りロック画面の上に鳴動画面が出るまで（Java だけの Activity で +0.95 s）、(b) 最近のタスクから消しても鳴り続ける、(e) プロセス間の往復時間、に加えて、強制停止の後・前景サービスの種類を測った。(c) Doze は利用者の操作で無効、(d) 再起動は許可待ちで未実施。エミュレータ（API 34）では測っていない（この回はエミュレータを使わない条件だった）。
3. 結果で E-01（別プロセス）・E-02（`ContentProvider.call`＋Binder のコールバック＋`jni` クレート 0.22）・E-03（`mediaPlayback`＋`USAGE_ALARM`）・E-10（W1-9 を維持）を決めた（§5）。
4. **残り**: Doze の再試験と、再起動・Direct Boot の試験（利用者の許可待ち）は W1-0 の残りとして行う。実 GameActivity の冷えた起動の計測は W1-4 の頭へ移した（§2.10）。
5. **次の一歩**: W1-1（橋渡し）。最初に `jni` クレート 0.22 で `SeedPlatform.invoke` の往復を 1 本通し、Rust 側の試作が無い E-02 の JNI の部分を確かめる。
   → 2026-09-27 に W1-1 を実装（§2.10）。残りは実機での往復・呼び鈴の確認（docs/android.md §25.7 の手順）。
   → 同日に W1-2（機能の opt-in。docs/android.md §25.10）を実装。次は W1-3（目覚ましの予約。機能の表の alarm に受信機の行を足す）。
   → 同日に W1-3（目覚ましの予約。docs/android.md §25.11）を実装。次は W1-4（鳴動。頭で実 GameActivity の冷えた起動を測る）。
   → 同日に W1-4a（鳴動・PlatformEntry・起動理由の実装。実機の計測を除く。docs/android.md §25.12）を実装。次は W1-4b（端末が USB に戻ったら、
     §25.12.8 の手順で鳴動と実 GameActivity の冷えた起動を実機で測る）。
   → 同日に W1-5（通知と権限。docs/android.md §25.13・§25.14）と W1-6（画面とアプリ・触感・ディープリンク。§25.15）を実装。W1-6 の日に初めて実機（Pixel 6a）で
     PlatformSmoke を通し、W1-1〜W1-6 の流れ（接続・往復・予約と発火・3 秒の鳴動・権限の要求）が動いた（§25.15.10）。次は W1-S（保存の耐久性）と
     W1-7（通しの確認）。W1-4b（冷えた起動の計測・最近のタスクから消しても鳴るか等）は端末で残っている。
   → 同日に W1-4b（実機の計測。§2.9.2）を行った: 冷えた起動は音 +0.26 s・エンジンの最初のフレーム +2.03 s（AC-1・AC-12 を満たす）、Doze の下でも
     時刻どおり（W1-0 の残りの Doze は済）、タスクを消しても鳴り続ける、`:seed_platform` の死で黙って止まる（設計案）。再起動と Direct Boot は見送り（手順は §2.9.2）。
   → 同日に W1-S（保存の耐久性。§2.7）を実装: sync と rename だけの置き換え・1 世代前（.bak）からの復旧・`SaveData.RecoveredFrom`・`SaveData.Batch`・
     大きな文字列の FFI（`Utf8Arg`）。次は W1-7（通しの確認。AC-10 の実機の `kill -9` の繰り返しを含む）。
   → 同日に W1-8（任意。センサー）と W1-7（通しの確認。§2.8・§2.9.2 の「W1-7 の実機の結果」）を行い、**W1 を終えた（2026-09-27）**。W1-7 では
     再起動と Direct Boot の試験（T4）と、見つけた不具合の修正（鳴動の復元・音量の控え・継ぎ目の無いループ・起動の放送の重なり・デバッグ受信機の保護・
     SaveData の書き出しの隙間）を行った。手作業の要る確認（通知のボタン・権限の画面・電源ボタン・戻る／ホーム／スワイプ・通知からアプリへ戻る）と
     リリース版での確認は §2.8 の表の「未実施」のとおり backlog に残した。**W1-9（任意。Direct Boot）は任意のまま**: 解除前の張り直しと音・通知は
     T4 で動いた。残るのは解除前の鳴動画面（`MainActivity` は directBootAware でない）で、Wake or Pay の v1 に要るかはアプリの仕様で決める。

## 7. backlog との対応

未着手・保留の課題は [backlog.md](backlog.md) の「アプリ基盤（W1 Android サービス層 / W2 UI 部品群）」節に置く。W0 の調査で見つかった既存の不具合・制限（SaveData の置き換えの隙間・リリース版で起動理由が取れない・権限とコンポーネントを足す仕組みが無い・ゲーム向けの固定の既定）もそこに書いた。既存の Android 節の関連項目（起動の初期化の同期・アプリを終える API が無い・安全領域の自動反映・縦画面の自動スケール・ジェスチャー・指0 だけのポインタイベント）は、そちらを正とし、この文書からは参照だけする。
