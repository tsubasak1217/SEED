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
//   - .actor 保存／エクスポート時: **再展開は「行わない」**。
//     以前は propagate_prefab_change を自動で呼んでおり、プレハブ本体を保存した瞬間に
//     同じ参照パスを持つ全インスタンスが問答無用で再展開され、シーン側で加えた変更
//     （コンポーネント追加・値変更・子の追加）が消えていた。これが最後に残っていた
//     データ損失経路だったため自動呼び出しを撤去した。
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
    fn apply_prefab_links_on_load(&mut self) -> bool {
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

        // Undo 用に再展開前の world_line=0 ツリーをスナップショットする
        let before_actors = self.snapshot_actors_for_wl(0);

        // 1 件も再展開されなければ履歴も通知も出さない
        if !self.apply_prefab_links_on_load() { return; }

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

    /// .actor 保存／エクスポート時のライブ反映（**現在は自動では呼ばれない**）。
    ///
    /// 通常シーン（world_line=0）から prefab_source == `saved_abs_path`（仮想パス換算）を
    /// 持つ全インスタンスを探し、書き出した内容で再展開する。
    ///
    /// 【自動呼び出しを撤去した理由】
    /// 以前は .actor の保存／エクスポート成功時にここを自動で呼んでいた。しかしその結果、
    /// プレハブ本体を保存した瞬間に同じ参照パスを持つ全インスタンスが問答無用で再展開され、
    /// シーン側で加えた変更（コンポーネント追加・値変更・子の追加）が消えていた。
    /// 「シーンに保存された内容を正とする」方針（ロード時自動再展開の廃止）と矛盾し、
    /// 最後に残っていたデータ損失経路だったため自動呼び出しを撤去した。
    /// プレハブ本体の変更をシーンへ反映したいときは、ユーザーが明示的に
    /// 「プレハブから更新」（PREFAB_REAPPLY）または「シーン内の全プレハブを更新」
    /// （PREFAB_REAPPLY_ALL）を実行する。
    ///
    /// 「特定パスのインスタンスだけを一括更新する」明示コマンドを足す際に再利用できるよう
    /// 実装は残してある。
    #[allow(dead_code)]
    pub(super) fn propagate_prefab_change(&mut self, saved_abs_path: &str) {
        // 保存パス（絶対）を assets:// 仮想パスへ換算して prefab_source と比較する
        let vpath = crate::engine::asset_fs::to_virtual(saved_abs_path);

        // Undo 用に再展開前の world_line=0 ツリーをスナップショットする
        let before_actors = self.snapshot_actors_for_wl(0);

        let changed = self.run_prefab_reinstantiation(0, Some(&vpath));
        if !changed {
            return;
        }

        // 再展開後のツリーをスナップショットし、1 操作として Undo 履歴へ記録する。
        // （snapshot は to_data 経由で prefab_source を含むため、Undo/Redo で
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
        // 再展開で DFS が変動し得るが、C# ウェーブ以前の暫定対応として現行選択 DFS を再送する。
        if let Some(dfs) = self.actor_virtual_selected_idx {
            self.send_actor_components(dfs as u32, self.actor_virtual_selected_slot_idx);
        }
        if let Some(ipc) = &self.ipc {
            ipc.send("SCENE_MODIFIED");
        }
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

        // Undo 用に再展開前のツリーをスナップショットする
        let before_actors = self.snapshot_actors_for_wl(wl);
        let host = self.scripting_host.clone();

        // scene を取り出して draw_ctx（&self）との同時借用を回避する
        // （run_prefab_reinstantiation / rebuild_actors_for_wl と同じパターン）
        let mut scene = self.scene.take().unwrap();
        let mut changed = false;
        {
            let ctx = self.draw_ctx.as_ref().unwrap();
            let mut counter = 0u32;
            // scene.actors と scene.world は別フィールドなので同時可変借用できる
            if let Some(actor) =
                find_actor_by_dfs_mut(&mut scene.actors, wl, actor_dfs, &mut counter)
            {
                reapply_prefab_in_subtree(
                    actor, &mut scene.world, ctx, host.as_ref(), &mut changed,
                );
            }
        }
        self.scene = Some(scene);

        // 対象が見つからない／プレハブを含まない場合は履歴も通知も出さない
        if !changed { return; }

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
    /// 戻り値は「1 件以上再展開したか」。
    fn run_prefab_reinstantiation(&mut self, wl: u32, filter: Option<&str>) -> bool {
        if self.draw_ctx.is_none() || self.scene.is_none() {
            return false;
        }
        let host = self.scripting_host.clone();

        // scene を取り出して draw_ctx との同時借用を回避する
        let mut scene = self.scene.take().unwrap();
        let mut changed = false;
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
                &mut changed,
            );
        }
        self.scene = Some(scene);

        // 再展開で 2D/3D 構成が変化し得るため canvas_world_lines を同期する
        if changed {
            self.update_canvas_wl_state_for(wl);
        }
        changed
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
/// `changed` は 1 件でも再展開したら true にセットされる。
fn reapply_prefab_in_subtree(
    actor:   &mut Actor,
    world:   &mut World,
    ctx:     &DrawContext,
    host:    Option<&Arc<ScriptingHost>>,
    changed: &mut bool,
) {
    if let Some(src) = actor.prefab_source.clone() {
        reinstantiate_single(actor, world, ctx, host, &src, changed);
        return;
    }
    // 通常アクタ: 子側にネストされたプレハブインスタンスを探す
    // （is_top=false のため world_line フィルタは適用されない。filter=None で全プレハブ対象）
    // world_line は children_mut() の可変借用より先に取り出しておく
    let wl = actor.world_line;
    reinstantiate_prefabs_in_actors(
        actor.children_mut(), world, ctx, host,
        wl, false, None, changed,
    );
}

/// アクター配列を走査し、プレハブインスタンス（prefab_source 付き）を再展開する。
///
/// # 引数
/// - `is_top`: true のときトップレベル配列（world_line フィルタを適用する）。
///   子配列への再帰では false（子は親と同一 world_line のため無条件で処理する）。
/// - `filter`: None なら全プレハブ、Some(p) なら prefab_source==p のみを対象とする。
/// - `changed`: 1 件でも再展開したら true にセットする。
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
    changed: &mut bool,
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
            reinstantiate_single(&mut actors[i], world, ctx, host, &src, changed);
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
            changed,
        );
        i += 1;
    }
}

/// 1 つのプレハブインスタンス `slot` を、参照先ファイルの内容で再展開する（in place 置換）。
///
/// 維持: ルートの Transform/CanvasTransform・name・active・world_line・prefab_source。
/// 置換: 子ツリー・コンポーネント（ファイル内容で丸ごと差し替え）。
/// ファイル欠損・パース失敗・構築失敗・自己参照のいずれかの場合は再展開せず
/// 旧インスタンスを維持する（＝リンク維持）。
fn reinstantiate_single(
    slot: &mut Actor,
    world: &mut World,
    ctx: &DrawContext,
    host: Option<&Arc<ScriptingHost>>,
    src: &str,
    changed: &mut bool,
) {
    // ── 参照先ファイルを読み込む（assets:// 仮想パス／絶対パスのどちらも可）──
    let raw = match crate::engine::asset_fs::read_string(src) {
        Ok(r) => r,
        Err(e) => {
            eprintln!("[Prefab] 参照先ファイルを読み込めません（リンク維持）: {src} err={e}");
            return; // シーン保存値のまま
        }
    };
    let json = raw.strip_prefix('\u{FEFF}').unwrap_or(&raw);
    let data: ActorData = match serde_json::from_str(json) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("[Prefab] 参照先ファイルのパースに失敗（リンク維持）: {src} err={e}");
            return;
        }
    };

    // ── 自己参照ガード ──
    // ファイルのルートが自身と同じ参照パスを指す場合、展開すると無限に自己を含み得るため
    // スキップする（通常は書き出し時に prefab_source を除去するため発生しないが、手動編集対策）。
    if data.prefab_source.as_deref() == Some(src) {
        eprintln!("[Prefab] 自己参照を検出したため展開をスキップ: {src}");
        return;
    }

    // ── 維持する値を退避する（ルート Transform/CanvasTransform・name・active・world_line）──
    let keep_name = slot.name.clone();
    let keep_active = slot.active;
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
    new_actor.prefab_source = Some(src.to_string());
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
    *changed = true;
}
