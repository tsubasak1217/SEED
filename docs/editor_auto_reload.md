# 自動再読込（スクリプト / シーン）と Play 中の扱い

エディタは 2 つの自動再読込を持つ。

| 種別 | 監視対象 | 反映方法 | 設定（表示メニュー） |
| --- | --- | --- | --- |
| スクリプト | アセットルート配下の `**/*.cs` | ランタイムへ `RELOAD_SCRIPTS`（ホットリロード） | 表示 > スクリプト > スクリプトを自動再読込 |
| シーン | 今開いている `.scene` 1 ファイル | `LoadScene`（ファイルを開いたときと同じ経路） | 表示 > シーン > シーンを自動再読込 |

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

## 7. 関連ファイル

| ファイル | 役割 |
| --- | --- |
| `editor/src/Reload/AutoReloadPolicy.cs` | 判定ロジック（純関数。WPF・ランタイム非依存） |
| `editor/tests/AutoReloadPolicyTests/` | 上記の単体テスト |
| `editor/src/Scripting/ScriptAutoReloader.cs` | `.cs` の監視・デバウンス・コンパイル検証・送信 |
| `editor/src/Scene/SceneAutoReloader.cs` | `.scene` の監視・自己保存の除外・再読込 |
| `editor/src/MainWindow.SceneAutoReload.cs` | シーン側の依存注入と `CurrentPlaybackState` |
| `editor/src/MainWindow.xaml.cs` | スクリプト側の依存注入・メニュートグル |
| `editor/src/MainWindow.Camera.cs` | `OnStateChanged` での保留分の消化 |
| `editor/src/EditorPreferences.cs` | `auto_reload_scripts` / `auto_reload_scene` / `play_script_hot_reload` |
| `editor/src/AndroidRun/AndroidHotReloadController.cs` | Android の実行中の監視・まとめ・差し替えの呼び出し（第 6 節） |
| `editor/src/MainWindow.AndroidRun.cs` | 上記の依存注入（種類ごとの設定の参照・Output への配線） |
