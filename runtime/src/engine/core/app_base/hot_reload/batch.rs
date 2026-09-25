// ============================================================
//  hot_reload/batch.rs — 1 フレームに届いた差し替えの要求をまとめ、「何をどの順で行い、誰に何を返すか」を決める（純粋な処理）
//
//  【状態の流れ】
//    受付（HotReloadBatch::push。IPC の処理の途中で届いた順に貯める）
//      → 計画（take_plan。フレームの境界＝IPC の処理の最後に 1 回。貯めた要求を取り出して空に戻す）
//      → 適用（App が計画どおりに: アセットのキャッシュを捨てる → 要ればシーンを 1 回だけ読み直す。app/hot_reload_ops.rs）
//      → 応答（HotReloadPlan::reply_lines。読み直しの成否を見て、要求ごとに RELOAD_DONE / SKIPPED / FAILED を 1 行）
//  同じフレームに何件届いても、シーンの読み直しは 1 回（モデルを 3 つ差し替えてもシーンは 1 回だけ組み直す）。
//  同じ宛先の要求が重なったら応答は 1 行（エディタは宛先ごとに 1 つの応答を待つ）。
//
//  【シーンを読み直せないとき】
//    - 読み込み中のシーンが無い（起動直後の失敗など）… シーンの要求は RELOAD_SKIPPED、アセットはキャッシュを捨てるだけ
//    - Edit モード（PC のエディタの埋め込みランタイム）… 読み直さない（エディタは自分の LOAD_SCENE を使う）。RELOAD_SKIPPED
//  ゲームの一時停止（PAUSE）は読み直しの前後で変えない（呼び出し側がそのまま保つ）。
// ============================================================

use std::path::Path;

use super::asset_key::key_refers_to;
use super::asset_kind::{classify, AssetRefresh};
use super::wire::HotReloadRequest;

/// シーンを読み直したときの詳細の接頭辞（`scene:<読み直したシーン>`）。
const DETAIL_SCENE_PREFIX: &str = "scene:";

/// キャッシュを捨てるだけで済んだときの詳細。
const DETAIL_IN_PLACE: &str = "inplace";

/// 1 フレームに届いた差し替えの要求（受付の順に貯める）。
#[derive(Debug, Default)]
pub struct HotReloadBatch {
    /// 届いた要求（届いた順）。
    requests: Vec<HotReloadRequest>,
}

/// シーンの読み直しの要求（計画の中の 1 件）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SceneReloadRequest {
    /// 無条件（RELOAD_SCENE）。
    Current,
    /// 今のシーンが指定のときだけ（RELOAD_SCENE:<相対パス>）。
    IfCurrent(String),
}

/// 計画の中のアセット 1 件（キャッシュを捨てる対象）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlannedAsset {
    /// アセットルートからの相対パス。
    pub relative: String,
    /// 反映のしかた（asset_kind.rs の表）。
    pub refresh: AssetRefresh,
}

/// 要求 1 件への応答の決め方。
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ReplyRule {
    /// 適用の後に RELOAD_DONE を返す。`needs_rebuild` なら、シーンの読み直しが失敗したときは RELOAD_FAILED にする。
    AfterApply { needs_rebuild: bool },
    /// 適用しない（RELOAD_SKIPPED。理由付き）。
    Skipped { reason: String },
    /// 受け付けない（RELOAD_FAILED。理由付き）。
    Failed { reason: String },
}

/// 要求 1 件への応答（宛先と決め方）。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PlannedReply {
    /// 宛先（wire.rs。scene / scene:<相対パス> / asset:<相対パス>）。
    pub target: String,
    /// 決め方。
    pub rule: ReplyRule,
}

/// フレームの境界で行うこと（計画）。
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct HotReloadPlan {
    /// キャッシュを捨てるアセット（重複を除いた、届いた順）。
    pub assets: Vec<PlannedAsset>,
    /// 今のシーンを読み直すか（何件の要求があっても 1 回）。
    pub rebuild_scene: bool,
    /// 要求ごとの応答（宛先の重複を除いた、届いた順）。
    pub replies: Vec<PlannedReply>,
}

/// 計画を立てるのに要る「今の状態」。
#[derive(Debug, Clone, Copy)]
pub struct PlanContext<'a> {
    /// 読み込み中のシーン（仮想パスか絶対パス。無ければ None）。
    pub current_scene: Option<&'a str>,
    /// アセットルート（絶対パスのシーンのキーを相対パスへ直すのに使う）。
    pub assets_root: Option<&'a Path>,
    /// シーンを読み直せるモードか（Play なら true。Edit はエディタが自分で LOAD_SCENE する）。
    pub scene_rebuild_allowed: bool,
}

impl HotReloadBatch {
    /// 要求を 1 件貯める（適用はフレームの境界の take_plan の後）。
    pub fn push(&mut self, request: HotReloadRequest) {
        self.requests.push(request);
    }

    /// 貯めた要求が無いか。
    pub fn is_empty(&self) -> bool {
        self.requests.is_empty()
    }

    /// 貯めた要求を取り出して計画にする（貯めた要求は空に戻る）。
    ///
    /// # 引数
    /// * `context` - 今の状態（読み込み中のシーン・アセットルート・モード）
    pub fn take_plan(&mut self, context: PlanContext<'_>) -> HotReloadPlan {
        let requests = std::mem::take(&mut self.requests);
        plan(&requests, context)
    }
}

/// 要求の並びから計画を立てる【純関数】。
///
/// # 引数
/// * `requests` - 届いた要求（届いた順）
/// * `context`  - 今の状態
pub fn plan(requests: &[HotReloadRequest], context: PlanContext<'_>) -> HotReloadPlan {
    let mut plan = HotReloadPlan::default();
    for request in requests {
        let target = request.target();
        // 同じ宛先の要求が重なったら、最初の 1 件だけに応答する（エディタは宛先ごとに 1 つの応答を待つ）
        if plan.replies.iter().any(|reply| reply.target == target) {
            continue;
        }
        let rule = match request {
            HotReloadRequest::Invalid { reason, .. } => ReplyRule::Failed { reason: reason.clone() },
            HotReloadRequest::Scene { only_if } => {
                let request = match only_if {
                    None => SceneReloadRequest::Current,
                    Some(relative) => SceneReloadRequest::IfCurrent(relative.clone()),
                };
                plan_scene(&mut plan, &request, context)
            }
            HotReloadRequest::Asset { relative } => plan_asset(&mut plan, relative, context),
        };
        plan.replies.push(PlannedReply { target, rule });
    }
    plan
}

/// シーンの読み直しの要求 1 件を計画へ入れ、応答の決め方を返す。
fn plan_scene(plan: &mut HotReloadPlan, request: &SceneReloadRequest, context: PlanContext<'_>) -> ReplyRule {
    let Some(current) = context.current_scene else {
        return ReplyRule::Skipped { reason: "読み込み中のシーンがありません".to_string() };
    };
    if let SceneReloadRequest::IfCurrent(relative) = request {
        if !key_refers_to(current, relative, context.assets_root) {
            return ReplyRule::Skipped {
                reason: format!("今のシーンは {current} のため読み直しません（{relative} へ遷移したときに反映されます）"),
            };
        }
    }
    if !context.scene_rebuild_allowed {
        return ReplyRule::Skipped { reason: "Edit モードではシーンを読み直しません（エディタの読み込みを使ってください）".to_string() };
    }
    plan.rebuild_scene = true;
    ReplyRule::AfterApply { needs_rebuild: true }
}

/// アセットの差し替えの要求 1 件を計画へ入れ、応答の決め方を返す。
fn plan_asset(plan: &mut HotReloadPlan, relative: &str, context: PlanContext<'_>) -> ReplyRule {
    let refresh = classify(relative);
    match refresh {
        AssetRefresh::Scripts => {
            return ReplyRule::Skipped { reason: "スクリプトは RELOAD_SCRIPTS で差し替えます".to_string() };
        }
        AssetRefresh::RestartRequired => {
            return ReplyRule::Skipped {
                reason: "起動時に 1 回だけ読むアセットのため差し替えられません（アプリを起動し直してください）".to_string(),
            };
        }
        AssetRefresh::SceneFile => {
            // シーンファイルは、今のシーンのときだけ読み直す（それ以外は送っただけ）
            return plan_scene(plan, &SceneReloadRequest::IfCurrent(relative.to_string()), context);
        }
        AssetRefresh::InPlace | AssetRefresh::RebuildScene => {}
    }
    if !plan.assets.iter().any(|asset| asset.relative.eq_ignore_ascii_case(relative)) {
        plan.assets.push(PlannedAsset { relative: relative.to_string(), refresh });
    }
    // シーンに取り込むアセットは、読めるならシーンを読み直す（読み込み中のシーンが無い・Edit ならキャッシュを捨てるだけ）
    let rebuild = refresh == AssetRefresh::RebuildScene && context.current_scene.is_some() && context.scene_rebuild_allowed;
    if rebuild {
        plan.rebuild_scene = true;
    }
    ReplyRule::AfterApply { needs_rebuild: rebuild }
}

impl HotReloadPlan {
    /// 何もしない計画か（要求が 1 件も無かった）。
    pub fn is_empty(&self) -> bool {
        self.replies.is_empty()
    }

    /// 適用の結果から、要求ごとの応答の行を作る【純関数】。
    ///
    /// # 引数
    /// * `elapsed_ms` - 適用にかかった時間（ミリ秒。キャッシュの破棄とシーンの読み直しの合計）
    /// * `rebuild`    - シーンの読み直しの結果（読み直していなければ None。成功なら読み直したシーン、失敗なら理由）
    pub fn reply_lines(&self, elapsed_ms: f64, rebuild: Option<&Result<String, String>>) -> Vec<String> {
        self.replies
            .iter()
            .map(|reply| match &reply.rule {
                ReplyRule::Failed { reason } => super::wire::failed_line(&reply.target, reason),
                ReplyRule::Skipped { reason } => super::wire::skipped_line(&reply.target, reason),
                ReplyRule::AfterApply { needs_rebuild: false } => {
                    super::wire::done_line(&reply.target, elapsed_ms, DETAIL_IN_PLACE)
                }
                ReplyRule::AfterApply { needs_rebuild: true } => match rebuild {
                    Some(Ok(scene)) => {
                        super::wire::done_line(&reply.target, elapsed_ms, &format!("{DETAIL_SCENE_PREFIX}{scene}"))
                    }
                    Some(Err(reason)) => super::wire::failed_line(&reply.target, reason),
                    // 計画上は読み直すはずだった（呼び出し側が読み直さなかった）。キャッシュは捨ててある
                    None => super::wire::done_line(&reply.target, elapsed_ms, DETAIL_IN_PLACE),
                },
            })
            .collect()
    }
}

// ============================================================
//  テスト（状態の流れ: 受付 → 計画 → 応答）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use crate::engine::core::app_base::hot_reload::wire::parse;

    /// Play 中・シーン Main を読み込み中の状態。
    fn playing<'a>() -> PlanContext<'a> {
        PlanContext { current_scene: Some("assets://scenes/Main.scene"), assets_root: None, scene_rebuild_allowed: true }
    }

    /// 行の並びを要求の並びにする（テスト用）。
    fn requests(lines: &[&str]) -> Vec<HotReloadRequest> {
        lines.iter().map(|line| parse(line).expect("差し替えの命令")).collect()
    }

    /// 要求が無ければ何もしない計画（シーンも読み直さない）。受付の後に take_plan すると空へ戻る。
    #[test]
    fn empty_batch_plans_nothing_and_drains() {
        let mut batch = HotReloadBatch::default();
        assert!(batch.is_empty());
        assert!(batch.take_plan(playing()).is_empty());

        batch.push(parse("RELOAD_SCENE").unwrap());
        assert!(!batch.is_empty());
        let plan = batch.take_plan(playing());
        assert!(plan.rebuild_scene);
        assert!(batch.is_empty(), "計画に取り出したら貯めた要求は空に戻る（次のフレームへ持ち越さない）");
        assert!(batch.take_plan(playing()).is_empty());
    }

    /// シーンとモデル 2 つを同じフレームに頼まれても、シーンの読み直しは 1 回・応答は 3 行。
    #[test]
    fn scene_rebuild_happens_once_per_frame() {
        let plan = plan(
            &requests(&["RELOAD_SCENE", "RELOAD_ASSET:models/a.glb", "RELOAD_ASSET:models/b.glb"]),
            playing(),
        );
        assert!(plan.rebuild_scene);
        assert_eq!(plan.assets.len(), 2);
        assert_eq!(plan.replies.len(), 3);
        let lines = plan.reply_lines(40.0, Some(&Ok("assets://scenes/Main.scene".to_string())));
        assert_eq!(lines[0], "RELOAD_DONE:scene|40.0|scene:assets://scenes/Main.scene");
        assert_eq!(lines[1], "RELOAD_DONE:asset:models/a.glb|40.0|scene:assets://scenes/Main.scene");
    }

    /// 画像だけならシーンを読み直さない（ゲームの状態を保つ）。
    #[test]
    fn in_place_assets_do_not_rebuild_scene() {
        let plan = plan(&requests(&["RELOAD_ASSET:ui/button.png", "RELOAD_ASSET:shaders/a.wgsl"]), playing());
        assert!(!plan.rebuild_scene);
        assert_eq!(plan.assets.iter().map(|a| a.refresh).collect::<Vec<_>>(), vec![AssetRefresh::InPlace; 2]);
        assert_eq!(plan.reply_lines(1.5, None), vec![
            "RELOAD_DONE:asset:ui/button.png|1.5|inplace".to_string(),
            "RELOAD_DONE:asset:shaders/a.wgsl|1.5|inplace".to_string(),
        ]);
    }

    /// 条件付きのシーンの読み直しは、今のシーンと一致するときだけ（大文字小文字・区切りを問わない）。
    #[test]
    fn conditional_scene_reload_checks_current_scene() {
        let same = plan(&requests(&[r"RELOAD_SCENE:Scenes\main.scene"]), playing());
        assert!(same.rebuild_scene);
        let other = plan(&requests(&["RELOAD_SCENE:scenes/Other.scene"]), playing());
        assert!(!other.rebuild_scene);
        assert!(matches!(other.replies[0].rule, ReplyRule::Skipped { .. }));
        assert!(other.reply_lines(0.0, None)[0].starts_with("RELOAD_SKIPPED:scene:scenes/Other.scene|今のシーンは"));
        // RELOAD_ASSET でシーンファイルを頼んだときも同じ規則
        let as_asset = plan(&requests(&["RELOAD_ASSET:scenes/Main.scene"]), playing());
        assert!(as_asset.rebuild_scene);
        assert!(as_asset.assets.is_empty(), "シーンファイルはキャッシュを持たないので捨てるものは無い");
    }

    /// シーンの読み直しが失敗したら、読み直しに頼った要求は RELOAD_FAILED、キャッシュだけの要求は RELOAD_DONE。
    #[test]
    fn rebuild_failure_is_reported_to_dependent_requests() {
        let plan = plan(&requests(&["RELOAD_ASSET:models/a.glb", "RELOAD_ASSET:ui/b.png"]), playing());
        let lines = plan.reply_lines(5.0, Some(&Err("シーンを読めません".to_string())));
        assert_eq!(lines[0], "RELOAD_FAILED:asset:models/a.glb|シーンを読めません");
        assert_eq!(lines[1], "RELOAD_DONE:asset:ui/b.png|5.0|inplace");
    }

    /// 同じ宛先の要求が重なったら応答は 1 行・捨てるアセットも 1 件。
    #[test]
    fn duplicate_targets_are_coalesced() {
        let plan = plan(&requests(&["RELOAD_ASSET:ui/a.png", "RELOAD_ASSET:./ui/a.png", "RELOAD_SCENE", "RELOAD_SCENE"]), playing());
        assert_eq!(plan.assets.len(), 1);
        assert_eq!(plan.replies.len(), 2);
    }

    /// 読み込み中のシーンが無い・Edit モードではシーンを読み直さない（アセットはキャッシュを捨てるだけ）。
    #[test]
    fn no_scene_or_edit_mode_skips_rebuild() {
        let no_scene = PlanContext { current_scene: None, assets_root: None, scene_rebuild_allowed: true };
        let plan_a = plan(&requests(&["RELOAD_SCENE", "RELOAD_ASSET:models/a.glb"]), no_scene);
        assert!(!plan_a.rebuild_scene);
        assert!(matches!(plan_a.replies[0].rule, ReplyRule::Skipped { .. }));
        assert_eq!(plan_a.replies[1].rule, ReplyRule::AfterApply { needs_rebuild: false });

        let edit = PlanContext { current_scene: Some("C:/p/assets/scenes/Main.scene"), assets_root: None, scene_rebuild_allowed: false };
        let plan_b = plan(&requests(&["RELOAD_SCENE"]), edit);
        assert!(!plan_b.rebuild_scene);
        assert!(plan_b.reply_lines(0.0, None)[0].starts_with("RELOAD_SKIPPED:scene|Edit モード"));
    }

    /// 受け付けない要求・差し替えられない種類はその理由で返す（相手を待たせない）。
    #[test]
    fn invalid_and_unsupported_requests_reply_with_reason() {
        let plan = plan(
            &requests(&["RELOAD_ASSET:../x.png", "RELOAD_ASSET:fonts/a.ttf", "RELOAD_ASSET:scripts/P.cs"]),
            playing(),
        );
        assert!(!plan.rebuild_scene);
        assert!(plan.assets.is_empty());
        let lines = plan.reply_lines(0.0, None);
        assert!(lines[0].starts_with("RELOAD_FAILED:asset:../x.png|"), "{}", lines[0]);
        assert!(lines[1].starts_with("RELOAD_SKIPPED:asset:fonts/a.ttf|"), "{}", lines[1]);
        assert!(lines[2].starts_with("RELOAD_SKIPPED:asset:scripts/P.cs|"), "{}", lines[2]);
    }
}
