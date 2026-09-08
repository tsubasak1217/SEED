// ============================================================
//  prefab_ops.rs — アクタファイル参照リンク（プレハブ）機構のコア
//
//  【設計】
//  Unity のプレハブに相当する「アクタファイル参照リンク」。
//  Actor::prefab_source（アセット相対 assets:// パス）を持つアクターは、
//  参照先 .actor / .actor2d ファイルのインスタンスとして扱う。
//
//  再展開（reinstantiate）のルール:
//   - 維持する値: ルートの Transform / CanvasTransform・name・active・world_line
//   - 置換する値: 子ツリー・コンポーネント（＝ファイル内容で丸ごと差し替え）
//   - 参照先ファイルが無い／読めない場合: シーン保存値のまま（リンク維持）
//
//  適用タイミング（**自動再展開は一切行わない。全てユーザーの明示操作**）:
//   - シーンロード時: 再展開は「行わない」。シーン(.scene)に保存された内容を正とする。
//     （旧実装は apply_prefab_links_on_load でロード毎に上書きしており、インスタンスへ
//       加えた変更が失われるデータ損失バグの原因だった。関数自体は再利用のため残す。）
//   - .actor 保存時: エディタ設定「プレハブ保存時にシーンのインスタンスへ自動反映」
//     （既定オン）がオンのときだけ、**エディタが明示的に** PREFAB_REAPPLY_PATH を送る。
//     ランタイム側から勝手に再展開することは無い（保存ハンドラは何もしない）。
//     かつては保存時に propagate_prefab_change を無条件で呼んでおり、プレハブ本体を保存した
//     瞬間に同じ参照パスを持つ全インスタンスが問答無用で再展開され、シーン側で加えた変更
//     （コンポーネント追加・値変更・子の追加）が黙って消えていた。今回の自動反映は
//     (1) 設定でオフにできる、(2) Undo で 1 操作として戻せる、(3) 反映件数をトーストで
//     必ず知らせる、の 3 点で「黙って消える」条件を外してある。
//   - 参照パス指定の一括更新（handle_reapply_prefab_path / IPC: PREFAB_REAPPLY_PATH）:
//     指定した 1 本の .actor を参照するインスタンスだけを再展開する（Undo 可能）。
//     上記の自動反映と MCP `seed_prefab_reapply` の実装本体。
//   - 版ずれの問い合わせ（handle_prefab_status / IPC: PREFAB_STATUS）: **読み取りのみ**。
//     インスタンスへ焼き込んだ prefab_hash（取り込んだ版）と .actor の現在の内容ハッシュを
//     比べ、更新が来ているインスタンス数をエディタへ返す。シーンを開いた直後にエディタが
//     投げ、stale が 1 件以上のときだけバナーを出す（ロード時の自動上書きはしない）。
//   - 個別更新（handle_reapply_prefab / IPC: PREFAB_REAPPLY）: 指定アクタ配下の
//     プレハブインスタンスだけをファイル最新内容で再展開する（Undo 可能）。
//   - 一括更新（handle_reapply_all_prefabs / IPC: PREFAB_REAPPLY_ALL）: シーン内の
//     全プレハブインスタンスをファイル最新内容で再展開する（Undo 可能）。
//     プレハブ本体を編集したあと、その内容をシーン全体へ反映したいときに使う。
//     いずれも破壊的操作のため、エディタ側で確認ダイアログを出してから送信する。
//
//  ネスト・自己参照（P1 の扱い）:
//   - ネストプレハブ（.actor 内のアクタがさらに prefab_source を持つ）は
//     P1 では 1 段のみ展開する（再帰展開はしない。TODO(P2)）。
//   - 自己参照（A.actor が A.actor を参照）は展開スキップ＋警告ログ。
// ============================================================

use std::sync::Arc;

use crate::engine::components::{CanvasTransform, Transform};
use crate::engine::core::app_base::scene::build_actor;
use crate::engine::core::app_base::undo::ActorTreeSnapshotCommand;
use crate::engine::core::scripting::ScriptingHost;
use crate::engine::ecs::World;
use crate::engine::methods::drawer::DrawContext;
use crate::engine::methods::gizmo_interact::{mat4x4_inv, mat4x4_mul};
use crate::engine::structs::objects::Actor;
use crate::engine::structs::objects::actor::ActorData;

use super::{App, apply_delta_to_actor_subtree, despawn_actor_recursive, find_actor_by_dfs_mut};

/// スケールが実質 0（逆行列が特異）とみなす閾値。
/// handle_set_actor_transform（transform_ops.rs）の特異判定と同じ値を使用する。
const SINGULAR_SCALE_EPS: f32 = 1e-7;

impl App {
    /// 通常シーン（world_line=0）の全プレハブインスタンスを一括再展開する（低レベル処理）。
    ///
    /// ロード時の自動呼び出しは廃止済み（データ損失バグのため）。
    /// 現在は明示コマンド `handle_reapply_all_prefabs` の実装本体として使う。
    /// Undo 記録・エディタ通知は呼び出し側の責務。
    fn apply_prefab_links_on_load(&mut self) -> usize {
        // filter=None: world_line=0 の全プレハブインスタンスを対象に再展開する
        self.run_prefab_reinstantiation(0, None)
    }

    /// シーン内の全プレハブインスタンスをプレハブ最新内容で一括再展開する
    /// （メニュー「シーン内の全プレハブを更新」用 / IPC: PREFAB_REAPPLY_ALL）。
    ///
    /// 対象は通常シーン（world_line=0）のみ。アクタ編集タブ（world_line>0）は
    /// ファイルそのものを編集する場所なので対象外。
    ///
    /// 【破壊的操作】シーン上でインスタンスへ加えた変更（コンポーネントの追加・値の変更・
    /// 子の追加など）はファイル内容で上書きされて失われる。エディタ側で確認ダイアログを
    /// 出したうえで送信すること。Undo で 1 操作として戻せる。
    ///
    /// 作法は `handle_reapply_prefab`（個別更新）と揃えてある
    /// （Undo 記録 → send_hierarchy → send_actor_components → SCENE_MODIFIED）。
    pub(super) fn handle_reapply_all_prefabs(&mut self) {
        // シーン未ロード・描画コンテキスト未初期化なら何もしない
        if self.scene.is_none() || self.draw_ctx.is_none() { return; }

        // プレハブ内容が変わっている前提の操作なので、散布用キャッシュも捨てる
        self.scatter_prefab_cache.clear();

        // Undo 用に再展開前の world_line=0 ツリーをスナップショットする
        let before_actors = self.snapshot_actors_for_wl(0);

        // 1 件も再展開されなければ履歴も通知も出さない
        if self.apply_prefab_links_on_load() == 0 { return; }

        // 再展開後のツリーをスナップショットし、1 操作として Undo 履歴へ記録する
        let after_actors = self.snapshot_actors_for_wl(0);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: 0,
            before_actors,
            after_actors,
        }));

        // エディタへ通知する（ヒエラルキー・選択中インスペクタ・シーン変更）
        self.send_hierarchy();
        if let Some(dfs) = self.actor_virtual_selected_idx {
            self.send_actor_components(dfs as u32, self.actor_virtual_selected_slot_idx);
        }
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// 指定した 1 本のプレハブファイルを参照する**全インスタンス**を再展開する
    /// （IPC: PREFAB_REAPPLY_PATH。エディタの「プレハブ保存時の自動反映」の実装本体）。
    ///
    /// 通常シーン（world_line=0）から `prefab_source == path`（`assets://` 仮想パス換算）を
    /// 持つインスタンスだけを対象にする。戻り値は再展開した件数。
    ///
    /// 【破壊的操作】シーン上でそのインスタンスへ加えた変更（コンポーネントの追加・値の
    /// 変更・子の追加など）はファイル内容で上書きされて失われる。Undo で 1 操作として
    /// 戻せる（＝エディタは「Ctrl+Z で戻せます」と案内したうえで自動実行してよい）。
    ///
    /// かつてはこれを `.actor` 保存時に**無条件で**呼んでおり、利用者が知らないうちに
    /// インスタンス側の編集が消えるデータ損失の原因になっていた。現在は
    /// 「エディタの設定でオンのときだけ・Undo 可能・件数を通知する」という条件付きで
    /// 呼び出す（既定オン）。設定をオフにすれば従来どおり手動更新のみになる。
    ///
    /// 作法は `handle_reapply_prefab`（個別更新）と揃えてある
    /// （Undo 記録 → send_hierarchy → send_actor_components → SCENE_MODIFIED）。
    pub(super) fn handle_reapply_prefab_path(&mut self, path: &str) -> usize {
        // 絶対パスでも `assets://` 仮想パスでも受け取れるよう、比較前に仮想パスへ揃える。
        // （to_virtual は既に仮想パスならそのまま返す）
        let vpath = crate::engine::asset_fs::to_virtual(path);

        // プレハブファイルが更新されたので、散布用キャッシュの該当エントリを捨てる
        self.scatter_prefab_cache.remove(&vpath);

        // Undo 用に再展開前の world_line=0 ツリーをスナップショットする
        let before_actors = self.snapshot_actors_for_wl(0);

        let count = self.run_prefab_reinstantiation(0, Some(&vpath));
        // 対象インスタンスが 1 件も無ければ履歴も通知も出さない（＝空の Undo を積まない）
        if count == 0 {
            return 0;
        }

        // 再展開後のツリーをスナップショットし、1 操作として Undo 履歴へ記録する。
        // （snapshot は to_data 経由で prefab_source / prefab_hash を含むため、Undo/Redo で
        //   rebuild_actors_for_wl が build_actor により正しく往復復元する）
        let after_actors = self.snapshot_actors_for_wl(0);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: 0,
            before_actors,
            after_actors,
        }));

        // エディタへ通知する（ヒエラルキー・選択中インスペクタ・シーン変更）
        self.send_hierarchy();
        // 選択中アクターがある場合はコンポーネント表示を最新内容へ更新する（best-effort）。
        // 再展開で DFS が変動し得るが、現行選択 DFS を再送する。
        if let Some(dfs) = self.actor_virtual_selected_idx {
            self.send_actor_components(dfs as u32, self.actor_virtual_selected_slot_idx);
        }
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
        count
    }

    /// 指定アクタ配下のプレハブインスタンスを、参照先 .actor の内容で再展開する
    /// （右クリックメニュー「プレハブから更新」用 / IPC: PREFAB_REAPPLY）。
    ///
    /// シーンロード時の自動再展開を廃止した代わりの、ユーザー明示操作の入口。
    /// 対象アクタ自身がプレハブインスタンスならそれを再展開し、そうでなければ
    /// 子孫にネストされたプレハブインスタンスを探して再展開する。
    ///
    /// 【破壊的操作】シーン上でインスタンスへ加えた変更（コンポーネントの追加・値の変更・
    /// 子の追加など）はファイル内容で上書きされて失われる。エディタ側で確認ダイアログを
    /// 出したうえで送信すること。Undo で 1 操作として戻せる。
    pub(super) fn handle_reapply_prefab(&mut self, actor_dfs: u32) {
        let wl = self.active_world_line;
        // シーン未ロード・描画コンテキスト未初期化なら何もしない
        if self.scene.is_none() || self.draw_ctx.is_none() { return; }

        // プレハブ内容が変わっている前提の操作なので、散布用キャッシュも捨てる
        self.scatter_prefab_cache.clear();

        // Undo 用に再展開前のツリーをスナップショットする
        let before_actors = self.snapshot_actors_for_wl(wl);
        let host = self.scripting_host.clone();

        // scene を取り出して draw_ctx（&self）との同時借用を回避する
        // （run_prefab_reinstantiation / rebuild_actors_for_wl と同じパターン）
        let mut scene = self.scene.take().unwrap();
        let mut count = 0usize;
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            let mut counter = 0u32;
            // scene.actors と scene.world は別フィールドなので同時可変借用できる
            if let Some(actor) =
                find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs, &mut counter)
            {
                reapply_prefab_in_subtree(
                    actor, &mut scene.world, ctx, host.as_ref(), &mut count,
                );
            }
        }
        self.scene = Some(scene);

        // 対象が見つからない／プレハブを含まない場合は履歴も通知も出さない
        if count == 0 { return; }

        // 再展開で 2D/3D 構成が変化し得るため canvas_world_lines を同期する
        self.update_canvas_wl_state_for(wl);

        // 再展開後のツリーをスナップショットし、1 操作として Undo 履歴へ記録する
        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        // エディタへ通知する（ヒエラルキー・選択中インスペクタ・シーン変更）
        self.send_hierarchy();
        if let Some(dfs) = self.actor_virtual_selected_idx {
            self.send_actor_components(dfs as u32, self.actor_virtual_selected_slot_idx);
        }
        if let Some(ipc) = &self.ipc { ipc.send("SCENE_MODIFIED"); }
    }

    /// シーン内のプレハブインスタンスの「版ずれ」を調べてエディタへ返す
    /// （IPC: PREFAB_STATUS → 応答 `PREFAB_STATUS:{json}`）。
    ///
    /// シーンロード時に**自動再展開しない**方針は維持したまま、「プレハブ本体が
    /// シーンへ取り込んだ版より新しい」ことだけをエディタへ知らせるための問い合わせ。
    /// エディタはこの結果でバナーを出し、利用者が押したときだけ再展開する。
    ///
    /// 判定はインスタンスに焼き込んだ `prefab_hash`（取り込んだ版）と、参照先ファイルの
    /// 現在の内容ハッシュの比較で行う。`prefab_hash` を持たない旧シーンのインスタンスは
    /// 版が不明なので `unknown` として数え、**stale には含めない**（＝勝手に促さない）。
    pub(super) fn handle_prefab_status(&mut self) {
        let json = self.collect_prefab_status_json();
        if let Some(ipc) = &self.ipc {
            ipc.send(&format!("PREFAB_STATUS:{json}"));
        }
    }

    /// `PREFAB_STATUS` の応答 JSON を組み立てる（送信は行わない。テストから直接呼べる）。
    ///
    /// 戻り値は `[{"source":..,"total":N,"stale":N,"unknown":N,"missing":bool}, ..]` の配列。
    /// 並びは `source` の昇順（エディタの表示順を安定させるため）。
    pub(super) fn collect_prefab_status_json(&self) -> String {
        // 参照パスごとの集計。ファイル読み込みは 1 パスにつき 1 回で済ませる。
        let mut per_source: std::collections::BTreeMap<String, PrefabStatusEntry> =
            std::collections::BTreeMap::new();
        let Some(scene) = &self.scene else { return "[]".to_string() };

        // 通常シーン（world_line=0）のみを対象にする。
        // アクタ編集タブ（world_line>0）はファイルそのものを編集する場所なので対象外。
        for actor in scene.actors.iter().filter(|a| a.world_line == 0) {
            collect_prefab_status_in_actor(actor, &mut per_source);
        }

        // 参照先ファイルの現在のハッシュを 1 パスにつき 1 回だけ読み、stale を確定させる。
        for (source, entry) in per_source.iter_mut() {
            match prefab_content_hash(source) {
                Some(current) => {
                    entry.stale = entry.hashes.iter().filter(|h| **h != current).count();
                }
                None => {
                    // 参照先が読めない（消された・移動した）。stale ではなく missing として報告する。
                    entry.missing = true;
                }
            }
        }

        let items: Vec<_> = per_source
            .iter()
            .map(|(source, e)| PrefabStatusJson {
                source,
                total:   e.total,
                stale:   e.stale,
                unknown: e.unknown,
                missing: e.missing,
            })
            .collect();
        serde_json::to_string(&items).unwrap_or_else(|_| "[]".to_string())
    }

    /// プレハブ参照リンクを解除する（右クリックメニュー「リンク解除」用）。
    ///
    /// active_world_line 上の DFS `actor_dfs` のアクターの prefab_source を None にする。
    /// 以後このアクターは再展開・ライブ反映の対象外となり、独立ツリーとして保存・維持される。
    /// Undo 可能・エディタ通知あり。
    pub(super) fn handle_unlink_prefab(&mut self, actor_dfs: u32) {
        let wl = self.active_world_line;
        if self.scene.is_none() {
            return;
        }

        let before_actors = self.snapshot_actors_for_wl(wl);
        let mut did = false;
        {
            let scene = self.scene.as_mut().unwrap();
            let mut c = 0u32;
            if let Some(actor) = find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs, &mut c) {
                if actor.prefab_source.is_some() {
                    actor.prefab_source = None;
                    did = true;
                }
            }
        }
        // 対象がプレハブインスタンスでなければ何もしない
        if !did {
            return;
        }

        let after_actors = self.snapshot_actors_for_wl(wl);
        self.undo_history.record(Box::new(ActorTreeSnapshotCommand {
            world_line: wl,
            before_actors,
            after_actors,
        }));

        self.send_hierarchy();
        if let Some(dfs) = self.actor_virtual_selected_idx {
            self.send_actor_components(dfs as u32, self.actor_virtual_selected_slot_idx);
        }
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
    }

    /// world_line=`wl` のプレハブインスタンスを再展開する共通処理。
    ///
    /// scene を一時的に take して、draw_ctx（&）と scene.world（&mut）の同時借用問題を
    /// 回避する（rebuild_actors_for_wl と同じパターン）。
    /// `filter` が None なら全プレハブ、Some(p) なら prefab_source==p のみを対象とする。
    /// 戻り値は「再展開したインスタンスの件数」。
    fn run_prefab_reinstantiation(&mut self, wl: u32, filter: Option<&str>) -> usize {
        if self.draw_ctx.is_none() || self.scene.is_none() {
            return 0;
        }
        let host = self.scripting_host.clone();

        // scene を取り出して draw_ctx との同時借用を回避する
        let mut scene = self.scene.take().unwrap();
        let mut count = 0usize;
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            reinstantiate_prefabs_in_actors(
                &mut scene.actors,
                &mut scene.world,
                ctx,
                host.as_ref(),
                wl,
                true,
                filter,
                &mut count,
            );
        }
        self.scene = Some(scene);

        // 再展開で 2D/3D 構成が変化し得るため canvas_world_lines を同期する
        if count > 0 {
            self.update_canvas_wl_state_for(wl);
        }
        count
    }
}

// ============================================================
//  再展開の実装（フリー関数）
// ============================================================

/// 1 つのアクタ（とその配下）に限定してプレハブ再展開を行う。
///
/// - `actor` 自身が prefab_source を持つ ＝ プレハブインスタンスのルートなら、それを再展開する
///   （ネストプレハブは P1 と同じく 1 段のみ。子へは再帰しない）。
/// - そうでなければ、子ツリーを再帰的に探索してネストされたプレハブインスタンスを再展開する。
///
/// `count` には再展開したインスタンスの件数を加算する。
fn reapply_prefab_in_subtree(
    actor:   &mut Actor,
    world:   &mut World,
    ctx:     &DrawContext,
    host:    Option<&Arc<ScriptingHost>>,
    count: &mut usize,
) {
    if let Some(src) = actor.prefab_source.clone() {
        reinstantiate_single(actor, world, ctx, host, &src, count);
        return;
    }
    // 通常アクタ: 子側にネストされたプレハブインスタンスを探す
    // （is_top=false のため world_line フィルタは適用されない。filter=None で全プレハブ対象）
    // world_line は children_mut() の可変借用より先に取り出しておく
    let wl = actor.world_line;
    reinstantiate_prefabs_in_actors(
        actor.children_mut(), world, ctx, host,
        wl, false, None, count,
    );
}

/// アクター配列を走査し、プレハブインスタンス（prefab_source 付き）を再展開する。
///
/// # 引数
/// - `is_top`: true のときトップレベル配列（world_line フィルタを適用する）。
///   子配列への再帰では false（子は親と同一 world_line のため無条件で処理する）。
/// - `filter`: None なら全プレハブ、Some(p) なら prefab_source==p のみを対象とする。
/// - `count`: 再展開したインスタンス 1 件につき 1 を加算する。
///
/// # ネスト・無限ループ防止
/// プレハブノードを再展開したら、その子（ファイル由来）へは再帰しない（P1 は 1 段のみ）。
/// プレハブでない通常アクターの配下にネストされたプレハブインスタンスは、子へ再帰して探す。
#[allow(clippy::too_many_arguments)]
fn reinstantiate_prefabs_in_actors(
    actors: &mut Vec<Actor>,
    world: &mut World,
    ctx: &DrawContext,
    host: Option<&Arc<ScriptingHost>>,
    wl: u32,
    is_top: bool,
    filter: Option<&str>,
    count: &mut usize,
) {
    let mut i = 0;
    while i < actors.len() {
        // トップレベルは対象 world_line のみを処理する（子は同一 wl なので無条件）
        if is_top && actors[i].world_line != wl {
            i += 1;
            continue;
        }

        // このノードがプレハブインスタンスのルートで、かつ filter に合致するか判定する
        let src_opt = actors[i].prefab_source.clone();
        let matched = match (&src_opt, filter) {
            (Some(src), Some(f)) => src == f,
            (Some(_), None) => true,
            _ => false,
        };

        if matched {
            // src_opt は matched=true の時点で必ず Some
            let src = src_opt.unwrap();
            reinstantiate_single(&mut actors[i], world, ctx, host, &src, count);
            // 再展開した子ツリーはファイル由来。ネストプレハブは P1 では展開しないため
            // 子へは再帰しない（無限ループ防止も兼ねる）。
            // TODO(P2): ネストプレハブの再帰展開に対応する。
            i += 1;
            continue;
        }

        // プレハブでない通常アクター: 子ツリーを再帰的に探索する
        // （通常アクター配下にネストされたプレハブインスタンスに対応するため）。
        reinstantiate_prefabs_in_actors(
            actors[i].children_mut(),
            world,
            ctx,
            host,
            wl,
            false,
            filter,
            count,
        );
        i += 1;
    }
}

/// プレハブ（`.actor` / `.actor2d`）ファイルを読み込み、`ActorData` と**その内容のハッシュ**を同時に返す。
///
/// ハッシュはインスタンス側 `Actor::prefab_hash` へ焼き込み、次回シーンを開いたときに
/// 「取り込んだ版」と「ファイルの現在の版」を比べて更新検出（`PREFAB_STATUS`）に使う。
/// 読み込みとハッシュ算出を 1 回のファイル読みで済ませるため、この関数に集約する。
pub(super) fn load_actor_data_with_hash(src: &str) -> Result<(ActorData, String), String> {
    let raw = crate::engine::asset_fs::read_string(src)
        .map_err(|e| format!("読み込み失敗: {e}"))?;
    let data: ActorData = serde_json::from_str(&raw).map_err(|e| format!("パース失敗: {e}"))?;
    Ok((data, content_hash(&raw)))
}

/// プレハブ（`.actor` / `.actor2d`）ファイルの現在の内容ハッシュを求める。
///
/// 読めない場合は `None`（＝版を比較できないので stale 判定しない）。
/// `asset_fs::read_string` を通すため BOM の有無は結果に影響しない。
pub(super) fn prefab_content_hash(src: &str) -> Option<String> {
    crate::engine::asset_fs::read_string(src).ok().map(|raw| content_hash(&raw))
}

// ── 内容ハッシュ（FNV-1a 64bit）─────────────────────────────────────────────
//  暗号学的強度は不要（衝突しても「更新に気付かない」だけで破壊は起きない）。外部
//  クレートを増やさず、C# エディタ側でも数行で同じ値を再現できることを優先して
//  FNV-1a を採用する。定数は FNV の規格値。
/// FNV-1a 64bit のオフセット基底（規格値）。
const FNV_OFFSET_BASIS_64: u64 = 0xcbf2_9ce4_8422_2325;
/// FNV-1a 64bit の素数（規格値）。
const FNV_PRIME_64: u64 = 0x0000_0100_0000_01b3;

/// 文字列の内容ハッシュを 16 桁の 16 進数文字列で返す（FNV-1a 64bit）。
fn content_hash(text: &str) -> String {
    let mut hash = FNV_OFFSET_BASIS_64;
    for byte in text.as_bytes() {
        hash ^= *byte as u64;
        hash = hash.wrapping_mul(FNV_PRIME_64);
    }
    format!("{hash:016x}")
}

/// 1 つのプレハブインスタンス `slot` を、参照先ファイルの内容で再展開する（in place 置換）。
///
/// 維持: ルートの Transform/CanvasTransform・name・active・visible・world_line・prefab_source。
/// 置換: 子ツリー・コンポーネント（ファイル内容で丸ごと差し替え）と `prefab_hash`（取り込んだ版）。
/// ファイル欠損・パース失敗・構築失敗・自己参照のいずれかの場合は再展開せず
/// 旧インスタンスを維持する（＝リンク維持）。
fn reinstantiate_single(
    slot: &mut Actor,
    world: &mut World,
    ctx: &DrawContext,
    host: Option<&Arc<ScriptingHost>>,
    src: &str,
    count: &mut usize,
) {
    // ── 参照先ファイルを読み込む（assets:// 仮想パス／絶対パスのどちらも可）──
    // 内容ハッシュも同時に受け取り、再展開したインスタンスへ「取り込んだ版」として焼き込む。
    let (data, hash): (ActorData, String) = match load_actor_data_with_hash(src) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("[Prefab] 参照先ファイルを読めません（リンク維持）: {src} {e}");
            return; // シーン保存値のまま
        }
    };

    // ── 自己参照ガード ──
    // ファイルのルートが自身と同じ参照パスを指す場合、展開すると無限に自己を含み得るため
    // スキップする（通常は書き出し時に prefab_source を除去するため発生しないが、手動編集対策）。
    if data.prefab_source.as_deref() == Some(src) {
        eprintln!("[Prefab] 自己参照を検出したため展開をスキップ: {src}");
        return;
    }

    // ── 維持する値を退避する（ルート Transform/CanvasTransform・name・active・visible・world_line）──
    let keep_name = slot.name.clone();
    let keep_active = slot.active;
    // 表示フラグもインスタンス側の値を維持する（再展開でプレハブ既定へ戻さない）。
    let keep_visible = slot.visible;
    let keep_wl = slot.world_line;
    let keep_tf = world.get::<Transform>(slot.entity).cloned();
    let keep_ct = world.get::<CanvasTransform>(slot.entity).cloned();

    // ── ファイルからサブツリーを新規構築する（新規 entity を spawn）──
    // 先に構築し、成功してから旧インスタンスを despawn することで、構築失敗時に
    // 旧インスタンスを失わないようにする（root_entity=None で新規 entity を使うため衝突しない）。
    let mut new_actor = match build_actor(data, ctx, world, host, None) {
        Ok(a) => a,
        Err(e) => {
            eprintln!("[Prefab] 再展開の構築に失敗（リンク維持）: {src} err={e}");
            return;
        }
    };

    // ── 旧インスタンスのエンティティ群を despawn する（構築成功後に実施）──
    despawn_actor_recursive(slot, world);

    // ── 退避値を書き戻す ──
    new_actor.name = keep_name;
    new_actor.active = keep_active;
    new_actor.visible = keep_visible;
    new_actor.prefab_source = Some(src.to_string());
    // 取り込んだプレハブの版を記録する（次回ロード時の更新検出に使う）。
    new_actor.prefab_hash = Some(hash);
    new_actor.set_world_line_recursive(keep_wl);
    // ルート Transform を維持する。新アクターの種別に合わせて適切な型を挿入する
    // （種別がファイル側で変わっているケースでも安全に処理できるよう両方を退避してある）。
    if new_actor.is_2d() {
        // 2D: スプライト描画は毎フレーム CanvasTransform 階層から再計算されるため、
        // ルート CanvasTransform の書き戻しのみで整合する（行列の再基準化は不要）。
        if let Some(ct) = keep_ct {
            world.insert(new_actor.entity, ct);
        }
    } else if let Some(keep) = keep_tf {
        // 3D: ファイル内容は「ルート位置＝原点」基準で構築されており、instance_mats・
        // 子 Transform はワールド空間で保持される（毎フレームの親子再計算は無い）。
        // シーン側のルート Transform（keep）を書き戻すだけではメッシュ位置が追従しない
        // ため、ファイル側ルート Transform との差分 delta = M_keep * M_file^{-1} を
        // サブツリー全体（MC 行列・子 Transform）へ適用して見た目を整合させる。
        let file_tf = world
            .get::<Transform>(new_actor.entity)
            .cloned()
            .unwrap_or_default();
        world.insert(new_actor.entity, keep.clone());
        // keep == file_tf（通常はドロップ直後など未移動のケース）は delta が単位行列の
        // ため適用しない（浮動小数点誤差の累積蓄積を防ぐ）。
        if keep != file_tf {
            // ファイル側スケールが 0 の場合は逆行列が特異になるため delta 適用をスキップする
            // （handle_set_actor_transform と同じガード）。
            let singular = file_tf.scale.iter().any(|&s| s.abs() < SINGULAR_SCALE_EPS);
            if singular {
                eprintln!("[Prefab] ファイル側ルートスケールが 0 のため行列補正をスキップ: {src}");
            } else {
                let delta = mat4x4_mul(keep.to_mat4(), mat4x4_inv(file_tf.to_mat4()));
                apply_delta_to_actor_subtree(&mut new_actor, world, delta);
            }
        }
    }

    // ── ツリー内のインスタンスを置き換える ──
    *slot = new_actor;
    *count += 1;
}


// ============================================================
//  版ずれ検出（PREFAB_STATUS）の集計
// ============================================================

/// 参照パス 1 本ぶんの集計途中データ。
#[derive(Default)]
struct PrefabStatusEntry {
    /// この参照パスを持つインスタンスの総数。
    total:   usize,
    /// 取り込み済みハッシュがファイルの現在値と食い違う件数（＝更新が来ている）。
    stale:   usize,
    /// `prefab_hash` を持たない（版が不明な）インスタンスの件数。旧シーン由来。
    unknown: usize,
    /// 参照先ファイルが読めない（消された・移動した）。
    missing: bool,
    /// 各インスタンスが取り込んだ版のハッシュ。ファイル側ハッシュ確定後に stale を数えるため保持する。
    hashes:  Vec<String>,
}

/// `PREFAB_STATUS` 応答 JSON の 1 要素。
#[derive(serde::Serialize)]
struct PrefabStatusJson<'a> {
    /// プレハブ参照パス（`assets://` 仮想パス）。
    source:  &'a str,
    /// この参照パスを持つインスタンスの総数。
    total:   usize,
    /// 版が食い違っているインスタンス数（エディタのバナーはこれが 1 以上のときだけ出す）。
    stale:   usize,
    /// 版が不明なインスタンス数（旧シーン由来。バナーの対象外）。
    unknown: usize,
    /// 参照先ファイルが読めないか。
    missing: bool,
}

/// アクタとその子孫を辿ってプレハブインスタンスを集計する。
///
/// プレハブインスタンスのルートを見つけたら、その配下へは降りない
/// （ネストプレハブは 1 段のみ展開する現行仕様と揃える）。
fn collect_prefab_status_in_actor(
    actor:      &Actor,
    per_source: &mut std::collections::BTreeMap<String, PrefabStatusEntry>,
) {
    if let Some(src) = &actor.prefab_source {
        let entry = per_source.entry(src.clone()).or_default();
        entry.total += 1;
        match &actor.prefab_hash {
            Some(h) => entry.hashes.push(h.clone()),
            None    => entry.unknown += 1,
        }
        return;
    }
    for child in actor.children() {
        collect_prefab_status_in_actor(child, per_source);
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::ecs::World;

    /// 内容ハッシュが「同じ内容なら同じ・違えば違う・常に 16 桁の 16 進数」であることを確認する。
    ///
    /// この 3 性質が崩れると版ずれ検出（PREFAB_STATUS）が誤検出／取りこぼしになる。
    #[test]
    fn content_hash_is_deterministic_and_fixed_width() {
        let a = content_hash(r#"{"name":"ZukanCard"}"#);
        let b = content_hash(r#"{"name":"ZukanCard"}"#);
        let c = content_hash(r#"{"name":"ZukanCard "}"#); // 末尾に空白 1 個だけ違う

        assert_eq!(a, b, "同じ内容は同じハッシュになること");
        assert_ne!(a, c, "1 文字違えば別のハッシュになること");
        assert_eq!(a.len(), 16, "常に 16 桁（64bit の 16 進数）であること");
        assert!(a.chars().all(|ch| ch.is_ascii_hexdigit()), "16 進数の文字だけであること");
        // 空文字は FNV-1a のオフセット基底そのもの（規格どおりであることの固定値検証）。
        assert_eq!(content_hash(""), format!("{FNV_OFFSET_BASIS_64:016x}"));
    }

    /// 版ずれ集計が「stale / unknown / total」を参照パスごとに正しく数えることを確認する。
    ///
    /// - 取り込み済みハッシュがファイルの現在値と違う → stale
    /// - `prefab_hash` を持たない旧シーン由来 → unknown（stale には数えない）
    /// - プレハブでないアクタの配下も辿る（ネストしたインスタンスを取りこぼさない）
    #[test]
    fn prefab_status_counts_stale_and_unknown_per_source() {
        let mut world = World::new();
        // ルートは通常アクタ。その配下にプレハブインスタンスを 3 つぶら下げる。
        let mut root = Actor::new(world.spawn(), "Root");

        // 取り込み済み版が "aaaa"（＝現在のファイル版と食い違う想定）
        let mut stale_inst = Actor::new(world.spawn(), "Card0");
        stale_inst.prefab_source = Some("assets://zukan/actors/ZukanCard.actor".into());
        stale_inst.prefab_hash   = Some("aaaa".into());

        // 取り込み済み版が "bbbb"（＝現在のファイル版と一致する想定）
        let mut fresh_inst = Actor::new(world.spawn(), "Card1");
        fresh_inst.prefab_source = Some("assets://zukan/actors/ZukanCard.actor".into());
        fresh_inst.prefab_hash   = Some("bbbb".into());

        // 版が不明な旧シーン由来のインスタンス
        let mut legacy_inst = Actor::new(world.spawn(), "Card2");
        legacy_inst.prefab_source = Some("assets://zukan/actors/ZukanCard.actor".into());

        root.children.push(stale_inst);
        root.children.push(fresh_inst);
        root.children.push(legacy_inst);

        let mut per_source = std::collections::BTreeMap::new();
        collect_prefab_status_in_actor(&root, &mut per_source);

        let key = "assets://zukan/actors/ZukanCard.actor";
        let entry = per_source.get(key).expect("参照パスごとに 1 エントリできること");
        assert_eq!(entry.total, 3, "インスタンス総数");
        assert_eq!(entry.unknown, 1, "版が不明なインスタンス数");
        assert_eq!(entry.hashes.len(), 2, "版が判っているインスタンスのハッシュを保持すること");

        // ファイルの現在版を "bbbb" とみなして stale を確定させる（実ファイル読みの代わり）。
        let current = "bbbb";
        let stale = entry.hashes.iter().filter(|h| h.as_str() != current).count();
        assert_eq!(stale, 1, "食い違う 1 件だけが stale になること");
    }

    /// プレハブでないアクタは集計に入らない（フォルダや素のアクタを数えない）ことを確認する。
    #[test]
    fn prefab_status_ignores_non_prefab_actors() {
        let mut world = World::new();
        let mut root = Actor::new(world.spawn(), "Root");
        root.children.push(Actor::new(world.spawn(), "Plain"));

        let mut per_source = std::collections::BTreeMap::new();
        collect_prefab_status_in_actor(&root, &mut per_source);
        assert!(per_source.is_empty(), "プレハブが 1 つも無ければ空になること");
    }

    /// プレハブインスタンスの配下には降りない（ネストプレハブ 1 段のみの現行仕様と揃える）。
    #[test]
    fn prefab_status_does_not_descend_into_instances() {
        let mut world = World::new();
        let mut outer = Actor::new(world.spawn(), "Outer");
        outer.prefab_source = Some("assets://a.actor".into());

        let mut inner = Actor::new(world.spawn(), "Inner");
        inner.prefab_source = Some("assets://b.actor".into());
        outer.children.push(inner);

        let mut per_source = std::collections::BTreeMap::new();
        collect_prefab_status_in_actor(&outer, &mut per_source);

        assert_eq!(per_source.len(), 1, "外側のインスタンスだけを数えること");
        assert!(per_source.contains_key("assets://a.actor"));
    }
}
