# 自動再読込（スクリプト / シーン / プレハブ）と Play 中の扱い

エディタは 3 つの自動再読込を持つ。

| 種別 | 監視対象 | 反映方法 | 設定（表示メニュー） |
| --- | --- | --- | --- |
| スクリプト | アセットルート配下の `**/*.cs` | ランタイムへ `RELOAD_SCRIPTS`（ホットリロード） | 表示 > スクリプト > スクリプトを自動再読込 |
| シーン | 今開いている `.scene` 1 ファイル | `LoadScene`（ファイルを開いたときと同じ経路） | 表示 > シーン > シーンを自動再読込 |
| プレハブ | アセットルート配下の `**/*.actor` / `**/*.actor2d`（`.backup/` 等を除く） | Edit: `PREFAB_REAPPLY_PATH` か `PREFAB_STATUS`／Play: `PREFAB_LIVE_PATCH_PATH`（第 7.1 節） | 表示 > シーン > プレハブを自動再読込 |

---

## 1. なぜ Play 中は反映しないのか

**スクリプトのホットリロードは、ランタイム側で全スクリプトインスタンスの作り直しを意味する。**
作り直されたインスタンスでは `OnStart` が改めて呼ばれるため、Play 中に走らせると次の実害が出る。

- チュートリアルや進行フラグが `OnStart` からやり直しになり、**ゲームが最初に戻る**
- `OnStart` で `Instantiate` している補助アクタ（ポーズメニュー・リザルト・評価バナー・
  巻き取り音・打点アイコン・火花）が**呼ばれた回数だけ増える** → フレーム時間が伸びる
- `static` シングルトンが**古いエンティティハンドルを持ち続け**、一部が描画されなくなる

シーンの自動再読込はさらに破壊的で、**ワールドごと作り直す**。

そのため既定では、**Play / Pause 中に検出した変更は保留し、Play を止めた直後に 1 回だけ反映する**。

## 2. 判定表

判定は 1 箇所（`editor/src/Reload/AutoReloadPolicy.cs`）に集約してあり、
`editor/tests/AutoReloadPolicyTests` が全組み合わせを固定している。

| 種別 | 自動再読込設定 | 状態 | 「Play 中もスクリプトを即時反映する」 | 結果 |
| --- | --- | --- | --- | --- |
| スクリプト | オフ | 任意 | 任意 | **Drop**（何もしない。保留もしない） |
| スクリプト | オン | Edit | 任意 | **ApplyNow** |
| スクリプト | オン | Play / Pause | オフ（既定） | **Defer**（Play 停止時に反映） |
| スクリプト | オン | Play / Pause | オン | **ApplyNow** |
| シーン | オフ | 任意 | — | **Drop** |
| シーン | オン | Edit | — | **ApplyNow** |
| シーン | オン | Play / Pause | — | **Defer**（設定に関わらず必ず保留） |
| プレハブ | オフ | 任意 | — | **Drop** |
| プレハブ | オン | Edit | — | **ApplyNow**（送るものは第 7.1 節の表。設定「プレハブ保存時に…自動反映」で分かれる） |
| プレハブ | オン | Play / Pause | — | **ApplyNow**（状態を保つ当て直し。保留しない。Edit のシーンへは停止後に反映） |

- `Idle` / `Building` / `Launching` は「まだワールドが動いていない」ため **Edit** として扱う。
- **Drop（設定オフ）では保留もしない。** 保留すると「オフにしたのに Play 停止時に勝手に反映された」になるため。
- 保留中に設定をオフにした場合、Play 停止時にも反映しない。

保留したときは上部バーへ警告色で通知する。

- スクリプト: `スクリプトの変更を検出しました（Play 停止時に反映）`
- シーン: `シーンの外部変更を検出（Play 停止時に再読み込み）`

## 3. Play 停止時の消化

`MainWindow.OnStateChanged` が `EditorState.Edit` を受けたときに、
**シーン → スクリプト**の順で保留分を消化する（シーンを読み直す場合は
新しいワールドに対してスクリプトを組み直すことになり、逆順だと無駄な 1 往復が増えるため）。

シーンの保留分は消化経路が通常のデバウンス判定（`Fire`）を通るため、
**未保存の編集があるときは従来どおり見送られる**（破棄になるため。
明示的に取り込むときは「表示 > シーン > シーンをディスクから再読込」）。

## 4. 設定「Play 中もスクリプトを即時反映する」

- 場所: 表示 > スクリプト > Play 中もスクリプトを即時反映する
- 永続化: `editor/settings/editor_preferences.json` の `play_script_hot_reload`
- 既定: **オフ**

オンにすると Play を止めずにスクリプトを反映できる（挙動だけを詰めたいとき向け）。
第 1 節の副作用を承知のうえで使うこと。**シーンの自動再読込はこの設定の対象外**で、
Play 中は常に保留される。

## 5. 多重防御: `SpawnOnce`

設定をオンにした場合・将来の別経路のために、スクリプト側にも防御を置いている。
`assets://common/scripts/UI/SpawnOnce.cs` の
`SpawnOnce.GetOrInstantiate(actorName, prefabPath, parent = null)` は、
**同名のアクタが既にあればそれを使い回す**。生成したアクタには `GameObject.Name` で
目印の名前が付くので、次に `OnStart` が走っても拾い直すだけで済む。

適用済み: `FishingController`（ReelSound）、`PauseMenu` / `ResultPanel` /
`FightEvalBanner`（プレハブ生成のフォールバック）、`HitBanner`（火花 `HitSparkleNN`）、
`FishingFight`（打点アイコンプール `BeatIconNN`）。

詳細と制約（同一フレーム内で同じ名前を 2 回要求すると 2 つ作られる等）は
`docs/scripting_api.md` の `SpawnOnce` の節を参照。

## 6. Android の実行中の差し替え（端末へ送る）

Android の実行中（端末のアプリが動いていて IPC がつながっている間）は、上の PC 向けの自動再読込とは**別の監視**
（`editor/src/AndroidRun/AndroidHotReloadController.cs`）が保存を拾い、端末へ送って取り込ませる。正典は
[android.md](android.md) §23。

- **保留しない。** Android の実行は「動かしたまま直したものを反映する」ための機能なので、端末のゲームが動いていても
  すぐに差し替える（第 1 節の副作用＝スクリプトの差し替えは `OnStart` の再実行、シーンの読み直しはシーンの開始時へ戻る、は同じ）。
- 設定は PC と共通: 「スクリプトを自動再読込」がオフなら `.cs` を、「シーンを自動再読込」がオフなら `.scene` を端末へ送らない
  （判定は変更を覚える時点。画像・モデル等のアセットは常に送る）。「Play 中もスクリプトを即時反映する」は Android には効かない。
- シーンは開いているシーンに限らず、保存された `.scene` を送る（端末の今のシーンなら読み直し、違えば送っただけで遷移したときに反映）。
  未保存の編集は送らない（保存したときに送られる）。
- PC 側の自動再読込（第 2〜3 節）は Android の実行中もそのまま動く（エディタの Edit のワールドは PC の規則で更新される）。

## 7. プレハブ（.actor）の Play 中の扱い（2026-10-02）

プレハブは「保留して停止時に反映」ではなく、**Play 中のインスタンスへ状態を保ったまま当て直す**
（`PREFAB_LIVE_PATCH_PATH`。スクリプトの CLR インスタンスを作り直さない＝第 1 節の副作用が無い）。
そのうえで、Play 停止後に Edit のシーンへも反映する（Play の世界は停止で Play 前の写しへ戻るため）。
正典は [editor_prefab.md](editor_prefab.md) 8 章。

| 契機 | Play / Pause 中 | Play 停止時 |
| --- | --- | --- |
| プレハブの保存（`SAVE_OK`） | `PREFAB_LIVE_PATCH_PATH`（設定に関わらず）＋パスを覚える | 設定オン: `PREFAB_REAPPLY_PATH`／オフ: `PREFAB_STATUS`（バナー） |
| Play 中の変更の書き戻し（`PREFAB_WRITE_BACK_DONE`） | ランタイムが続けて当て直し済み。パスを覚える | 同上 |

- 設定は「表示 > シーン > プレハブ保存時にシーンのインスタンスへ自動反映」（`PrefabAutoPropagateOnSave`）。
  Edit のシーン（保存されるもの）への反映だけに効く。停止時点の値で判断する（保留中にオフにしたら再展開しない）。
- 判定は `editor/src/Reload/PrefabPlayReapplyQueue.cs`（純粋なクラス）、テストは `editor/tests/PrefabPlayReapplyTests`。
- 消化は `MainWindow.OnStateChanged`（Edit）→ `OnReturnedToEditForPrefabs`（`MainWindow.Prefab.cs`）。
  シーン・スクリプトの保留分の消化の後に行う。
- **停止した時点でアクタータブ（キャンバス編集タブを含む）を表示中なら、設定オンでも再展開せず `PREFAB_STATUS`（バナー）に落とし**、
  見送った理由をトーストで知らせる（2026-10-03。`docs/reviews/2026-10-03_code_review.md` #16）。アクタータブの表示中は、
  タブの読み直し（`OPEN_ACTOR`）とシーンのタブへの切り替え（`SET_ACTIVE_WORLD_LINE`）が Undo の履歴を作り直すので、
  「Ctrl+Z で戻せます」を約束できないため。更新はシーンのタブで、バナーの［更新する］から行う。
- 表示中のアクタータブが書き戻しで古ければ、**反映より前に**読み直す（`OPEN_ACTOR` を先に送る。後に送ると再展開の Undo を消す）。
  そのタブに未保存の編集があれば確かめる（[editor_prefab.md](editor_prefab.md) 8 章）。

### 7.1 プレハブの外部変更の取り込み（2026-10-03）

エディタの Ctrl+S は Edit 以外では動かないため、Play 中に `.actor` を直す手段はエディタの外（テキストエディタ・
AI・別ツール）になる。アセットルート配下の `.actor` / `.actor2d` を監視し、外部の書き換えを次のように送る。

| 自動再読込（`AutoReloadPrefabs`） | 状態 | 保存時の自動反映（`PrefabAutoPropagateOnSave`） | 送るもの |
| --- | --- | --- | --- |
| オフ | 任意 | 任意 | **何もしない**（保留もしない） |
| オン | Edit | オン | `PREFAB_REAPPLY_PATH`（Undo 1 操作・件数のトースト・シーンは未保存に。0 件なら黙る） |
| オン | Edit | オフ | `PREFAB_STATUS`（版ずれのバナーだけ。シーンに触れない） |
| オン | Play / Pause | 任意 | `PREFAB_LIVE_PATCH_PATH` ＋パスを覚える（停止時は第 7 節の表どおり） |

- Edit では画面プレビュー（`PREVIEW_REFRESH_PATH`）も作り直す（保存したときと同じ。設定に関わらず）。
- 判定は `AutoReloadPolicy.DecidePrefabExternalChange`（純粋な関数）。エディタ自身の保存の続き
  （`PrefabPlayReapplyQueue.DecideOnSave`）とは、Edit・自動反映オフのときだけ違う（本人が今保存したならバナーは出さない／
  外部の書き換えならバナーで知らせる）。
- 設定は「表示 > シーン > プレハブを自動再読込」（`editor_preferences.json` の `auto_reload_prefabs`、既定オン）。

**監視の対象**（`PrefabWatchPaths`）: 拡張子が `.actor` / `.actor2d` のもの。`.backup/`（世代バックアップ）・
`obj` / `bin` / `.git` / `.vs` / `node_modules` の中は拾わない。原子的な保存の一時ファイル `X.actor.tmp` は拾わず、
名前の変更 `X.actor.tmp → X.actor` を本名で拾う。

**デバウンスと自己書き込みの除外**（`PrefabExternalChangeTracker`。純粋なクラス。SceneAutoReloader と同じ値）:

1. パスごとに最後のイベントから **600 ms** 静まったら判定する（書き込みの途中で読まない）。
2. 内容の SHA-256 を読む（`FileContentHash`。シーンの自動再読込と共有）。読めなければ 600 ms 後に読み直す（**5 回**まで。超えたら捨てる）。
3. 前に知っていた内容と同じなら何もしない（touch・重複イベント）。
   **監視の開始時**（設定がオンなら）と**設定をオンにしたとき**に、既存の `.actor` / `.actor2d` の内容を背景スレッドで読んで覚える
   （`PrefabKnownHashSeeder`。数が多くても UI を止めない。2026-10-03。`docs/reviews/2026-10-03_code_review.md` #15）。
   それまでは「知っている内容」を埋めるのが判定と自己書き込みの終了だけで、起動直後の最初の書き込みは内容が同じでも外部の変更になっていた。
   - **覚え終わる前に届いたイベントは従来どおり外部の変更**として扱う（その時点では内容を知らない）。
   - 覚え込みを読んでいる間に書かれたファイル（読む前後で更新時刻が違う・更新時刻が覚え込みの開始より後）は覚えない
     （新しい内容を「知っている」ことにすると、その書き込みを取りこぼすため）。
   - 覚え込みより後にイベント・自己書き込みで知った内容は、遅れて届いた覚え込みで上書きしない。判定待ちのパスにも覚えさせない。
4. 一括の書き換え（「ツール → プロジェクトの形式をアップグレード」のダイアログの間と、閉じてから **1.5 秒**）は当て直さない。
5. **エディタ自身の書き込み**は当て直さない。どれもランタイムがファイルを書くので、開始と終了を知らせる:

| 書き込み | 開始（窓を開ける） | 終了（余韻 1.5 秒・その時点の内容を覚える） |
| --- | --- | --- |
| アクタータブの保存 `SAVE_ACTOR` | `ExecuteActorSave`（`NotifyActorSaveStarted`） | `SAVE_OK` / `SAVE_ERROR`（`OnSaveCompleted`） |
| Play 中の変更の書き戻し `PREFAB_WRITE_BACK` | 送ったとき（プレハブの参照パスが分かるときだけ） | `PREFAB_WRITE_BACK_DONE` / `_ERROR` |
| アクタファイル化 `EXPORT_ACTOR` | （パネルが直接送るので無し） | `EXPORT_ACTOR_OK` |

   終了の知らせが来なければ開始から **15 秒**で窓を閉じる（応答が失われても監視が死なない）。終了時に覚えた内容と
   同じなら、余韻の後に遅れて届いたイベントも 3 で落ちる。保存・書き戻しには既に `PREFAB_REAPPLY_PATH` /
   当て直しの続きがあるので、監視が拾うと二重になる。
6. それ以外は外部の変更として上の表に従って送る。

テスト: `editor/tests/AutoReloadPolicyTests`（`PrefabAutoReloadTests.cs`。判定表の全組み合わせ・パス・デバウンス・自己書き込み・抑止。
`PrefabHashSeedTests.cs`。開始時の覚え込みと、覚えてよいファイルの選び方）。

## 8. 関連ファイル

| ファイル | 役割 |
| --- | --- |
| `editor/src/Reload/AutoReloadPolicy.cs` | 判定ロジック（純関数。WPF・ランタイム非依存） |
| `editor/tests/AutoReloadPolicyTests/` | 上記の単体テスト |
| `editor/src/Reload/PrefabPlayReapplyQueue.cs` | Play 中に変わったプレハブの待ち行列（第 7 節。純粋なクラス） |
| `editor/tests/PrefabPlayReapplyTests/` | 上記の単体テスト |
| `editor/src/Reload/PrefabAutoReloader.cs` | `.actor` / `.actor2d` の監視・タイマー（第 7.1 節。判定は下の 2 つへ委譲） |
| `editor/src/Reload/PrefabExternalChangeTracker.cs` | デバウンス・自己書き込みの除外・内容の比較・抑止（純粋なクラス） |
| `editor/src/Reload/PrefabWatchPaths.cs` | 監視の対象のパスの判定（純粋な関数） |
| `editor/src/Reload/PrefabKnownHashSeeder.cs` | 監視の開始時に既存のプレハブの内容を読む（背景スレッドで呼ぶ。覚えてよいファイルの選び方。第 7.1 節の規則 3） |
| `editor/src/Reload/StaleActorTabs.cs` | 書き戻しで古くなったアクタータブの読み直しの印（閉じる・開くで消す・未保存なら確かめる。純粋なクラス） |
| `editor/src/Reload/FileContentHash.cs` | 内容の SHA-256（シーン・プレハブの自動再読込が共有） |
| `editor/src/MainWindow.PrefabAutoReload.cs` | プレハブ側の依存注入・送るものの振り分け・自己書き込みの橋渡し・メニュートグル |
| `runtime/src/engine/core/app_base/app/prefab_live_patch/` | Play 中の当て直し・書き戻し（ランタイム側） |
| `editor/src/Scripting/ScriptAutoReloader.cs` | `.cs` の監視・デバウンス・コンパイル検証・送信 |
| `editor/src/Scene/SceneAutoReloader.cs` | `.scene` の監視・自己保存の除外・再読込 |
| `editor/src/MainWindow.SceneAutoReload.cs` | シーン側の依存注入と `CurrentPlaybackState` |
| `editor/src/MainWindow.xaml.cs` | スクリプト側の依存注入・メニュートグル |
| `editor/src/MainWindow.Camera.cs` | `OnStateChanged` での保留分の消化（シーン → スクリプト → プレハブ） |
| `editor/src/EditorPreferences.cs` | `auto_reload_scripts` / `auto_reload_scene` / `play_script_hot_reload` / `auto_reload_prefabs` |
| `editor/src/AndroidRun/AndroidHotReloadController.cs` | Android の実行中の監視・まとめ・差し替えの呼び出し（第 6 節） |
| `editor/src/MainWindow.AndroidRun.cs` | 上記の依存注入（種類ごとの設定の参照・Output への配線） |
