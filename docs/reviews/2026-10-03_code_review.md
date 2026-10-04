# 2026-10-02 夜〜10-03 未明に合流したコミットのレビュー（2 回目）

> **対応状況（2026-10-03 03:20 時点。HEAD 76fee9e6）** — 残りは `docs/backlog.md` の「2 回目のレビューの未対応（ランタイム側）」で追う。
>
> | 項目 | 状況 | コミット |
> |---|---|---|
> | #1〜#4（書き戻しの 4 件） | **回避のみ**: 書き戻しを既定無効（`prefab_write_back_enabled`）。根本の直しは未着手 | 4cac1dbf |
> | #5（外部変更 × シーンの自動再読込） | **暫定**: Edit の外部変更は `PREFAB_STATUS`（バナー）だけにした。「版のずれたインスタンスだけ再展開」は未着手 | 5ee3dc78 |
> | #6（アクタータブ中のアップグレード） | 済（アクタータブ表示中は始めない） | 19255447 |
> | #7（OnDestroy の中で結び付けが外れる） | 済（区切りで確かめ直す） | 97554d82 |
> | #12 #13 #14 #15 #16 #26（エディタ側） | 済（#13 は「名前も構成も同じ兄弟」が残り） | 054419bf |
> | #20〜#25、#31〜#33 の一部（SEED.UI / Binding / L10n） | 済 | 0a94dac8 |
> | **#8 #9 #10 #11 #17 #18 #19（ランタイム側の中）** | **未着手** | — |
> | #27〜#30、#34〜#37（低・まとめ）、#31〜#33 の残り | 未着手 | — |

- 基準: **4fb14cde**（レーン 3 のデータバインディングの統合）。行番号はすべて 4fb14cde 基準。それ以降のコミットは対象外。
- 対象: 3111058f・343b90a0・f0f7088d・edccecec・0045ce2b・bfd3121a・d996bcb5・1232cb34・7c7ab934・df390401・39417f4f・95723ec7。ほかに合流コミットが 9 件ある。`git show --remerge-diff` で見ると、手で衝突を解いたのは 77180ec5 の docs/backlog.md だけだった。
- 方法:
  - コミット済みの内容を読んだだけ。`git show` と、`git archive` で作業ツリーの外（tmp）に書き出した 4fb14cde の写しを使った。
  - **ビルド・テスト・実行は一切していない**（指示どおり）。「何が起きるか」はコードを読んで導いたもので、再現はしていない。
- 分担:
  - 監督役: レーンをまたぐ組み合わせ、FFI の突き合わせ、合流。
  - サブレビュア 8 名（Opus 7・Sonnet 1。すべて読み取りのみ）:
    - A: 3111058f のランタイム
    - B: 3111058f のエディタ＋343b90a0
    - C: f0f7088d
    - D: edccecec
    - E: 0045ce2b・bfd3121a
    - F: d996bcb5・1232cb34
    - G1: 95723ec7・7c7ab934
    - G2: df390401・39417f4f
  - 「確認: 済」は、監督役がコードを読んで裏を取ったもの。「確認: 未」は、サブレビュアの読みだけのもの。

## 要約

| 重さ | 件数 | 主なもの |
|---|---|---|
| 致命 | 0 | — （`.actor` の上書きはどれも `.backup/` に 10 世代残るので、手で戻せる） |
| 高 | 7 | Play 中の書き戻し（PREFAB_WRITE_BACK）が `.actor` を壊す経路が 4 つ（#1〜#4）。<br>外部変更の自動再展開とシーンの自動再読込がぶつかり、取得した `.scene` を古い内容で上書きさせる（#5）。<br>一括アップグレードの「保存してから」がシーンの未保存の編集を消す（#6）。<br>OnDestroy の中で観測値を変えると結び付けが黙って外れる（#7）。 |
| 中 | 19 | 選択がずれる経路の残り（#8〜#13）、書き戻しのタブの読み直し、監視の初回、Undo の約束、当て直しの破棄、動的ノード API と地形・物理、SEED.UI の Push・作り置き・CloseAll、双方向の留め金、収録のフォルダ |
| 低 | 11 組 | #27〜#37（領域ごとにまとめた） |

**書き戻しの 4 件（#1〜#4）は根が同じ**です。書き戻しは Play 中の木をそのまま `.actor` にし、除いているのは「スクリプトが生成したノードの部分木」と「根の配置の値」だけです。どの版から作られたか・何をスクリプトが変えたかを見ていません。

docs/editor_prefab.md §8 の「入れ子のプレハブは 1 段の参照のまま」「根の値は今のファイルの値を保つ」など、個々の規則の実装は正しく読めました。抜けているのは、ファイルの側から見た安全確認です。直すときは、`base_cache` に控えてある元の版との差分を書く前に確かめる形が、まとめて効きます。

---

## 1. 書き戻しが、スクリプトの生成物の中へ移されたファイル由来のノードまで捨てる。Wake or Pay の shell.actor では 4 つのタブの根の画面が消える  【重さ: 高】
- 場所:
  - runtime/src/engine/core/app_base/app/prefab_live_patch/write_back.rs:107-121（`prune_script_spawned` は印のある子を部分木ごと外す）
  - scripting/src/Api/UI/Navigation/ScreenStack.cs:378（枠を `GameObject.Instantiate(FramePrefab, _screens)` で作る＝印あり）・413-421（置いてある根・渡された中身を `adopted.SetParent(parent)` で枠の中へ移す）・458-466（`TakeAdoptChild` は Screens の下に置いた子＝ファイル由来）
  - ScreenStack.Content.cs:37-44・99-110（`Push(GameObject)` の中身）
  - runtime/src/engine/core/app_base/app/script_scene_ops.rs:513-549（スクリプトの SetParent は印を変えない）
  - editor/src/MainWindow.Prefab.cs:65-69（確認ダイアログの文）
  - コミット 3111058f。fc706e68 の RootAdoptChild、0045ce2b の Push(GameObject) と組み合わさって起きる。
- 何が起きるか:
  - Wake or Pay の `assets/app/prefabs/shell.actor` では、`TabHost/Pages/{Alarms,Activity,Garden,Shop}` の 4 つの ScreenStack が、`RootAdoptChild` で `AlarmList`・`ActivityTab`・`GardenTab`・`ShopTab` を引き取る。どれも入れ子のプレハブのインスタンス（サブレビュア A が実ファイルを読み取りで確認）。
  - Play 中に Shell の見た目を詰めて「書き戻す」と、4 つの画面は枠（印あり）と一緒に刈られ、**shell.actor から消えて書かれる**。
  - 続く当て直しは 3 方向で何も消さないので、Play 中は気付けない。
  - 停止すると `PREFAB_REAPPLY_PATH`（既定オン）が走り、App.scene の Shell も 4 つを失う。シーンは Undo で戻せるが、ファイルは `.backup/` から手で戻すしかない。
  - 次の Play では `RootPrefab` から作り直して動くので、壊れたことが表に出にくい。
  - `Push(GameObject)` で渡した、アプリの置き場（ファイル由来）の中身も同じ。
- 根拠:
  - 印（`spawned_by_script`）はノードごとの「作られ方」で、子孫の出所を表さない。刈る関数は印の無い子孫を探さない。
  - 確認ダイアログは「スクリプトが Play 中に生成した部分（積まれた画面・リストの行など）は書き込みません」と言うだけで、生成物の中へ**移された**ファイルのノードが消えることは書いていない。docs/editor_prefab.md §8 も同じ。
- 確認: 済（監督役と A が別々に同じ結論。監督役は刈る関数・ScreenStack の引き取り・SetParent の適用を読んだ）
- 直し方の案: 刈る部分木の中に印の無い子孫があれば、そのノード名を理由にして書き戻しを断る。または、書く前に今のファイルと名前の道筋で突き合わせ、ファイルのノードが消えるなら断る（#4 と同じ確認でまとめて防げる）。

## 2. 古い版から作られたインスタンス、または 2 方向で当て直したインスタンスを書き戻すと、プレハブのその後の変更を黙って巻き戻し、消したノードを復活させる  【重さ: 高】
- 場所:
  - prefab_live_patch/write_back.rs:63-73（書く中身はインスタンスの `to_data` そのもの。今のファイルから戻すのは根の名前・active・visible・2D の CanvasTransform だけ。`keep_template_root` は 97-105）
  - prefab_live_patch/ops.rs:106-121（2 方向でも当て直しの後に `prefab_hash` を新しい版へ書き換える）
  - plan.rs:224-240（2 方向では「ファイルが消したノード」を消さない）
  - コミット 3111058f
- 何が起きるか:
  - Card.actor を使うシーンが A と B の 2 つある。A を開いて Card.actor に子 Badge を足して保存する。保存時の自動反映は開いているシーン A にしか効かないので、B のインスタンスは古い版のまま残る。版ずれのバナーを［無視］したときや、自動反映をオフにしている利用者でも同じ。
  - B を開いて Play し、Card を詰めて書き戻すと、Card.actor から Badge が消える。停止時の再展開で A にも巻き戻しが広がる。
  - 2 方向（元の版の控えが無い）で当て直したインスタンスは、プレハブで消したノードを持ったまま `prefab_hash` だけ新しい版になる。これを書き戻すと、消したノードがファイルに復活する。
- 根拠: 書き戻しには、`actor.prefab_hash` と今のファイルの版を比べる処理が無い。
- 確認: 済（監督役が write_back.rs・ops.rs:121・plan.rs の消す条件を読んだ）
- 直し方の案:
  - 今のファイルの版と `actor.prefab_hash` が違う・None・2 方向で当て直し済みなら、理由を付けて断る。
  - または、元の版・今のファイル・インスタンスの 3 方向で合成してから書く。合成の部品は `merge.rs` にある。

## 3. 書き戻しの対象を DFS 番号だけで決めるので、確認ダイアログの間に Play の木がずれると別のインスタンス・別の .actor を上書きする  【重さ: 高】
- 場所:
  - editor/src/MainWindow.Prefab.cs:222-240（`RequestPrefabWriteBack`。番号を引数で受け、MessageBox の後にそのまま送る）
  - editor/src/Panels/InspectorPanel.xaml.cs:10419-10420（帯を組み立てた時点の `_currentActorId` を閉じ込める）
  - editor/src/Panels/HierarchyPanel.xaml.cs:1199（メニューを作った時点の `prefabNode.Id`）
  - runtime/.../prefab_live_patch/write_back.rs:47-62（確かめるのは「その番号のアクタがプレハブの根か」だけ）
  - コミット 3111058f
- 何が起きるか:
  1. Play 中、DFS で後ろにあるインスタンスの根（例: Card.actor、40 番）で［書き戻す］を押す。
  2. 「元に戻せません」の確認を読んでいる間もゲームは進む。手前でノードの増減が起きると番号がずれる（Toast・リストの行・ScreenStack の積み下ろし・`Create`・当て直しなど）。
  3. OK で `PREFAB_WRITE_BACK:40` が届き、40 番に今いる**別のアクタ**が対象になる。
     - それが別のプレハブ（例: Row.actor）の根なら、その実行中の状態で Row.actor を上書きする。
     - 同じプレハブの別インスタンス（一覧のカードの隣）なら、隣の値で Card.actor を上書きする。
  4. 停止後は、書かれた方のパスで Edit のシーンも再展開される。
  - 当て直し（PREFAB_LIVE_PATCH_PATH）自身も木の形を変える。それなのに古い選択の番号で `ACTOR_COMPONENTS` を送るので（ops.rs:146-149）、インスペクタの帯の［書き戻す］も別の対象を指しうる（#8 と同じ根）。
- 根拠:
  - エディタは確認ダイアログに出した `source` を知っているのに、IPC には番号しか載せない。
  - ランタイムは `prefab_source.is_some()` しか見ない。
- 確認: 済（監督役・A・B の 3 者が別々に同じ結論。ずれが起きる頻度はゲームによる）
- 直し方の案:
  - `PREFAB_WRITE_BACK:{dfs},{仮想パス}` にして、ランタイムで `prefab_source` の一致を確かめ、違えば断る。
  - 同じプレハブの取り違えも防ぐため、ダイアログの後にヒエラルキーの安定キー（名前の道筋）で番号を引き直してから送る。

## 4. スクリプトがファイル由来のノードに加えた変更（AddComponent / AddScript / RemoveComponent・表示・本文・Destroy・付け替え）がそのまま焼かれる。docs の約束と逆で、AddScript は書き戻すたびに増える  【重さ: 高】
- 場所:
  - runtime/src/engine/core/app_base/app/script_node_ops.rs:66-140（`AttachSlot`・`AddScript` は、ファイル由来のアクタへ印なしでスロットを足す。`ComponentSlot` に印の欄が無い）
  - structs/objects/actor/mod.rs:416-451（`to_data` は全スロット・`visible`・`active`・名前を書く）
  - prefab_live_patch/write_back.rs:63-65（除くのはノードの印だけ）
  - script_scene_ops.rs:487-492（`SetVisible` は `actor.visible` を直接変える）
  - docs/scripting_api.md:1319（「`AddComponent` で足したコンポーネントは…書き戻しでファイルへ書かれない」）
  - コミット 3111058f × f0f7088d
- 何が起きるか:
  - プレハブの中のスクリプトが OnStart で、ファイル由来のノードに `AddScript<RowView>()` や `AddComponent<Sprite>()` をする。
    - Play 中に詰めて書き戻すと、足したものが .actor に入る。
    - 次の Play で OnStart がもう 1 つ足すので 2 つになり、書き戻すたびに増える。スクリプトなら処理とイベントの購読が 2 回になる。
  - `RemoveComponent<Text>()` で外したスロットは、ファイルからも消える。
  - バインド（`Bind.Visible`）やスクリプトが切り替えた子の表示、Text の本文、アニメの途中の姿勢もそのまま焼かれる。
  - OnStart で見本の行を `Destroy` する・外の層へ `SetParent` する作りでは、そのノードがファイルから消える。
  - 続く当て直しは「元の版 → 書いた中身」の差分を Play 中のほかの全インスタンスへ当てる（plan.rs:215-237・apply.rs:318-323）。その結果:
    - ほかのインスタンスの実行時の値が上書きされる。
    - このインスタンスで消したノードが、ほかのインスタンスでも破棄される（OnDestroy が走る）。
- 根拠: 書き戻しで除くのはノードの `spawned_by_script` だけ。実行時に足したスロットの印も、元の版（`base_cache` に控えがある）との突き合わせも無い。
- 確認: 済（監督役・A・C が別々に同じ結論。監督役はスロットの追加の適用・to_data・docs の 1319 行を読んだ）
- 直し方の案:
  - AttachSlot / AddScript で足したスロットに実行時だけの印を持たせ、書き戻しの直列化で除く。`to_data` は filter_map なので添字の合わせ方に注意。
  - 書く前に元の版との差分を作り、ファイルのノード・スロットが消える場合や、表示・名前だけが変わった場合は一覧にして確認を取る（既定は断る）。
  - docs:1319 とダイアログを実装に合わせる。

## 5. 外部変更の自動再展開がシーンの自動再読込とぶつかる。VCS の取得などで .scene と .actor が同時に変わると、取得した .scene を古い内容で上書きさせる／読み直したシーンの上書きを消す  【重さ: 高】
- 場所:
  - editor/src/MainWindow.PrefabAutoReload.cs:79-87（Edit・自動反映オンなら無条件に `PREFAB_REAPPLY_PATH`）
  - editor/src/Scene/SceneAutoReloader.cs:423-430（未保存なら読み直さない）
  - runtime/.../prefab_ops.rs:133-171・420-471（`prefab_hash` を見ず、パスの一致したインスタンスを全部作り直す）
  - editor/src/MainWindow.Migration.cs:149-157（抑止しているのは一括アップグレードの間だけ）
  - コミット 343b90a0
- 何が起きるか: 開いているシーン S.scene（未保存なし）と、S にインスタンスのある P.actor が同時に書き換わる。例は次のとおり。
  - VCS パネルの取得（Lore の sync）
  - AI の `write_asset_file` で両方を書く
  - git pull / checkout
  - テンプレートの上書きインポート
  - 相手が「P を保存し、自動反映で S も更新して」両方を送ってきた、という普通の形

  どちらの順で判定されても壊れる。
  - (a) P の判定が先:
    1. 古い S に対して再展開が走る。
    2. `SCENE_MODIFIED` で `*` が付く。
    3. S の判定は「未保存の編集があるため再読込しません」で見送られる。
    4. そのまま Ctrl+S（または終了時の「保存しますか」）で、**取得した新しい S を古い S で上書きする**。
  - (b) S が先に読み直された:
    1. 新しい S のインスタンスは、すでに新しい P と同じ版になっている。
    2. それでも続く再展開が丸ごと作り直し、S に保存されていたインスタンスごとの変更を消して `*` にする。
- 根拠: 外部の書き込みを抑止しているのは一括アップグレードだけ。次の経路は抑止の外にある（サブレビュア B が確認）。
  - VCS パネル（VersionControlPanel.Operations.cs:55-67）
  - AI の書き込み（EditorCommandExecutor.cs:260-299）
  - インポート（TemplateImporter.cs:149-179）

  docs/editor_auto_reload.md §7.1 に、シーンの自動再読込との関係の記述が無い。
- 確認: 済（監督役が、無条件の送信・未保存での見送り・パスだけで選ぶ再展開を読んだ。VCS とインポートの経路は B の読み）
- 直し方の案:
  - 外部変更では「版のずれたインスタンスだけ」再展開する。方法は、ランタイムで `prefab_hash` が今のファイルと一致したら飛ばすか、PREFAB_STATUS で stale>0 のパスだけ送るか。
  - 自動の再展開による `*` を、シーンの読み直しを見送る理由にしない。
  - VCS の取得とインポートは抑止で包み、終わったらシーンの読み直し＋PREFAB_STATUS にまとめる。

## 6. 一括アップグレードの［はい（保存してから）］が、アクタタブ表示中はアクタだけを保存し、シーンの未保存の編集を読み直しで消す  【重さ: 高】
- 場所:
  - editor/src/MainWindow.Migration.cs:77-91（SaveThenOpen → `DoQuickSave`）・159-166（実行後に `ForceReload`）
  - editor/src/MainWindow.Scene.cs:382-398（`DoQuickSave` は `_activeActorPath != null` なら `ExecuteActorSave` だけ）・583-618（`OnSaveCompleted` はアクタの保存でも `MarkClean()` して `ContinuePendingUpgrade`）
  - editor/src/Scene/SceneAutoReloader.cs:263-277（`ForceReload` は確認なしで読み直す）
  - コミット edccecec
- 何が起きるか:
  1. シーンを編集して `*` にする。
  2. .actor をダブルクリックしてアクタタブを開く（シーンの保存は求められない）。
  3. 「ツール → プロジェクトの形式をアップグレード...」で［はい（保存してから）］を選ぶ。
  4. アクタだけが保存され、全体の未保存の印が消え、窓が開く。
  5. 実行すると開いているシーンがディスクから読み直され、手順 1 の編集が消える。Undo の履歴も無くなる。
  - 利用者は「保存してから」を選んでいるので、消えることに気付けない。トーストは「読み直しました」。
- 根拠: 未保存の旗（`_isDirty`）はシーンとアクタタブで 1 つだけ。edccecec より前は読み直しが無かったので、編集はメモリに残っていた。MigrationTests の 2 件は純関数だけで、この配線は確かめていない。
- 確認: 済（監督役が DoQuickSave・OnSaveCompleted・ForceReload を読んだ。GUI では確かめていない）
- 直し方の案: アップグレードの前の保存では、アクタタブ表示中でもシーン（`_currentScenePath`）を保存する。それができない状態なら取りやめるか、先にシーンタブへ戻させる。

## 7. 別のスクリプトの OnDestroy で観測値・一覧を変えると、生きている結び付けが「当てる先が消えた」と判断して黙って外れる  【重さ: 高】
- 場所:
  - scripting/src/Api/Binding/Model/ValueBinding.cs:50-66（`!_target.IsAlive` なら即 `Dispose()`）
  - Targets/TextTarget.cs:23・SpriteColorTarget.cs:23・ActorLife.cs:35-48（`IsValid` / `HasComponent` で生死を見る）
  - Model/ListBinding.cs:40
  - runtime/src/engine/components/script_component.rs:795-811（OnDestroy は「World ポインタが公開されていない」フェーズ外で呼ばれる）
  - runtime/.../scripting/host_api.rs:2232-2242（World が無いと `ffi_has_component` は 0）
  - コミット 1232cb34
- 何が起きるか: `Bind.Text(this, label, GameState.EnemyCount, n => $"残り {n}")` を置き、敵の `OnDestroy` に `GameState.EnemyCount.Value--` を書いて敵を Destroy する。
  - OnDestroy の中では World が見えないので、`Text.IsValid` も `HasComponent` も false になる。
  - 結び付けは当てる先が死んだと判断して自分を外す。以後ラベルは二度と更新されず、ログも出ない。
  - `Bind.Visible`・`Bind.Color`・`Bind.List`（OnDestroy で `ObservableList.Remove`）・L10n 版の `Bind.Text` も同じ。
  - 暗幕を `Bind.Visible` で結んでいると、暗幕が出たまま残り、下の画面を操作できなくなる。
- 根拠: 「OnDestroy の中ではシーンが見えない」という既存の規約を、結び付けが「消えた」と読み違えている。OnDestroy で共有の状態を減らすのはよくある書き方。
- 確認: 済（監督役が、Apply の即 Dispose・IsValid の FFI・Drop の中の OnDestroy を読んだ）
- 直し方の案: `!IsAlive` のときは即 Dispose せず `WaitForTarget()` で次の区切りへ回す。区切り（World の中）で生死を確かめ直し、生きていれば最新の値を当てる。ListBinding も同じ。

## 8. 自動の再展開・当て直しの後、ランタイムの選択（selected_actor_dfs_ids）が古い番号のまま残り、ギズモと COPY が別のアクタに当たる  【重さ: 中】
- 場所:
  - runtime/.../prefab_ops.rs:133-170（`handle_reapply_prefab_path` は選択を引き直さず、古い `actor_virtual_selected_idx` で ACTOR_COMPONENTS を送る）
  - prefab_live_patch/ops.rs:146-148（当て直しも同じ）
  - editor/src/Panels/HierarchyPanel.xaml.cs:721-763（`RestoreSelectionAfterSync` の Moved はインスペクタにだけ知らせ、ランタイムへ `SELECT` を送らない）
  - clipboard.rs:54-60（COPY はランタイムの選択を使う）・gizmo_handler.rs:27-49（ギズモも同じ）・HierarchyPanel.xaml.cs:1505-1506（ヒエラルキーのコピーは `COPY` だけを送る）
  - 343b90a0 で外部変更による再展開が自動になった。edccecec の選択の引き直しは Undo / Redo の腕だけ。
- 何が起きるか:
  1. Edit でシーンの Lid（DFS 3）を選んでいる。
  2. 外部のツール（AI・テキストエディタ）が Card.actor に子を 1 つ足す。
  3. 監視が `PREFAB_REAPPLY_PATH` を自動で送り、Lid は 4 番になる。
  4. ヒエラルキーとインスペクタは 4 番へ移るが、ランタイムの選択は 3 番（今は Card の新しい子）のまま。
  5. ビューポートのギズモは Card の子に出る。ドラッグすると Card の子が動き、普通の編集として積まれる。ヒエラルキーで Ctrl+C すると Card の子がコピーされる。
  - Play 停止後の反映（PrefabPlayReapplyQueue）と、Play 中の当て直しでも同じことが起きる。
- 根拠: 利用者が選んだときは `SendSelectionToRuntime`（HierarchyPanel.xaml.cs:948-986）が `SELECT` を送るが、同期の後の引き直しでは送らない。
- 確認: 済（監督役）
- 直し方の案:
  - 再展開の後は、名前の道筋で選択を引き直して SELECTED を送る（undo_selection.rs の仕組み）。
  - 当て直しは entity が残るので、プレビューの `capture_selection` / `restore_selection` で引き直す。
  - エディタ側も、Moved のときに `SELECT` / `SELECT_MULTI` を送り直す。

## 9. Delete → Ctrl+Z で、戻ったアクタではなく次のアクタが選ばれる（edccecec の退行）  【重さ: 中】
- 場所:
  - runtime/.../actor_ops.rs:1185-1190・840-846（削除は `actor_virtual_selected_idx` だけを消し、`selected_actor_dfs_ids` を残す）
  - undo_selection.rs:186-202（`had_selection` は古い番号でも true になり、その番号の今の道筋を控える）
  - コミット edccecec
- 何が起きるか:
  1. R{A,B,C} で B（2 番）を選んで Delete する。`selected_actor_dfs_ids=[2]` が残る。
  2. Ctrl+Z を押すと、2 番（今は C）の道筋が控えられ、組み直した木で C=3 に引かれる。
  3. `SELECTED:999000003` と C のインスペクタが届き、続く Delete・F2・値の編集は C に当たる。
  - 複数を消して戻すと、無関係な複数のアクタが選ばれる。
  - edccecec より前の Undo は SELECTED を送らなかった。
- 確認: 済（監督役が削除の後始末と capture を読んだ）
- 直し方の案: 削除（AI の削除も含む）で `selected_actor_dfs_ids.clear()` する。capture では、主が None なら選択を控えない。

## 10. 同じ名前の兄弟がある木では、改名・並べ替え・名前の入れ替え・付け替えの Undo で、別の同じ名前のアクタへ選択が移る  【重さ: 中】
- 場所:
  - runtime/.../undo_selection.rs:152-165（`resolve_saved_actor` は道筋で引けると、形が同じでもそちらを採る）
  - docs/editor_screen_preview.md:197（「名前の変更の Undo では選択が残る」）
  - コミット edccecec
- 何が起きるか（ヒエラルキー・インスペクタ・ランタイムの選択はそろうが、選ばれるのは別のアクタ）:
  - R{X,Y} で X を "Y" に改名し（R{Y',Y}）、Y' を選んで Ctrl+Z する。鍵 (Y,0) が元の Y に引けてしまい、形は同じなのに Y が選ばれる。
  - R{A,A,A} で先頭を "B" に改名し、3 番目（鍵 (A,1)）を選んで Ctrl+Z すると、2 番目へ移る。
  - 名前の入れ替えを 2 回 Undo すると、表示名が同じまま中身が別のアクタになる。
  - コミットの残件（「同名の兄弟の途中を消した Undo」）より範囲が広い。
- 確認: 済（監督役が引く順を読み、例を手で追った）
- 直し方の案: 同じ名前の兄弟が 2 つ以上ある段を通る鍵は採らず、形が同じなら番号を、違えば選択を外す。根本は、保存しない一時 ID（backlog の案）。docs も直す。

## 11. 2D のキャンバスへ部品を足すと SELECTED がヒエラルキーより先に届き、インスペクタが新しい子ではなく別のアクタへ移る  【重さ: 中】
- 場所:
  - runtime/.../canvas_component_ops.rs:160-170・281-291（`send_selected()` → `send_hierarchy()`。後者は間引かれうる）
  - editor/src/Panels/HierarchyPanel.xaml.cs:735-758（Moved のときインスペクタへ知らせる）
  - コミット edccecec（知らせる処理が足されて、この順番が害になった）
- 何が起きるか:
  1. ［HUD（子 Title）, Menu］で HUD に Sprite を足す。
  2. エディタは古い木で `SELECTED:2` を処理し、2 番の行＝Menu を選ぶ。
  3. 続く同期で Menu が 3 へ動いたと判定し、インスペクタは Menu を出す。
  - 結果: ランタイムの選択（ギズモ）は新しい子、ヒエラルキーとインスペクタは Menu で、食い違う。
- 確認: 済（監督役が送る順を読んだ）
- 直し方の案: 貼り付けと同じく、SELECTED の前にヒエラルキーを即時（`send_hierarchy_now` / flush）で送る。send_selected の前で一律に flush してもよい。

## 12. ヒエラルキーの目アイコンが作ったときの DFS を握ったままで、手前の増減の後に押すと別のアクタの表示を切り替え、保存される  【重さ: 中（既存。edccecec が触った行）】
- 場所: editor/src/Panels/HierarchyPanel.xaml.cs:852-869（ラムダが `node.Id`・`node.SelfVisible`・`node.IsPreview` を握る）・689-701（`HeaderDiffers` は Id を比べないので、行を使い回すとヘッダを作り直さない）
- 何が起きるか: R{A,B,C} で A を消す（またはプレビューの出し入れ・貼り付け・Undo）。B の行は使い回され、目は古い 2 番を握る。B の目を押すと `SET_VISIBLE:2,…` で **C** が隠れ、普通の編集として積まれ、保存される。
- 確認: 済（監督役が HeaderDiffers を読んだ）
- 直し方の案: クリックのときに行の `TreeViewItem.Tag` の ActorNode から読む。または HeaderDiffers に Id を含める。

## 13. インスペクタのロック中、同じ名前のアクタがロックした番号へずれ込むと、表示はロックしたアクタで書き先は別のアクタになる  【重さ: 中（既存。#1 の残り）】
- 場所: editor/src/Panels/InspectorPanel.Lock.cs:113-127・212-249、InspectorPanel.xaml.cs:351-355・476
- 何が起きるか:
  1. List{I0,I1,I2}（全部 "Item"）で I0 を消し、I1 をロックして別のアクタを選ぶ。
  2. Ctrl+Z で I0 が戻る。ロックの確かめは「同じ番号に同じ名前」が当たるので続く。
  3. 表示は I1 の値のまま、`_currentActorId` は I0 を指す。ランタイムの SELECTED / ACTOR_COMPONENTS はロックで捨てられる。
  4. 次の編集は I0 に当たる。
- 確認: 未（サブレビュア D の読み。ロックが切り替えを弾く所だけ監督役が読んだ）
- 直し方の案: ロック中も、ロックした番号のアクタの ACTOR_COMPONENTS を取り直して名前と中身を照合する。または、同じ名前の兄弟がいる段ではロックを外す。

## 14. 書き戻しで付けた「読み直し」の印がタブを閉じても残り、アクタタブの未保存の編集を確認なしで捨てる  【重さ: 中】
- 場所:
  - editor/src/MainWindow.Prefab.cs:90・306-331（`_staleActorTabPaths` は Add と読み直し時の Remove の 2 か所だけ）
  - runtime/.../ipc_handler.rs:1114-1170（OPEN_ACTOR はその世界線を消して読み直し、Undo の履歴を作り直す）
  - コミット 3111058f
- 何が起きるか:
  - (a) P をアクタタブで開いたまま Play し、P を書き戻す。停止後、そのタブを表示せずに閉じる。後で P を開き直して編集し、保存前にシーンタブへ移ってから戻ると、`OPEN_ACTOR` で読み直され、開き直した後の編集と Undo が消える。
  - (b) P のタブに未保存の編集を残したまま Play して P を書き戻すと、停止時にタブが表示中なら、すぐ読み直されて編集が消える。
- 確認: 済（監督役が印の出し入れと TryReloadStaleActorTab を読んだ。タブ単位の未保存の印が無いことは A・B の読み）
- 直し方の案: タブを閉じる・新しく開くときに印を消す。読み直す前に確認を出す（タブ単位の未保存の判定が要る）。

## 15. 外部変更の監視は起動時に内容を知らないので、最初の書き込みは内容が同じでも再展開する（docs §7.1 の規則 3 と食い違い）  【重さ: 中】
- 場所: editor/src/Reload/PrefabExternalChangeTracker.cs:127・278-295（`_knownHashes` を埋めるのは Classify と NotifySelfWriteFinished だけ）、テスト PrefabAutoReloadTests.cs:162-172（初回は Changed を固定）、docs/editor_auto_reload.md:147（コミット 343b90a0）
- 何が起きるか: 起動後（または設定をオフ→オンにした後）に、まだ一度もイベントの来ていない .actor へ、同じ内容の書き込み・コピー・touch が来る。判定は Changed になり、既定の設定では `PREFAB_REAPPLY_PATH` が走ってインスタンスの上書きが消える（Undo できるが、#16 の条件では消える）。
- 確認: 済（監督役が追跡器を読んだ）
- 直し方の案: #5 の「ずれたインスタンスだけ再展開」で根本的に防ぐ。または監視の開始時に、既存の .actor のハッシュを背景で覚える。docs とテストを実際の規則に合わせる。

## 16. 自動の再展開の前提「Undo 1 操作で戻せる」が、OPEN_ACTOR / SET_ACTIVE_WORLD_LINE の履歴の作り直しで成り立たない  【重さ: 中（根は既存。自動の入口が増えた）】
- 場所: runtime/.../ipc_handler.rs:1170（OPEN_ACTOR）・1220（SET_ACTIVE_WORLD_LINE）。どちらも `undo_history = UndoHistory::new()`。editor/src/MainWindow.Prefab.cs:284-300（停止時の反映の直後に古いタブの OPEN_ACTOR）。コミット 3111058f / 343b90a0
- 何が起きるか:
  - (a) 停止時に表示中のアクタタブが古いと、`PREFAB_REAPPLY_PATH` が Undo を積んだ直後に `OPEN_ACTOR` が履歴を消す。それでも「Ctrl+Z で戻せます」のトーストが出る。
  - (b) アクタタブで作業中に AI 等が使用中のプレハブを書き換えると、再展開の Undo はシーンタブへ移った時点で消える。
- 確認: 済（監督役が履歴の作り直しの 2 か所を読んだ）
- 直し方の案: 自動の再展開をアクタタブ表示中に行うときは、Ctrl+Z を約束せずバナーに落とす。停止時は OPEN_ACTOR を再展開より前に送る。根本は世界線ごとの履歴。

## 17. Play 中の当て直しでノードを消す・作り直すと、その下のスクリプトの生成物（ScreenStack の画面・リストの行）も消える  【重さ: 中】
- 場所: runtime/.../prefab_live_patch/apply.rs:318-323（ファイルから消えた子）・348-359（形が変わった子）。どちらも `despawn_actor_recursive` で部分木ごと消す。plan.rs:215-237。docs/editor_prefab.md:247（「スクリプトが生成した部分木…消さず・触らず」）。コミット 3111058f
- 何が起きるか: Play 中に shell.actor の `Screens` の名前を外部で変えると、3 方向の当て直しは「消して作る」になる。そのタブの画面は全部消え、スクリプトが握る `_screens` などのハンドルは死ぬ。次の Push は親が無効なので、シーンの根の直下へ作られる（script_scene_ops.rs:191-195）。2D/3D・フォルダの別を変えた場合も同じ。
- 確認: 済（監督役が apply.rs の破棄を読んだ）
- 直し方の案: 壊す前に配下の生成物を外し、作り直したノード（同じ道筋）か生き残る祖先へ付け直す。できなければそのノードは消さずにログを出す。

## 18. ルートでの SetAsFirstSibling / SetSiblingIndex が地形のチャンクフォルダの中へ入り、Play を止めても残ってシーンに保存される  【重さ: 中（起きればシーンのファイルが壊れる）】
- 場所:
  - runtime/src/engine/core/scripting/node_tree.rs:41-63（`logical_roots` はトップレベルのフォルダを展開する）・127-168・222-239（`insert_before` は種別を見ずにフォルダの中へ入る）
  - terrain_ops.rs:1904-1912・2118-2124（地形ルートもチャンクの器も `Actor::new_folder`）
  - play_snapshot.rs:60-73（地形ルートは `Keep`＝現物保持）
  - コミット f0f7088d
- 何が起きるか:
  1. トップレベルの先頭が `terrain` のシーンで、ルートのアクタに `SetAsFirstSibling()`（または k がチャンクの範囲に入る `SetSiblingIndex(k)`）を呼ぶ。
  2. 論理のルートの兄弟にはチャンクのメッシュが並んでいるので、そのアクタは `terrain/chunk_x_y_z` の中へ移される。
  3. Play を止めても地形は現物保持なので、Play 中のアクタが地形の中に残る。元の位置にはスナップショットから作り直した同じアクタが居る（二重化）。
  4. 保存すると .scene に入る。
  - 移す先の 2D/3D を見ないので、SetParent なら断られる配置（2D のルートキャンバスを 3D フォルダへ）もできる。docs §7（「ルートの兄弟は同じシーンのルート」）・コミットの「SetParent と同じ拒否規則」とも食い違う。
- 確認: 済（監督役が論理のルート・差し込み・地形のフォルダ・Keep を読んだ。実行はしていない）
- 直し方の案: 地形ルートの部分木と種別の違うフォルダへは入れない。Play 中は、現物保持の部分木（地形・world_line>0）を対象・親にする動的ノード操作を断る。

## 19. 物理の ID が Play 開始時の DFS 番号のままなので、Create(親)・SetSiblingIndex・当て直しで木の形が変わると、物理の姿勢と衝突のイベントが別のアクタへ届く  【重さ: 中（根は既存）】
- 場所: runtime/.../physics_ops.rs:867-892（開始時に DFS の数え上げを ID にする）・1032-1060（毎フレーム今の木を数え直して同じ番号のアクタへ書く）。コミット f0f7088d / 3111058f（新しい入口）
- 何が起きるか: 3D の Play 中、動的な Rigidbody より DFS で前にある親へ `Create3D("Marker", player)` する、または並べ替える。以後毎フレーム、物理の姿勢がずれた先の別のアクタの Transform とモデル行列に書かれ、OnCollision* / OnTrigger* も別のスクリプトに届く。Instantiate(親)・SetParent・Destroy でも以前から起きる。backlog の「コライダーが反映されない」はこのずれに触れていない。
- 確認: 済（監督役が集めると当てるの数え方を読んだ）
- 直し方の案: 開始時に ID → Entity の表を控えて引く。最低限 docs §7 に注意書きを入れる。

## 20. ModalHost: 面のプレハブが読めないと作りかけの数が減らず、その種類の戻るを永久に飲み込む  【重さ: 中】
- 場所: scripting/src/Api/UI/Navigation/ModalHost.cs:148-163（`Open` は `root.IsValid` だけを見る）・204-217（`HandleBack` は `_opening[kind] > 0` で true）・89（Count）、runtime/.../host_api.rs:2291-2315（`ffi_instantiate_under` はパスが空でなければ予約して 1 を返し、読み込みの失敗はフレーム末尾で despawn するだけ）（コミット 0045ce2b）
- 何が起きるか: templates/ui を取り込み直していないプロジェクト（`assets/ui/prefabs/popup.actor` が無い）で `Popup.Show(...)` を呼ぶ。または `ShowPlane` / `ShowPopup` に誤ったパスを渡す。面のスクリプトが始まらないので `_opening` が 1 のまま残り、次のことが続く。
  - Overlay の戻るが毎回飲まれて、画面のスタックへ届かない。
  - 手札の `WhenClosed` は終わらない。
  - Android の予測型の戻るでも、アプリが常に「受ける」になる。
  - 直るのは `CloseAll` を呼んだときだけ。
- 確認: 済（監督役が Open・HandleBack・数を減らす 2 か所・ffi_instantiate_under を読んだ）
- 直し方の案: 作りかけに上限フレームを持たせ、`Claim` が来なければ取りやめてエラーを出し、手札を閉じる。

## 21. `Push(GameObject, …, ReturnToParent)` を中身を作ったのと同じフレームに呼ぶと、下ろしたとき中身がシーンの根へ見えたまま出る  【重さ: 中】
- 場所: scripting/src/Api/UI/Navigation/ScreenStack.Content.cs:37-44・55-61（`content.Parent` をその場で控える）・119-129（親が無効なら `SetParent(null)`）、ScreenStack.cs:422（Visible=true にする）、runtime/.../host_api.rs:2376-2406（`ffi_parent_of` は今の木を読むので、同じフレームに作った実体は None）（コミット 0045ce2b）
- 何が起きるか: `_body ??= GameObject.Instantiate(path, _store); root.Push(_body, …, ScreenContentRelease.ReturnToParent);` の形（docs は「できあがっていなくてもよい」）だと、控えた親は無効になる。Pop すると中身はシーンの根の直下へ移り、見えたまま残る。以後の Push も、根の直下を親として控える。
- 確認: 済（監督役が親の控え方・戻し方・parent_of を読んだ。根の直下でどう描かれ、押せるかは推測）
- 直し方の案: 親は積んだ時点ではなく、`TakeContent`（枠ができたフレーム）で読んで控える。親が無ければ根へ移さず、隠して警告する。

## 22. 同じ中身（GameObject）を 2 回積んでも防がず、上の段を下ろすと下の段が空の枠になる  【重さ: 中】
- 場所: ScreenStack.Content.cs:63-68（`AcceptSupplied` は `IsValid` しか見ない）・99-110・119-134、ScreenStack.cs:417-425・713-724（コミット 0045ce2b）
- 何が起きるか: 二度押しで `Push(body)` が 2 回呼ばれると、中身は 2 段目の枠へ移り、1 段目は空になる。既定の Destroy では、戻るで 2 段目を下ろすと中身も消える。1 段目は空の不透明な枠で残り、アプリの持つ参照も死ぬ。
- 確認: 済（監督役が受け付けの条件を読んだ）
- 直し方の案: 渡された中身がほかの段で使用中なら断る。`ReleaseContent` では、中身がこの枠の下にあるときだけ扱う。

## 23. `CloseAll` で閉じた面の結果を種類だけで決めるので、帯が Dialog のポップアップや自前の面で `DialogResult.Dismissed` が返る（docs は null）  【重さ: 中】
- 場所: scripting/src/Api/UI/Navigation/Model/ModalCloseOrder.cs:44（`kind == ModalKind.Dialog ? Dismissed : null`）、ModalHost.CloseAll.cs:46・76（コミット 0045ce2b）
- 何が起きるか: `PopupOptions.Kind = Dialog`（docs が「シートの上に出すなら Dialog」と案内する設定）や、`ShowPlane(ModalKind.Dialog, …)` で開いた自前の面を `CloseAll` で閉じると、普通の `ModalHandle` に箱入りの `DialogResult.Dismissed` が入る。戻るや幕で閉じたときは null。`(MyResult?)await h.WhenClosed` のような受け手は InvalidCastException になる（受け手の書き方は推測）。
- 確認: 済（監督役が ResultFor と 2 か所の呼び出しを読んだ）
- 直し方の案: 「手札が DialogHandle なら Dismissed、それ以外は null」で決める。

## 24. 使い回す作り置き（PrewarmMode.Reuse）を KeepState=false の画面で使うと、OnScreenExit なしで OnScreenEnter が 2 回届く  【重さ: 中】
- 場所: scripting/src/Api/UI/Navigation/ScreenStack.cs:697（Settle の手放しは `notifyExit: false`）・713-724、ScreenStack.Prewarm.cs:251-274（`ReturnPrewarm` が同じ UiScreen を Ready に戻す）（コミット 0045ce2b）
- 何が起きるか: 覆われて手放されると、Exit を受けないまま作り置きへ戻り、次に貸されると再び Enter を受ける。docs は「外れるたびに OnScreenExit」と約束し、Enter で取り Exit で返す数え上げ（画面の点灯・描き続け）を勧めているので、その数が 1 回ずつ漏れる。
- 確認: 済（監督役が Settle → DestroyEntry → ReturnPrewarm を読んだ）
- 直し方の案: 作り置きへ戻すときは `notifyExit` によらず OnScreenExit を届ける。または Reuse の画面は KeepState を常に true として扱う。

## 25. 双方向の留め金が、購読側の補正（範囲に収める・拒否して戻す）を部品へ返さず、表示と値が食い違ったまま残る  【重さ: 中】
- 場所: scripting/src/Api/Binding/Model/TwoWayBinding.cs:76-117（`OnTargetChanged` が `_writingSource=true` のまま観測値を書き、その中で届く補正後の値を `OnSourceChanged` が捨てる）、docs/ui_binding.md:156（コミット 1232cb34）
- 何が起きるか: `Bind.Toggle(this, node, _notify)` と `_notify.Subscribe(this, on => { if (on && !granted) _notify.Value = false; })` を置き、利用者がトグルを ON にする。トグルは ON の見た目のまま、`_notify.Value` は false で、警告も出ない。Slider を上限で抑える、Selection で選べない項目を戻す、も同じ。「購読の中で戻す」形は ObservableTests.cs:134-154 が正式な使い方として試している。
- 確認: 済（監督役が留め金の 2 つの関数を読んだ）
- 直し方の案: 留め金を外した後で `_source.Value` と部品へ書いた値を比べ、違えば `WriteTarget(_source.Value)` する。

## 26. コメントに書いただけの末尾 `/` のフォルダ参照で、フォルダ丸ごと（除外ルールに当たるファイルや .cs も）が新たに pak に入る（docs とテストの「従来どおり」と食い違う）  【重さ: 中】
- 場所: editor/src/Packaging/Collect/AssetPathUtil.cs:47（`TrimEnd('/')`）、AssetCollector.cs:720-729（③ のフォルダ参照は ⑤ のコメント判定より前）・856-866（`IncludeFolder` は除外ルールを見ない）、docs/packaging.md:167、テスト CommentReferenceTests.cs:171・180（コミット 95723ec7）
- 何が起きるか: プロジェクトの .cs に `/// 原画は <c>assets://art/</c> に置く` のような文書コメントがあると、修正前は何も入らなかった（末尾 `/` が照合に外れた）。修正後は `art/` の配下が全部 pak に入る。`.psd`・`.blend`・`Thumbs.db` も警告付きで入り、`.cs` は警告も出ない。テンプレートのインポート（`CollectFrom`）も同じ道を通るので、ライブラリのそのフォルダが丸ごとプロジェクトへコピーされる。今の Wake or Pay・templates・scripting/src には該当するコメントは無い（G1 が読み取りで確認）。
- 確認: 済（監督役が TrimEnd・③ と ⑤ の順・IncludeFolder を読んだ）
- 直し方の案: ③ で、`OnlyInComments` かつ生の文字列が `/` で終わる参照はフォルダを展開しない（修正前と同じ結果）。docs とテストの期待を合わせる。

---

## 27. （低・まとめ）Play 中の当て直しの細部  【重さ: 低】
- 場所: runtime/.../prefab_live_patch/plan.rs:157-172・215-221、ops.rs:171-176、apply.rs:88-119・181-187・213-221・274-301（コミット 3111058f）
- 何が起きるか:
  - 2 方向（元の版が無い）では、スクリプトが枠へ移した・改名した・Destroy したノードを「ファイルにあるのに無い」と見て作り直す。古い版の Shell では `AlarmList` などが `Screens` の直下に 2 個目として出て、OnStart も走る（Play の間だけ）。docs の 2 方向の条件に、入れ子のインスタンスの版が古い場合が載っていない。
  - 同じ名前の兄弟の 1 個目を Destroy すると、ファイルの 1 個目への変更が元の 2 個目に当たる。
  - 既存の 3D ノードへファイルで ModelComponent を足すと、インスタンス行列が根の配置へ移されず、原点付近に描かれる（新しいノードは delta で移している）。
  - 入れ子のインスタンスを別のプレハブへ差し替えても `prefab_source` / `prefab_hash` が古いまま。`NeedsRebuild` でスロットのエンティティが替わり、GetComponent で握った参照が無効になることが docs に無い。
- 確認: 未（サブレビュア A の読み）
- 直し方の案: 2 方向では、同じ名前の道筋のノードが生きた木の別の場所にあれば作らない。作ったモデルのスロットに `delta_3d` を掛ける。入れ子の `prefab_source` が変わったら Replace にする。docs に追記する。

## 28. （低）書き戻しの失敗の扱い: 書いた後に ERROR を返す・今のファイルが読めなくても上書きする  【重さ: 低】
- 場所: runtime/.../prefab_live_patch/write_back.rs:70-73・79-88、editor/src/MainWindow.Prefab.cs:262-273（コミット 3111058f）
- 何が起きるか: 保存に成功した後に `live_patch_prefab_path(&vpath)?` が失敗すると `PREFAB_WRITE_BACK_ERROR` を返し、エディタは停止時の反映もタブの印も予約しない（ファイルは書かれている）。今のファイルが読めない場合（形式の移行に断られた・消された）も、ログだけ出して上書き・作り直しする。
- 確認: 済（監督役）
- 直し方の案: 書いたら DONE（警告付き）を返す。今のファイルが移行の失敗・NotFound なら断る。

## 29. （低・まとめ）監視と停止後の反映の隙間  【重さ: 低】
- 場所: editor/src/MainWindow.SceneAutoReload.cs:75-80、RuntimeManager.cs:1005-1021・1916-1937、PrefabPlayReapplyQueue.cs:101-108、PrefabAutoReloader.cs:95-107、MainWindow.PrefabAutoReload.cs:89-95、MainWindow.Prefab.cs:383-466（コミット 343b90a0 / 3111058f）
- 何が起きるか:
  - ENTER_PLAY から PLAY_ENTERED までの間は State が Edit のままなので、外部変更で `PREFAB_REAPPLY_PATH` を送り、ランタイムは Play で丸ごと再展開する（OnStart が走り直す）。待ち行列にも入らず、停止後の Edit に反映されない。書き戻しの直後に停止すると、DONE の時点で Edit になっていて Remember が抜ける。
  - FileSystemWatcher の `Error`（バッファの溢れ）を購読しておらず、大量の書き換え（VCS の取得）で .actor のイベントが黙って落ちる。監視先のフォルダの置き換えで止まったままになる。
  - 自動反映オフで［無視］した後、別のプレハブの外部変更でバナーが無視した分まで含めて出し直し、［更新する］で意図して残した上書きまで再展開する。
- 確認: 一部済（Error を購読していないことは監督役が読んだ。残りはサブレビュア B の読み）
- 直し方の案: 起動中は Play 扱いにし、ランタイムは Play 中の PREFAB_REAPPLY_PATH を断る。Error で警告と PREFAB_STATUS を出して張り直す。外部変更のバナーは変わったパスだけを対象にする。

## 30. （低・まとめ）動的ノード API の細部  【重さ: 低】
- 場所: runtime/src/engine/core/scripting/host_api/nodes.rs:243-257、host_api.rs:2302-2304、script_node_ops.rs:94-140、audio/mod.rs:563-567・665-670、script_system.rs:59-75（コミット f0f7088d）
- 何が起きるか:
  - 同じフレームに Instantiate した 2D プレハブの下への `Create(Auto)` は、予約エンティティが Transform しか持たないので 3D と判定され、ルート直下に作られる（警告のみ）。
  - `AddScript` は `[RequireComponent]` / `[DisallowMultipleComponent]` を通らない（強制はエディタのインスペクタだけ）。
  - 鳴っているループの AudioSource を `RemoveComponent` しても音が止まらない（Destroy も同じで既存）。
  - AddScript したインスタンスは、同じフレームの EndFrame が OnStart より前に、gameObject 未束縛のまま呼ばれる（Instantiate と同じで既存）。
- 確認: 未（サブレビュア C の読み）
- 直し方の案: 親が生成待ちなら Auto を断って Create2D/3D を案内する。AddScript の適用時に属性を照会する（または docs に明記）。Audio のスロットを外すときに `stop_component`。EndFrame は `started == false` を飛ばす。

## 31. （低・まとめ）SEED.UI の細部  【重さ: 低】
- 場所: scripting/src/Api/UI/Navigation/ModalPlane.cs:87-89・129-159、Dialog.cs:135-150・216-225、ModalHost.CloseAll.cs:37-48・59-78、ScreenStack.Prewarm.cs:160-165、Model/PrewarmSlot.cs:106-128（コミット 0045ce2b・bfd3121a）
- 何が起きるか:
  - 面のスクリプトが始まる前のダイアログを `Close()`（動きあり）で閉じると、札が 0.9 倍で幕なしに 0.2 秒見える（docs の例 `Dialog.ShowProgress(…)` → すぐ Close の形）。
  - `CloseAll` の中で手札の知らせが面を開くと、その面は閉じずに残る。知らせの中から `CloseAll` を呼び直すと、取りやめ済みの作りかけでも `_opening` を減らし、新しい作りかけの分まで減る。
  - `ModalPlane.RequestClose` でダイアログを閉じる口だけは Choose を通らず、`InputText` が null のまま（前回の #9 の残り。docs は手札の Close と並べて案内している）。
  - 何も積まれていないスタックで `SetRoot` より先に温め描きをすると、作り置きの画面が 1〜2 フレーム見えて押せる。
- 確認: 未（サブレビュア E の読み。E は CloseAll の再入で例外が出ないことは確認済み）
- 直し方の案: 面の開始で外からの閉じを先に見て準備中のまま閉じる。`_pending.Remove` が true のときだけ数を減らす。Dialog が RequestClose を Choose へ回す。温め描きは `Depth > 0` で根が不透明のときだけ行う。

## 32. （低・まとめ）データバインディングの細部  【重さ: 低】
- 場所: scripting/src/Api/Binding/Model/ValueBinding.cs:40-41、Bind.cs:26、Bind.Text.cs:51・63-72、Targets/VisibleTarget.cs:30-35、Targets/WidgetRef.cs:44-66、ScriptBridge.cs:246-261（コミット 1232cb34）
- 何が起きるか:
  - 作った時点の最初の当てで変換が例外を投げると、購読だけが残り owner にも預けられない（破棄後も書き続ける）。
  - Instantiate した直後の、根を非表示で保存したプレハブへ `Bind.Visible(true)` を結ぶと、読み戻しが true なので書かれず、表示されないまま残る。
  - 当てる先が無効、または部品の付かないノードでも警告が出ず、owner 無しだと毎フレーム待ち続ける（LocalizedLabel は 30 フレームで警告するのに）。
  - owner 無しの L10n 版 `Bind.Text` は静的な Events に握られ、言語を切り替えるまで解放されない（docs の「観測値が捨てられるまで」と食い違う）。
  - フレームに 1 回の判定に使う f32 の時間が丸めで止まると（144 Hz で約 36 時間）、`BindingFrame.Tick` が二度と走らない。
- 確認: 未（サブレビュア F の読み）
- 直し方の案: 最初の当てを try で包み、失敗したら外して再送出する。読み戻しではなく最後に書いた値と比べる。一定フレームで 1 回警告する。docs に寿命を書く。フレーム番号を NativeFrameContext で渡す。

## 33. （低・まとめ）ローカライズの細部  【重さ: 低】
- 場所: scripting/src/Api/Localization/Model/LocaleLanguage.cs:17、LocalePaths.cs:38-40、LocaleIndex.cs:183-189、LocaleCulture.cs:108-137、LocaleFormatter.cs:161-181、docs/localization.md:110-111・205（コミット d996bcb5）
- 何が起きるか:
  - index.json の `"code": "pt_BR"` は `pt-BR` に書き換わり、`assets://locale/pt-BR.json` を探すので、`pt_BR.json` に置いた表は読めず fallback へ落ちる（コメントの「書いたまま」は誤り）。
  - docs の「Configure はほかのスクリプトが文を引く前に呼ぶ」は、OnStart の順が決まっていないので守れない。先に Get したものは `[key]` のまま残る（Changed を受けるものは直る）。
  - FormatDate 等は FormatException しか捕まえず、グレゴリオ暦でない文化で範囲外の日付だと例外が呼び出し元へ飛ぶ（推測）。
- 確認: 未（サブレビュア F の読み）
- 直し方の案: docs とコメントにファイル名の規則を書く。Configure の説明を実際の振る舞いに合わせる。ArgumentOutOfRangeException も捕まえる。

## 34. （低・まとめ）収録・コメント判定・最近の一覧・テストのビルド  【重さ: 低】
- 場所: editor/src/Packaging/Collect/AssetCollector.cs:725・731-733・745-746、CSharpCommentSpans.cs:109-116、editor/src/Preview/PreviewRecentStore.cs:108-120・157-183、editor/tests/LocalizationTests/LocalizationTests.csproj:38（コミット 95723ec7、合流 77180ec5）
- 何が起きるか:
  - `"assets://models/char.001/"` のように名前にドット＋英数字を含むフォルダの参照は、③ が `.001` を拡張子とみなすので効かず、❌ の欠落になり、中身は pak に入らない。
  - 閉じない `/*` が `#if false` の中や `#region` の文にあると、その後ろのコードが全部コメント扱いになり、欠落の警告が消える。
  - 最近のプレビューの一覧: `Push` の中の読み込みが一時的な IOException になると空の一覧にこの 1 件を足して書くので、全プロジェクトの一覧が消える（コメントの「失うのは 1 件だけ」と食い違う）。
  - LocalizationTests.csproj が `CSharpCommentSpans.cs` をリンクしておらず、AssetReferenceScanner.cs がそれを使うので、このテストはビルドできない（95723ec7 はほかの 7 つの csproj にだけ足した。CI はテストの csproj を含まない）。
- 確認: 一部済（最近の一覧の catch と csproj のリンクは監督役が読んだ。残りはサブレビュア G1 の読み）
- 直し方の案: ③ の拡張子の条件を外す。行頭の `#` の行は行末まで飛ばす。IO の失敗のときは書かない。csproj にリンクを足す。

## 35. （低・まとめ）一括アップグレードの待ち・AI のプレビューの判定・ガードの漏れ  【重さ: 低】
- 場所: editor/src/MainWindow.Migration.cs:85-91・127-137・162-166、editor/src/Scene/SceneAutoReloader.cs:263-277、editor/src/AI/Tools/EditorCommandExecutor.PreviewGuard.cs:28-31、runtime/.../editor_preview/guard.rs:61-105（コミット edccecec）
- 何が起きるか:
  - Play 中にアップグレードを実行すると、ForceReload は予約に回るだけなのに「読み直しました」と出る。［いいえ（破棄）］も効かない。
  - 保存の完了が来ないと `_pendingUpgradeTarget` が残り、後の無関係な保存の完了で窓が突然開く。
  - AI ツールのプレビューの判定は手元のヒエラルキー（最大 100 ms 遅れ）で見るので、誤って通すとランタイムは断るが、AI には「設定しました」と返る。
  - `ADD_CONTROL_POINT_AT_SCREEN:{プレビューの中}` が通る（MCP の `seed_send_ipc` だけ）。選択がプレビューの根だけのときの PASTE を誤って断る。
- 確認: 未（サブレビュア D の読み）
- 直し方の案: Play 中はアップグレードを断る。待ちはシーンの読み込み・再起動・時間切れで消す。ガードの表に足す。

## 36. （低・まとめ）MCP ツール  【重さ: 低（一部は backlog に既知）】
- 場所: editor/src/MainWindow.AiHost.Tools.cs:57-81・164-196、editor/src/Templates/Actors/TemplateActorIpc.cs:34、editor/SeedMcpServer/Program.cs:1018-1041（seed_batch の cmd の enum）、docs/editor_mcp.md:184-189（コミット df390401）
- 何が起きるか:
  - `seed_template_actor(add)` は、汎用の `SCENE_MODIFIED` の最初の 1 行を自分の成功とみなす。利用者や別の操作の編集が届くと、失敗でも成功と返す。
  - `platform_sim` / `gpu_mem_report` / `preview` の応答待ちには相関 ID が無い。時間切れの直後に同じ種類を呼ぶと、遅れた古い応答を自分の答えにする。docs §5.6.A の例は `seed_platform_sim` を 3 連続で呼ぶ。
  - 上の 2 つは docs/backlog.md:220 に既知として記載がある。
  - docs は `seed_batch` から `profile` を呼べると書くが、cmd の enum に `profile` が無い（既存。「enum を更新」の際に見落とし）。
- 確認: 済（監督役が backlog の記載と enum を読んだ）
- 直し方の案: 専用の応答（`ADD_TEMPLATE_ACTOR_DONE` / `_ERROR`）と、要求ごとの連番を足す。enum に `profile` を足す。

## 37. （低・まとめ）docs と実装の食い違い  【重さ: 低】
- docs/scripting_api.md:1303・1317: `Parent` はフォルダを含む直接の親なのに、論理の読みと並べている。フォルダの中では `go.Parent.GetChild(go.SiblingIndex) != go` になる。
- docs/scripting_api.md:1315: 3D フォルダへの SetSiblingIndex は FFI の入口で黙って 0 を返し、`[Script] SetSiblingIndex 拒否` の行は出ない（nodes.rs:271）。
- docs/scripting_api.md:1317: 「読みはフレームの始めの木」は EndFrame では成り立たない（途中の適用が先に走る）。
- docs/ui_navigation.md:441: 戻るの問いが「`ModalHost.Count(種類) > 0`」のままだが、実装は Park した面を除く。
- docs/scripting_api.md:4185: `slider.TickCount` を欄として案内しているが、動き始めた後に直接書くと次の Refresh まで変わらない（`SetTickCount` だけが Refresh する）。
- docs/ui_navigation.md:178: ReturnToParent の中身でも、スタックごと消えると枠と一緒に消える。
- ModalHost.cs:139 の「作れなければ閉じた手札」は、読み込みの失敗では成り立たない（#20）。
- docs/editor_prefab.md §2: 「現在の 4 つの入口」が、外部変更と停止後の再展開も同じ設定で決まることを書いていない。
- docs/backlog.md:4067-4070: EXPORT_ACTOR の注記は「中身は同じ」とあるが、既存のプレハブへ上書きでアクタファイル化すると、ほかのインスタンスが新しい中身で再展開される。
- docs/editor_auto_reload.md §7.1: 「0 件なら黙る」とあるが、状態表示は件数に関わらず出る。
- 確認: 未（サブレビュア B・C・E の読み）

---

## 確認したが問題なしと判断した点
1. **FFI**: `ScriptHostApi` の Rust（host_api.rs:3937-4045）と C#（ScriptHost.cs:1562-1671）を先頭から突き合わせた。53 欄すべてで順序・数・型・ポインタか値かが一致する（監督役が並べて照合、C も同じ結論）。静的な表は新しい 4 欄に `nodes::ffi_*` を当てている。ほかのレーンは足していない。`extern "system"` と `unmanaged[Cdecl]` は x64 / arm64 で同じ。
2. **動的ノードの unsafe と保留の表**: 次の点を確認した（C）。
   - 出力は null と容量を確かめてから書き、文字列は保存前に複製する。
   - Entity の比較は世代込み。
   - 同じフレームでの Add→Remove→Get、Add→Destroy、Destroy→Add は辻褄が合う。
   - Transform / CanvasTransform は外せない。
3. **spawned_by_script**: 印は `ActorData` に無いので、.scene・.actor・Undo・Play の写しのどれにも出ない。Create（script_node_ops.rs:49）と Instantiate（script_scene_ops.rs:174）の両方で立つ。`to_data` は子を 1 対 1 で並べるので、刈る関数の添字合わせは正しい（監督役・A）。
4. **書き込みの経路**:
   - `actor_file::save` → `safe_write`（.tmp からの rename・`.backup/` に 10 世代）を通る。
   - 根の `prefab_source` / `prefab_hash` を外す。
   - 拡張子は参照パスのままなので、.actor と .actor2d を取り違えない。
   - 入れ子のインスタンスは参照と中身のまま書かれる（A）。
5. **プレビューと再展開**: プレビューの木は `prefab_source` を外してあるので（editor_preview/tree.rs:228-233）、自動の `PREFAB_REAPPLY_PATH` も当て直しもプレビューに触れない（監督役）。
6. **当て直しの元の版**: 当て直しの後に新しい版を控え、インスタンスの版も揃えるので、2 回目は 3 方向で 1 回目の変更を当て直さない。書き戻しの直後に監視が同じ内容で当て直しを送っても、並びを揃え直すだけで無害（A）。
7. **監視の自己書き込みと設定**: 次の点を確認した（監督役・B）。
   - FileSystemWatcher のイベントは UI スレッドの追跡器へ載せ替え、鍵は大文字小文字を無視する。
   - SAVE_ACTOR は送る前に窓を開け、SAVE_OK でハッシュを覚えるので、二重の再展開にならない。
   - 「保存時に自動反映」オフは迂回されない（外部変更でも PREFAB_STATUS だけ）。
8. **SEED.UI の再入**: 次の点を確認した（E）。
   - `CloseAll(false)` は写しを回すので、手札の知らせの中で開いても閉じても例外にならない。
   - 作り置きは 2 か所に同時に貸されない。
   - Dialog を外から閉じる主な経路（Close・Dismiss・CloseAll・面ができる前）は `Choose` を通る（前回の #9）。
   - Slider の刻みの範囲外の数は 0〜100 に収まる。
9. **ローカライズ**: 次の点を確認した（F）。
   - fallback の輪（ja → en → ja）は `LocaleIndex.BuildChain` で止まる。
   - 引数の不足・`{` の閉じ忘れは例外にならない。
   - 書式の文化は `GetCultureInfo` で、CurrentCulture に引きずられない。
   - スクリプトの読み直しでは、全インスタンスの OnDestroy の後に `Events.ClearAll`・`L10n.ResetForReload`・`BindingFrame.ResetForReload` が走る。古いアセンブリのデリゲートは残らない。
10. **MCP・端末プリセット・プロジェクト設定**: 次の点を確認した（G2・G1）。
    - MCP:
      - 読み取り専用の分類は docs §7.2 と一致する（template_actor の add・preview・platform_sim は変更系）。
      - 端末の模擬の環境変数は `ProcessStartInfo.Environment` だけに入り、Edit 用のランタイムには渡らない。
    - プロジェクト設定:
      - project_settings.json の render / font の書き込みは、旗の 3 状態と知らない鍵・ほかの節を保つ。
      - release の pak に開発用の印が入るのは、明示的にチェックしたときだけ。
    - サムネイルの道具: `WorkFolderPolicy` は何かを書く前に、プロジェクト・ドライブの根・ホームを断る。

## 読めなかった範囲・注意
- 実行・ビルド・テストは一切していない。GUI の挙動（MessageBox の間の同期・ヒエラルキーの間引き）とタイミングに依る件（#3・#5・#11）は、コードの道筋からの推定。
- 読んでいない、または流し読みにとどまるもの:
  - プレハブの当て直しの `merge.rs` の欄ごとの合成（A は計画と適用の要所だけ）
  - Dialog.Input / Items / Layout、ListView・TabHost との組み合わせ（E）
  - EventBus 全体（F）
  - GpuMemReportFormatter・RuntimeManager.cs の全文・Android の実行の画面（G2）
  - ProjectSettingsPreviewProbe と各テストの本文（G1）
  - frame_renderer.rs のピックとギズモのプレビューの扱い（D）
- 写しの外で読んだのは、サブレビュア A の `D:\SEED_projects\WakeOrPay\assets\app\prefabs\shell.actor` と、G1 の Wake or Pay の .cs の grep だけ（どちらも読み取りのみ）。WarashibeFishing のスクリプトは作業ツリーにあるので見ていない。
- **範囲外で気づいた既存の穴**（今回のコミットの変更ではない。backlog の候補。確認: 未）:
  - ヒエラルキーで複数のアクタをドラッグして付け替えると、2 つ目以降の REPARENT が最初の木の DFS のまま送られる（HierarchyPanel.xaml.cs:2235-2273 と actor_ops.rs:863-934）。
  - AI の SCENE_INFO は常に世界線 0 の番号なのに、AI_* の命令は表示中の世界線へ当たる（ai_ops.rs:38-44）。
  - 壊れた project_settings.json（末尾のカンマなど）は既定値として読まれ、プロジェクト設定の画面で保存すると全体が既定値で上書きされる（ProjectSettingsData.cs:417-421。`.backup/` は残る）。
  - SAVE_ACTOR に応答が無いと `_isSavingActor` が残り、次のシーン保存の SAVE_OK で保存していないプレハブの PREFAB_REAPPLY_PATH が飛ぶ（MainWindow.Scene.cs:564-595）。
