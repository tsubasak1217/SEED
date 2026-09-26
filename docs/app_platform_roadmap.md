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
| `SaveData` | キー＝値（int・long・float・string・bool）を JSON 1 ファイルに。背面へ回るとき・Activity の破棄で自動保存 | §2.7 の耐久性の穴 |
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

### 2.2 構成（案。W1-0 のスパイクで確かめる）

```
[メインプロセス（今の APK）]                                   [:seed_platform プロセス（Java だけ・新規）]
 C# SEED.Platform.*                                              PlatformProvider（ContentProvider。exported=false）
   └ host_api（新カテゴリ）                                       ├ call("alarm.schedule" …) など同期の命令と問い合わせ
       └ Rust engine::platform::bridge                           │    （別プロセスへの同期呼び出し。Binder）
           ├ invoke → JNI → SeedPlatform.invoke (Java)  ─call→   ├ AlarmStore … 予約の控え（files/seed_platform/alarms.json。原子的に書く）
           └ イベントの受け口 ← nativeOnPlatformEvent            ├ EventJournal … 発火・停止・通知の操作の記録（未読をエンジンが取りに来る）
 MainActivity（GameActivity）                                    AlarmScheduler … AlarmManager.setAlarmClock（Doze でも時刻どおり）
   ├ onCreate: 起動 Intent が信頼できる目覚ましなら             AlarmReceiver（exported=false）… 発火 → RingService を前景で起動
   │           showWhenLocked / turnScreenOn を上げる             RingService（前景サービス）
   ├ onNewIntent / 起動 Intent → 起動理由 → nativeOnPlatformEvent │  ├ MediaPlayer（AudioAttributes USAGE_ALARM・ループ）・バイブ・PARTIAL_WAKE_LOCK
   ├ onRequestPermissionsResult → nativeOnPlatformEvent           │  ├ フルスクリーン通知（→ PlatformEntry）＋鳴動中の常駐通知
   └ 実行時の受信機（RECEIVER_NOT_EXPORTED）… 「新しい記録あり」   │  ├ 音声フォーカスを失っても止めない
                                                                  │  └ 安全弁（既定 60 分）で自動停止し記録する
 activity-alias PlatformEntry（exported=false → MainActivity）   BootReceiver … BOOT_COMPLETED / MY_PACKAGE_REPLACED / TIME_SET /
   … プラットフォーム層の PendingIntent だけがここを通る                          TIMEZONE_CHANGED / EXACT_ALARM 権限の変化で予約を張り直す
```

- **鳴動の流れ**: `AlarmManager.setAlarmClock` → `AlarmReceiver`（:seed_platform）→ `RingService` を前景で起動（正確なアラームの受信は、背面からの前景サービス起動の制限の例外）→ 音・バイブ・WakeLock → フルスクリーン通知（`PlatformEntry` 行きの PendingIntent。extras に予定時刻・予約 ID・payload）→ 端末がロック中・画面オフなら MainActivity が起動し、使用中なら通知がヘッドアップで出る（Android の仕様）→ エンジンが起動理由を読み、スクリプトが鳴動画面を出す → 解除・スヌーズで `Alarms.StopRinging(id)`。
- **エンジンが生きているときの知らせ**: :seed_platform は `sendBroadcast`（自パッケージ宛て）で「記録あり」を知らせ、MainActivity が実行時に登録した受信機が受けて native へ伝え、native が `PlatformProvider` から未読の記録を取る。エンジンが居なければ何も起きない（プロセスを起こさない）。前面へ戻ったとき（onResume）にも未読を取る。
- **音源**: 予約のときに、スクリプトが渡した `assets://` の音をエンジン側が `files/seed_platform/sounds/<内容のハッシュ>.<拡張子>` へ書き出し、絶対パスを予約の控えに入れる（pak は :seed_platform から読めないため）。読めなければ **モジュールに同梱した既定の音**で鳴らす（無音にしない。Flutter 版も未知の音は bell に落とした）。
- **既存の音声との関係**: エンジンの音声は前面で `AUDIOFOCUS_GAIN`（USAGE_GAME）を要求する（`AudioFocusController.java`、android.md §16.3）。鳴動画面が前に出るとエンジンがフォーカスを取り、鳴動側は失う。**鳴動側は音声フォーカスの喪失で止まらない**ことを必須にする（止めると鳴動画面を開いた瞬間に鳴り止む）。

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
| 再起動直後（ロック解除前） | 非対応（最初のロック解除まで戻らない。記憶・要確認） | 予約の控えと既定の音を**端末保護ストレージ**に置き、受信機・鳴動サービスを `directBootAware` にすれば、夜中の自動更新の再起動の後でも鳴らせる（W1-9。記憶に基づく案・要確認） |

> 上の構成は W1-0 のスパイク（Pixel 6a・Android 16）で次を確かめてから確定する: (1) 別プロセスの前景サービスが、メインプロセスを最近のタスクから消しても鳴り続ける、(2) フルスクリーン通知から GameActivity をロック画面の上に冷えた状態で起動できる、(3) `ContentResolver.call` の往復が 10 ms 以下、(4) `adb shell dumpsys deviceidle force-idle` の下で予定時刻どおりに鳴る、(5) 再起動後に張り直される。どれかが崩れたら、同一プロセス案（onDestroy でプロセスを殺さない条件の追加など）を比べ直す。

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
    public static void   CancelAll();
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
    public static void EnsureChannel(string channelId, string name, NotificationImportance importance, string description);
    public static bool Show(NotificationRequest request);
    public static void Cancel(string id);
    public static bool AreEnabled { get; }            // アプリの通知が端末で有効か
}

// ── 権限 ─────────────────────────────────────────────────
public enum PermissionKind { PostNotifications, ExactAlarm, FullScreenIntent, /* v2: */ RecordAudio, SendSms }
public enum PermissionStatus { Granted, Denied, DeniedPermanently, NeedsSettings, NotApplicable }
public static class Permissions
{
    public static PermissionStatus Check(PermissionKind kind);
    public static int  Request(PermissionKind kind);   // 実行時ダイアログ。要求 ID を返し、結果は PermissionResult { RequestId, Kind, Status }
    public static void OpenSettings(PermissionKind kind); // 特別なアクセスの画面（正確なアラーム・フルスクリーン通知）／アプリの通知設定
}
// 前面へ戻ったとき（設定画面から帰ってきた）に状態が変わっていれば PermissionChanged { Kind, Status }

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
- **v2（W5）で足すもの**: 読み上げ（TTS。鳴動音の上に重ねる）、録音（AAC/m4a・マイク権限）、センサー（重力を除いた加速度。アプリの「振る」確認に要る。**v1 に要るかはアプリ仕様 §10 U-04 で決める**）、安全な保存（Android Keystore）、ネットワークの権限を配布版へ入れる設定、ファイルの選択（SAF。音源の取り込み）。

### 2.4 JNI の流儀（今の決まりと、W1 で足す部分）

| 項目 | 今の決まり（段階0〜D） | W1 で足す部分 |
|---|---|---|
| Java→native | `private static native void nativeXxx(...)` を `Java_com_seedengine_runtime_<Class>_<method>` で結ぶ。RegisterNatives も jni クレートも使わない。引数は数値か UTF-8 の byte[]（JSON）。受けた値は engine の `platform::*` の置き場（Mutex・原子変数）へ入れるだけで、エンジンはイベントループの周回・フレームで読む（jni_exports.rs 冒頭、android.md §4.3・§4.4） | `nativeOnPlatformEvent(byte[] json)` の 1 本。イベントはキュー（上限つき）へ積み、フレームの頭でスクリプトへ配る |
| native→Java | **無い**（まだ 1 か所も呼んでいない） | `SeedPlatform.invoke(String module, String method, byte[] json): byte[]` の 1 本。ネイティブのスレッドから `FindClass` するとアプリのクラスが見えない（システムのクラスローダーになる）ので、**起動時に Java から `nativeRegisterPlatformBridge(Class)` で自分のクラスを渡し、GlobalRef で持つ**。JavaVM は `AndroidApp::vm_as_ptr()`（winit 経由の android-activity）から取り、呼ぶスレッドを attach する |
| 呼び出しの手段 | JNIEnv の関数表を番号で呼ぶ（`jni_env.rs`。4 関数だけ） | 必要な関数が 10 前後に増える（NewGlobalRef・GetStaticMethodID・CallStaticObjectMethod・NewByteArray・SetByteArrayRegion・DeleteLocalRef・AttachCurrentThread など）。**今の流儀を延長するか、jni クレートを入れるかを W1-0 で決める**（§5 の未決 E-02） |
| UI スレッド | Java 側の各クラスは UI スレッド専用 | ウィンドウ操作（showWhenLocked・システムバー）は `runOnUiThread`。`invoke` は呼び出し元のスレッドで同期に動き、長い処理はしない（Binder の往復 1 回程度） |
| エラー | panic は JNI の境界で受け止め、ログは liblog へ直接 | `invoke` は例外を投げず `{ "ok": false, "error": "…" }` を返す。スクリプトには bool と `Platform.LastError` で見せる |

### 2.5 プロジェクト設定とマニフェスト

`project_settings.json` の `android` 節に足すキー（エディタのモデルは `editor/src/ProjectSettings/AndroidAppSettings.cs`。今も知らないキーは `ExtraData` で保たれる）:

| キー | 例 | マニフェスト・Gradle への反映 |
|---|---|---|
| `features` | `["alarm", "notifications"]` | SeedAndroid が `-Pseed.features=alarm,notifications` を渡し、`build.gradle.kts` が該当する**機能ごとのマニフェスト断片**（Gradle のライブラリモジュール、または生成したソースセット）を加える。マニフェストのマージで MainActivity への intent-filter・権限・サービス・受信機が入る |
| `deep_links` | `[{ "scheme": "…", "host": "…" }]` | 生成したマニフェスト断片の MainActivity の intent-filter |
| `system_bars` | `"visible"`（アプリ）／`"hidden"`（既定。今のゲームの振る舞い） | MainActivity の起動時の既定（スクリプトから `Window.SetSystemBarsVisible` で変えられる） |
| `app_category` | `"productivity"` | `android:appCategory`（今は `game` 固定。AndroidManifest.xml） |

機能ごとに入るもの（案）:

| 機能 | 権限 | コンポーネント |
|---|---|---|
| `alarm` | `USE_EXACT_ALARM`、`SCHEDULE_EXACT_ALARM`（maxSdkVersion 32）、`RECEIVE_BOOT_COMPLETED`、`WAKE_LOCK`、`VIBRATE`、`USE_FULL_SCREEN_INTENT`、`FOREGROUND_SERVICE`、`FOREGROUND_SERVICE_MEDIA_PLAYBACK`（または `_SYSTEM_EXEMPTED`。§2.6）、`POST_NOTIFICATIONS` | `PlatformProvider`・`AlarmReceiver`・`BootReceiver`・`RingService`（`android:process=":seed_platform"`）、`activity-alias PlatformEntry` |
| `notifications` | `POST_NOTIFICATIONS` | （通知のボタンは `PlatformEntry` 行きの PendingIntent。Android 12+ はトランポリン禁止なので受信機を挟まない） |
| `deep_links` | — | intent-filter |

`SeedAndroid` の Google Play の要件チェック（`editor/src/Android/Release/`、`runtime/android/play_requirements.json`。android.md §24.8）に、「宣言した機能と権限が一致しているか」「前景サービスの種類が宣言されているか」「`USE_EXACT_ALARM` を使うなら目覚まし・カレンダーのアプリとして申告が要る」の注意を足す。

### 2.6 Google Play と Android の制約（2026-09-27 に公式のページで確かめたものは「確認済み」）

| 項目 | 内容 | 状態 |
|---|---|---|
| 正確なアラームの権限 | Android 12（API 31–32）は `SCHEDULE_EXACT_ALARM`（ユーザーが許可・取り消し可）。Android 13+ は `USE_EXACT_ALARM`（インストール時に自動で許可・取り消せない）か `SCHEDULE_EXACT_ALARM`。Android 14+ の新規インストールでは `SCHEDULE_EXACT_ALARM` は**既定で拒否**。`setExact` / `setExactAndAllowWhileIdle` / `setAlarmClock` はどれもこの権限が要る | 確認済み（developer.android.com「Schedule alarms」「Schedule exact alarms are denied by default」） |
| `USE_EXACT_ALARM` の Play ポリシー | 目覚まし・カレンダーが中核機能のアプリだけが宣言できる（審査あり）。それ以外は公開できない | 確認済み（Play Console ヘルプ「Permissions and APIs that Access Sensitive Information」を検索結果で確認） |
| 両方を宣言する形 | `SCHEDULE_EXACT_ALARM` に `maxSdkVersion="32"` を付けて `USE_EXACT_ALARM` と並べる形は公式の文書には載っていない（コミュニティの定番） | **未確認**。W1 で lint と Play Console の事前審査で確かめる |
| 権限の取り消し | `SCHEDULE_EXACT_ALARM` が取り消されるとアプリは止められ、以後の正確なアラームは全部取り消される。`ACTION_SCHEDULE_EXACT_ALARM_PERMISSION_STATE_CHANGED` を受けて張り直す | 確認済み |
| 再起動 | すべてのアラームは電源断で消える。`RECEIVE_BOOT_COMPLETED` の受信機で張り直す | 確認済み |
| 強制停止 | 公式の文書には明記が無いが、一般に強制停止（設定の「強制停止」・`am force-stop`）で予約は消え、ユーザーがアプリを開くまで戻らない。**起動のたびに全予約を張り直す**ことで補う（Flutter 版も起動時に `rescheduleAll`） | 推測（一般的な挙動。W1 で `dumpsys alarm` で確かめる） |
| `setAlarmClock` と Doze | 「システムは配信時刻を調整しない。最も重要なアラームとして扱い、必要なら低電力モードを抜けて配信する」 | 確認済み |
| フルスクリーン通知 | Android 14+ で `USE_FULL_SCREEN_INTENT` の既定の許可は、通話か目覚ましが中核のアプリだけ（Play Console での申告。2025-01-22 以降）。それ以外（**サイドロードを含む**）は既定で無効で、`NotificationManager.canUseFullScreenIntent()` で確かめて `ACTION_MANAGE_APP_USE_FULL_SCREEN_INTENT` の設定画面へ案内する。拒否されると通知から黙って外れ、音は鳴るのに画面が点かない（Flutter 版で実際に起きた） | 確認済み（Play Console ヘルプ 13392821 と検索結果）＋Flutter 版の記録（コミット `7899d17`） |
| 前景サービスの種類 | targetSdk 34+ は種類の宣言と `FOREGROUND_SERVICE_<種類>` の権限が必須。Play Console で種類ごとに「機能の説明・遅延／中断されたときの影響・動画」を申告する | 確認済み（Play Console ヘルプ 13392821） |
| 種類の候補 | `mediaPlayback`（背面での音声の再生。Android 15+ は BOOT_COMPLETED から起動できない＝鳴動では無関係）、`systemExempted`（**`SCHEDULE_EXACT_ALARM` か `USE_EXACT_ALARM` を持つアプリは使える**。そうでなければ例外）、`specialUse`（他に当てはまらない用途。理由を書いて審査。Flutter 版のスヌーズの常駐サービスが使っていた） | 確認済み（developer.android.com「Foreground service types」） |
| 推奨 | 鳴動は `mediaPlayback` を第一候補（申告の用途が明快）、`systemExempted` を比較。**`specialUse` は使わない**（スヌーズ中の通知は前景サービスにせず普通の通知にする。アプリ仕様 §10 U-11） | 提案 |
| 通知の権限 | Android 13+ は `POST_NOTIFICATIONS` の実行時許可が要る | 一般的な知識（確認は W1） |
| 通知のトランポリン | Android 12+ は通知（本文・ボタン）の PendingIntent から受信機・サービスを経由して Activity を起動できない。Activity を直接起動する PendingIntent にする | 一般的な知識（確認は W1） |
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

### 2.8 受け入れ基準（W1 の完了の条件）

実機（Pixel 6a・Android 16）とエミュレータ（API 34・35・36）で確かめる。Flutter 版の「実機で確認すべきこと」（`wake-or-pay:README.md:1166-1190`）を引き継いだ。

| # | 基準 |
|---|---|
| AC-1 | 画面オフ・ロック中に予約した時刻（±5 秒）に鳴り、**ロック画面の上**にエンジンの画面が出る。`dumpsys deviceidle force-idle` の下でも同じ |
| AC-2 | 鳴動中に最近のタスクからアプリを消しても**音が続き**、通知からアプリへ戻れる |
| AC-3 | 戻る・ホーム・通知のスワイプでは止まらない。`StopRinging` でだけ止まる。`MaxRingMinutes` で自動で止まり、`AlarmRingStopped(Timeout)` が次の起動で届く |
| AC-4 | `adb reboot` の後も予約が残り（`dumpsys alarm`）、時刻どおりに鳴る。端末の時刻・タイムゾーンを変えても、アプリが渡した UTC の時刻で鳴る（壁時計の再計算はアプリが `AlarmsRescheduled(TimeChanged)` を受けて行う） |
| AC-5 | 鳴っていないときは、アプリを開いたまま画面を消して電源ボタンを押すと**ロック画面が出る**（アプリが上に出ない） |
| AC-6 | 目覚ましで起動したとき、**リリース版でも** `App.LaunchReason` が `Alarm`・予約 ID・予定時刻・payload を返す。`adb shell am start -n <applicationId>/com.seedengine.runtime.MainActivity --es …` で偽装しても `Alarm` にならない |
| AC-7 | 通知のボタンを押すと、アプリが死んでいても起動し `NotificationAction`（ボタンの ID・payload）が取れる。生きていれば `Intent` イベントで届く |
| AC-8 | 通知の実行時権限を求めて結果イベントが届く。正確なアラーム・フルスクリーン通知の状態が取れ、設定画面を開いて戻ると `PermissionChanged` が届く。`appops` で拒否した状態でも落ちない |
| AC-9 | エンジンを意図的に落としても（デバッグ命令で panic）鳴り続ける |
| AC-10 | `SaveData.Save()` の直後にプロセスを `kill -9`（デバッグ版の `run-as`）する試験を 100 回繰り返しても save.json が消えず壊れない。壊した save.json からは前の世代で起動する |
| AC-11 | `features` を書いていないプロジェクトの APK には、W1 の権限・サービスが 1 つも入らない（`aapt2 dump permissions` / `badging`） |
| AC-12 | 鳴動画面の最初のフレームが、フルスクリーン通知から 3 秒以内に出る（冷えた起動・Pixel 6a）。出るまでも音は鳴っている（前提: §4 の X-1。起動の初期化の同期を解く） |
| AC-13 | デスクトップの Play で同じスクリプトが動き（予約は模擬のタイマー、通知はログ、権限は許可）、デバッグ命令で「いま鳴った」を起こせる |
| AC-14 | `docs/scripting_api.md`（と html）・`docs/android.md` の新しい節・`docs/project_system.md` の `android` 節が更新され、Play の要件チェックが新しい権限を扱う |

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
# 最近のタスクから消す操作そのものは実機で手で行う
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
```

- 確かめ用のシーン（例 `templates/scenes/platform_probe.scene`＋スクリプト）を用意し、「1 分後に予約」「いま止める」「通知を出す」「権限を求める」をボタンで出す。結果は画面と logcat（`[SEED PLATFORM]`）に出す。
- SeedAndroid の `run` と `logcat` で回し、`--scene` で確かめ用のシーンから起動する（android.md §20.10）。
- 実時間を待つ試験（Doze・再起動・60 分の安全弁）は、`MaxRingMinutes` を短くした設定で行い、60 分は 1 回だけ通しで確かめる。
- Java の純粋な部分（予約の控えの JSON・記録の読み書き・次に張り直す対象の選び方）は JVM の単体テストで固める（Gradle の `testDebugUnitTest`）。

### 2.10 段階と見積もり

| 段階 | 内容 | 規模（段階A の 1 段階≒1〜2 時間のエージェント作業＋実機確認、を単位にした目安） |
|---|---|---|
| W1-0 | スパイク: 別プロセスの前景サービス・フルスクリーン通知から GameActivity・`ContentResolver.call`・Doze・再起動（§2.2 の 5 点）。JNI の手段（今の流儀の延長か jni クレートか）を決める | 1〜2 |
| W1-1 | 橋渡し: `SeedPlatform.invoke`・`nativeOnPlatformEvent`・イベントキュー・`ScriptHostApi` の新カテゴリ・C# の `SEED.Platform` の骨組み・デスクトップの模擬 | 2 |
| W1-2 | 機能の opt-in: `android.features` など新キー → SeedAndroid → Gradle → マニフェストの断片、`appCategory`・`system_bars`、Play の要件チェック | 1〜2 |
| W1-3 | 目覚まし: `AlarmStore`・`AlarmScheduler`（setAlarmClock）・`AlarmReceiver`・`BootReceiver`（時刻・タイムゾーン・更新・権限の変化）・音源の書き出し | 2 |
| W1-4 | 鳴動: `RingService`（音・バイブ・WakeLock・フォーカス喪失で止めない・安全弁）・フルスクリーン通知・`PlatformEntry`・起動理由・onCreate での showWhenLocked | 2〜3 |
| W1-5 | 通知と権限: チャネル・常駐とボタン・トランポリン無しの起動・実行時権限と結果・特別なアクセスの状態と設定画面 | 2 |
| W1-6 | 画面とアプリ: `Window.*`（動的な showWhenLocked・画面を点けたまま・システムバー）・`MoveTaskToBack`・`OpenUrl`・`Haptics`・ディープリンク | 1 |
| W1-S | 保存の耐久性（§2.7） | 1 |
| W1-7 | 通しの確認（AC-1〜14）・docs・backlog | 2 |
| （任意）W1-8 | センサー（重力を除いた加速度）。アプリの「振る」を v1 に残すなら | 0.5〜1 |
| （任意）W1-9 | Direct Boot（再起動後・ロック解除前の鳴動） | 1〜2 |

**合計の目安: 15〜20 段階相当 ≒ エージェント作業で 3〜4 日＋実機での確認の待ち時間。** 根拠: Android 対応の段階0〜D-4（2026-09-24 17:00 〜 09-26 07:30、コミット 15 本前後）は、1 段階が 1〜2 時間の作業＋実機確認で進んだ（`git log`）。W1 は 1 段階の中身がそれと同程度（Java＋Rust＋C#＋docs＋実機）で、ただし**実時間を待つ試験**（Doze・再起動・60 分）と**別プロセスという初めての構成**の分だけ上振れを見込む。

## 3. W2: UI 部品群

### 3.1 いまあるもの・無いもの（2026-09-27 に W0 でコードを読んで確かめた）

| 部品・機能 | 状態 | 根拠 |
|---|---|---|
| レイアウト（縦・横・グリッドの自動配置） | **無し**。`CanvasTransform` の位置とアンカーを 1 つずつ指定するだけ | `runtime/src/engine/components/canvas_transform.rs` |
| アンカーの伝わり方 | アンカーは親の `CanvasComponent` の領域に対する比率。**`CanvasComponent` を持たない親の子ではアンカーが効かない**。2D ノードのレイアウト計算が 5 か所に重複している | `app/canvas_collect.rs:62,106,126`、backlog「2D ノードのレイアウト計算が 5 か所に重複コピーされている」 |
| 解像度への追従 | ルートキャンバスの `auto_scale`（基準 1920×1080）。**縦画面では縦横別々の倍率が掛かり形が崩れる**。基準より大きいキャンバスは縮小しない | `canvas_component.rs:130-265`、backlog（Android 節「縦画面でキャンバスの自動スケールが縦横別々に掛かる」・「キャンバスの `auto_scale` が…縮小しない」） |
| DPI・dp | 換算は無い（`Screen.DPI` を返すだけ） | `Screen.cs:116-127` |
| 安全領域 | 値は取れる（`Screen.SafeArea`）。**キャンバスへ自動では効かない** | scripting_api §7.12、backlog「キャンバス UI へ安全領域を自動で反映する仕組みが無い」 |
| クリップ（はみ出しの切り取り・シザー） | **無し**（Draw・スプライト・テキストのどれにも） | `primitive2d/`・`ui_draw_pass.rs` |
| スクロール・慣性 | **無し**（前提のクリップも無い） | — |
| 当たり判定 | Sprite / SkinnedSprite の `raycast_target` だけ（**Text は対象外**）。最前面の 1 つに `OnPointer*`。**指0 の 1 本だけ**。押下の取り消し（スクロールが始まったら押下をやめる）は無い | `pick_2d.rs:46-49,91-96`、`pointer_events.rs`、backlog「キャンバス UI のポインタイベントは指0 の 1 本だけ」 |
| ジェスチャー | **無し**（長押し・フリック・ピンチ・ダブルタップ）。タッチに時刻・押下時間・圧力が無い | backlog「ジェスチャ…の組み込み API が無い」 |
| 文字入力・IME | **無し**（Windows も Android も）。winit の `WindowEvent::Ime` / 文字の受け取りを配線していない。Android は GameActivity の IME 処理を邪魔しないだけで、確定文字列を受け取る経路が無い | `core/input/mod.rs`（処理する `WindowEvent` に Ime が無い）、`MainActivity.java:139` |
| フォーカス・クリップボード | **無し** | grep |
| 文字 | SDF（任意サイズ）、フォント指定（組み込みは **M PLUS Rounded 1c Regular**。Flutter 版と同じ書体）、枠と折り返し（日本語は 1 文字単位＋簡易禁則）、太さ・影・縁取り、インライン画像、スロット。**スクリプトから寸法を測れない**。グリフのアトラスは 4096² で約 2,500 字、**あふれると追い出さずに描かない** | `Text.cs`、`font/atlas.rs:17-19,183-194`、`font/mod.rs:59-74` |
| 図形 | 角丸矩形・線・円弧・リング・折れ線・多角形・ベジエ。**グラデーション・9 スライス・画像の描画（Draw から）は無い**。1 フレーム 4,096 図形 | `Draw.cs`、`batch2d.rs:19` |
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
| W2-P1 | **ECS で分ける** | 状態はコンポーネント（データ）、振る舞いはシステム。**レイアウト・クリップ・当たり判定・ジェスチャーの調停・文字入力（IME）・描画の要否は Rust のシステム**（性能とエディタでの編集のため）。**部品の振る舞い（ボタンの状態・スライダ・スクロールの物理・一覧の再利用・ホイール・タブ・シート・グラフ）は C# の `SEED.UI` 名前空間**（SEEDScripting に同梱。エンジンを更新すれば全プロジェクトに届く）。見た目はプレハブ（`templates/ui/`）とテーマのデータで差し替える |
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

## 4. W1・W2 にまたがる要件

| # | 要件 | 理由・根拠 | どこで |
|---|---|---|---|
| X-1 | **冷えた起動を速く・止まらなくする**（起動の初期化と .NET の展開を android_main から外し、ANR を避ける） | 目覚ましでアプリが起きる瞬間は、端末がスリープから戻った直後で負荷が高い。今は初回の展開（0.5〜0.8 秒）・CLR の起動・GPU とシーンの初期化が同期で走り、APK の更新直後に 44 秒かかって ANR の後に落ちた記録がある（backlog「起動の初期化が android_main スレッドで同期に走る」「同梱 .NET の展開と CLR の起動が android_main で同期に走る」）。音は W1 の別プロセスが鳴らすので止まらないが、鳴動画面が出ない | W1 の受け入れ基準 AC-12 の前提。既存の backlog 項目を W1 と同時に進める |
| X-2 | **描かなくてよいときは描かない**（W2-P7） | アプリの画面はほとんど止まっている。今は前面で毎フレーム描き続け、UI と提示だけで Pixel 6a の GPU 約 4.5 ms を毎フレーム使う | W2-10 |
| X-3 | **アプリとしての既定**: ステータスバーを表示、`appCategory` を選べる、戻るの最上位は閉じずに背面へ | 今のテンプレートはゲーム向けの固定（システムバーを常に隠す・`appCategory="game"`・閉じる API が無い） | W1-2・W1-6 |
| X-4 | **保存の耐久性**（§2.7） | お金と履歴 | W1-S |
| X-5 | **デスクトップで作り込める** | 画面づくりの大半はエディタの Play。`SEED.Platform` の模擬（W1-P7）と縦長の Play ウィンドウ（プロジェクト設定 `window_width`/`window_height`） | W1-1・W2-1 |
| X-6 | **docs の同期** | スクリプト API の正典は `docs/scripting_api.md`（と手で同期する html）。Android の節は `docs/android.md` に足す（例 §25「アプリのプラットフォーム機能」）。プロジェクト設定のキーは `docs/project_system.md` | 各段階の完了の条件 |

## 5. 未決事項（エンジン側）

| # | 事項 | 選択肢 | 提案 |
|---|---|---|---|
| E-01 | 鳴動の置き場所 | (a) Java だけの別プロセス `:seed_platform`（§2.2） (b) 同じプロセスのまま、前景サービスがある間は `onDestroy` でプロセスを殺さない | **(a)**。(b) は同じプロセスで Activity を作り直せない（winit の EventLoop は 1 回だけ）ため、鳴動中に最近のタスクから消した後の再起動で詰む。W1-0 で確かめる |
| E-02 | native → Java の呼び出しの手段 | (a) 今の流儀（jni クレートを足さず、JNIEnv の関数表を番号で呼ぶ）を広げる (b) `jni` クレートを入れる | 呼ぶ関数は 10 前後で 1 本の汎用入口だけなので **(a) でも足りる**。ただし attach・GlobalRef・例外処理の定型が増えるなら (b) の方が安全。W1-0 で試作して決める |
| E-03 | 鳴動の前景サービスの種類 | `mediaPlayback` / `systemExempted` | **`mediaPlayback` を第一候補**（Flutter 版の `alarm` パッケージと同じ。Play の申告の用途が明快）。`systemExempted` は正確なアラームの権限が前提で、Play の申告でどう扱われるか未確認 |
| E-04 | 機能ごとのマニフェストの入れ方 | (a) 機能ごとの Gradle のライブラリモジュール（マニフェストのマージで入る） (b) SeedAndroid が生成するマニフェストの断片（生成したソースセット） | ディープリンクのように値がプロジェクトごとに違うものがあるので **(b)**（生成物は `src/seedIcon/res` と同じく追跡しない）。Java のコードは常に APK に入れ、コンポーネントの宣言だけを機能で出し入れする |
| E-05 | W2 の分担 | Rust（ECS のコンポーネントとシステム）と C#（`SEED.UI`）の割り振り | §3.2 の W2-P1 のとおり: レイアウト・クリップ・当たり判定・ジェスチャー・IME・描画の要否は Rust、部品の振る舞いは C#、見た目はプレハブとテーマ |
| E-06 | Android の文字入力 | (a) GameActivity の文字入力（android-activity の API を winit の外から使う） (b) Java の `EditText` を画面の上に重ねる | **(a) を W2-0 で試す**。(b) は見た目がゲームの UI と揃わない |
| E-07 | テーマのデータの形 | (a) JSON のアセット（`Assets.ReadText`） (b) `AudioDictionary` と同じ形の辞書コンポーネント（インスペクタで編集） | **(a)**（実行中の差し替え・アプリのテーマ交換・テキストでの差分管理に向く）。エディタでの編集が要るなら (b) を後から足す |
| E-08 | `SEED.UI` の置き場所 | (a) SEEDScripting（エンジンに同梱。更新が全プロジェクトに届く） (b) テンプレートとしてプロジェクトへコピー | 振る舞いは **(a)**、見た目（プレハブ・テーマの見本）は **(b)** |
| E-09 | 画面なしでスクリプトを動かすか（v2 の寝坊連絡の送信を、アプリが死んでいても行うため） | (a) :seed_platform から CLR を画面なしで起動する (b) 送信をネイティブ（Java）に寄せる | W5 で決める。Flutter 版は Dart のバックグラウンド isolate で送っていた |
| E-10 | Direct Boot（再起動後・ロック解除前の鳴動） | やる / やらない | 夜中の自動更新の再起動で朝に鳴らないのは目覚ましとして致命的なので、**W1-9 として W1 の後半に入れる**（1〜2 段階） |

## 6. 段階と見積もりのまとめ

| 段階 | 規模の目安 | エージェント作業 | 上振れしやすい所 |
|---|---|---|---|
| W1 | 15〜20 段階（＋任意の W1-8・W1-9 で 2〜3） | 3〜4 日＋実機での待ち時間 | 別プロセスという初めての構成、実時間を待つ試験（Doze・再起動・60 分）、起動の速さ（X-1） |
| W2 | 22〜32 段階 | 5〜8 日 | IME（Android は winit の外の経路）、クリップとレイアウトの土台（重複の整理）、グラフの操作感 |

- **段階の単位**: Android 対応の段階0〜D-4 の実績（2026-09-24 17:00 〜 09-26 07:30 の約 38 時間にコミット 15 本前後。1 段階が 1〜2 時間の作業＋実機確認。`git log`）を 1 段階の大きさとした。
- W1 と W2 は**並べて進められる**（触る場所が違う: W1 は Java・JNI・マニフェスト・SaveData、W2 はキャンバス・入力・フォント・描画）。ただし同じ作業ツリーで並行して書き込まない（エージェントを並べるなら別の worktree）。
- アプリ（W3）は、W1-1〜W1-4 と W2-1〜W2-4・W2-7 ができた時点で主要な画面に取りかかれる。純粋ロジックと保存（アプリ仕様 §8 の W3-D・W3-S）は今すぐ始められる。

### 6.1 W1 の最初の一歩（提案）

**W1-0 のスパイク**（1〜2 段階）。API を作る前に、いちばん大きな前提（別プロセスの鳴動と、フルスクリーン通知からの GameActivity の冷えた起動）を実機で確かめる。

1. `runtime/android/app` に試験用の Java だけのパッケージ（`com.seedengine.runtime.platform`）を足す: `AlarmReceiver`（exported=false）・`RingService`（`android:process=":seed_platform"`、`mediaPlayback`、`MediaPlayer` の `USAGE_ALARM` のループ、フォーカスを失っても止めない）・`setAlarmClock` での予約・フルスクリーン通知（`activity-alias PlatformEntry` → MainActivity）。予約と停止は、エンジンの API を作らずにデバッグ用の `adb shell am broadcast` で起こす。
2. Pixel 6a（Android 16）とエミュレータ（API 34）で次を測る: (a) 画面オフ＋ロック中に時刻どおり鳴り、ロック画面の上に GameActivity が冷えた状態で出るまでの秒数、(b) 最近のタスクから消しても鳴り続ける、(c) `dumpsys deviceidle force-idle` の下で時刻どおり、(d) `adb reboot` の後に張り直される（ここだけは控えを Java で持つ）、(e) `ContentResolver.call` の往復時間。
3. 結果で E-01（別プロセス）・E-02（JNI の手段）・E-03（前景サービスの種類）を決め、この文書を更新する。

理由: C# や Rust を一行も変えずに adb だけで確かめられ、崩れたときに捨てる量が最小。ここが崩れると W1 の API の形（同期か非同期か、どのプロセスが控えを持つか）が変わる。

## 7. backlog との対応

未着手・保留の課題は [backlog.md](backlog.md) の「アプリ基盤（W1 Android サービス層 / W2 UI 部品群）」節に置く。W0 の調査で見つかった既存の不具合・制限（SaveData の置き換えの隙間・リリース版で起動理由が取れない・権限とコンポーネントを足す仕組みが無い・ゲーム向けの固定の既定）もそこに書いた。既存の Android 節の関連項目（起動の初期化の同期・アプリを終える API が無い・安全領域の自動反映・縦画面の自動スケール・ジェスチャー・指0 だけのポインタイベント）は、そちらを正とし、この文書からは参照だけする。
