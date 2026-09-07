// ============================================================
//  thumbnail_ops.rs — アクタ・サムネイル（図鑑画像）生成の駆動
// ------------------------------------------------------------
//  役割:
//    IPC `RENDER_ACTOR_THUMBNAIL:{actor},{out_png},{size_px},{view}` を受けて、
//    「そのアクタだけが写った、背景が透明な正方形 PNG」を 1 枚書き出す。
//
//  なぜ状態機械なのか:
//    サムネイルは 1 回の関数呼び出しでは作れない。理由は 2 つある。
//      1) 被写体の AABB は「1 度描いてみないと」分からない。ワールド AABB は
//         GPU バッチ（InstancedModelBatch::world_aabbs）が描画準備の過程で
//         計算・キャッシュするもので、CPU 側に事前計算された境界は存在しない。
//      2) カラーの読み戻しはスワップチェーンの提示テクスチャからしか取れず、
//         これは実際にフレームを 1 枚描いて present するまで存在しない。
//    そのため「読み込む → 1 枚描く → AABB からカメラを決める → もう 1 枚描いて撮る
//    → 合成して書き出す」と複数フレームにまたがる。その進行を [`ThumbnailJob`] が持つ。
//
//  どうやって現在のシーンを壊さずに撮るか:
//    SEED の描画は `App::active_world_line` に属するアクタ**だけ**を集める
//    （actor_utils::collect_mcs_in_world_line が唯一の入口で、メッシュ・地形・水・
//    パーティクル・スカイボックス・キャンバスすべてがこの値で絞られる）。
//    そこで専用の隔離ワールド線 [`THUMBNAIL_WORLD_LINE`] へアクタを 1 体だけ読み込み、
//    その間だけ active_world_line を差し替える。ユーザーのシーン（ワールド線 0）は
//    エンティティごと生き残ったまま、単に「描かれない」状態になる。
//    撮影が終わったら隔離ワールド線のエンティティを despawn し、
//    active_world_line とカメラを元に戻す。**シーンには一切触れない**。
//
//  背景の抜き方:
//    カラーバッファの背景にはクリア色が焼き込まれており、色からは被写体との境界を
//    復元できない（魚の体色とクリア色が一致しうる）。そこで ID パス
//    （drawer/id_pass.rs）のアルファ成分を使う。ID パスは各ピクセルへ
//    `bitcast<f32>(instance_id)` を書き、背景は必ず 0 になるので、
//    「ID != 0」が色に依存しない完全な被写体マスクになる。
//
//  スクリプトを走らせない理由:
//    `Scene::load_actor_into` に `scripting_host: None` を渡すと ScriptComponent の
//    CLR インスタンス自体が作られない。魚の遊泳スクリプトが動いて撮影中に
//    位置がずれることも、FishingController を探して警告を吐くこともない。
// ============================================================

use std::path::PathBuf;

use crate::engine::core::app_base::scene::Scene;
use crate::engine::core::renderer::actor_thumbnail::{
    self, CropRect, Framing, RefineDecision, ThumbnailRequest, FRAMING_MARGIN_RATIO,
};
use crate::engine::core::renderer::screenshot;
use crate::engine::structs::objects::Actor;
use crate::engine::structs::tensor::Vector3;
use crate::engine::structs::transforms::Quaternion;

use super::App;

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// サムネイル生成専用の隔離ワールド線 ID。
///
/// アクタ編集タブが使う値（エディタが 1 から順に払い出す）と衝突しないよう、
/// 現実的に到達しない大きな値を予約する。
pub const THUMBNAIL_WORLD_LINE: u32 = 900_000_001;

/// アクタ読み込み後、構図を決めるまでに描くフレーム数。
///
/// AABB は ECS から直接求める（描画結果に依存しない）ので原理的には 0 でよいが、
/// 読み込んだモデルの GPU 資源（頂点バッファ・テクスチャ・統合バッチ）が
/// 最初のフレームで整うため、1 枚は回してから構図を決める。
const WARMUP_FRAMES: u32 = 1;

/// カメラを据えてから撮影するまでに描くフレーム数。
///
/// カメラを大きく動かすと、その位置での可視判定（フラスタム／Hi-Z カリング）が
/// 落ち着くまで 1 フレームかかることがある。撮り逃しを避けるための猶予。
const SETTLE_FRAMES: u32 = 2;

/// サムネイル生成セッションを畳むまでのアイドル時間 [秒]。
///
/// 【なぜセッションが要るのか】1 匹撮り終えるたびに `active_world_line` を
/// ユーザーのシーン（0）へ戻すと、その 1 フレームのためにシーン全体の
/// レイトレーシング加速構造（地形・スキンモデルの BLAS と TLAS）が組み直される。
/// 実測では 31 匹の一括生成中、11 匹目でランタイムが GPU 資源を使い切って落ちた。
///
/// そこで「次の要求が来ている間は隔離ワールド線に留まる」。
/// エディタは 1 匹ずつ直列に投げてくるので次の要求はミリ秒単位で届き、
/// この時間だけ何も来なければ「一括生成は終わった」とみなして元のシーンへ戻す。
const SESSION_IDLE_SECONDS: f32 = 2.0;

/// 構図の追い込み（撮影 → 実測 → カメラ修正 → 撮り直し）の最大回数。
///
/// モデルのローカル AABB は実際に描かれる範囲より大きいことがあり
/// （描画されない補助メッシュ・スキンのバインドポーズのずれなど）、
/// AABB だけで構図を決めると被写体が隅に小さく寄ってしまう prefab が実在した。
/// そこで撮った ID マスクの実際の広がりから構図を測り直し、収束するまで撮り直す。
/// 正射投影ではピクセル ↔ ワールドが線形なので通常 1〜2 回で収まる。
const MAX_REFINE_PASSES: u32 = 4;

/// 1 ジョブが完了せずに待てる実時間の上限（ハングの保険）[秒]。
///
/// ID バッファが用意されていない、ウィンドウが 0x0 でフレームがそもそも回らない、
/// といった状況で永久に待たないための番人。エディタ側のタイムアウト（1 匹 60 秒）より
/// 短くして、無応答ではなく理由付きの失敗が返るようにしてある。
const JOB_DEADLINE_SECONDS: u64 = 30;

/// 被写体が 1 ピクセルも写っていなかったときのエラーメッセージ。
const ERROR_EMPTY_RENDER: &str = "アクタが 1 ピクセルも描画されませんでした（モデルが無い／読み込みに失敗した可能性）";

// ============================================================
//  ThumbnailJob — サムネイル 1 枚ぶんの進行状態
// ============================================================

/// ジョブの進行段階。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Phase {
    /// アクタ読み込み直後。AABB が埋まるまでフレームを空回しする。
    Warmup { frames_left: u32 },
    /// カメラを据えた直後。カリングが落ち着くまで待つ。
    Settle { frames_left: u32 },
    /// 撮影フレーム。ID パスを強制し、カラーの読み戻しも要求済み。
    Capture,
    /// カラーと ID マスクが揃うのを待っている。
    Await,
}

/// サムネイル生成ジョブ 1 件。`App::thumbnail_job` に入っている間だけ生きる。
pub struct ThumbnailJob {
    /// 要求の内容（出力先・サイズ・ビュー）。
    request: ThumbnailRequest,
    /// 現在の段階。
    phase: Phase,
    /// ジョブ開始時刻（デッドライン判定用）。
    started_at: std::time::Instant,
    /// 撮影フレームで読み出した ID マスク（`width × height` 要素、0 = 背景）。
    id_mask: Option<Vec<f32>>,
    /// ID マスクの幅・高さ。
    id_size: (u32, u32),
    /// 現在の構図（追い込みで更新される）。
    framing: Framing,
    /// これまでに行った追い込みの回数。
    refine_passes: u32,
}

/// サムネイル生成セッション。
///
/// 「最初の 1 匹を撮る直前の App の状態」を丸ごと抱え、
/// 連続生成が途切れた（[`SESSION_IDLE_SECONDS`] 無音になった）ところで復元する。
/// セッションが生きている間、`active_world_line` は隔離ワールド線のまま留まる。
pub struct ThumbnailSession {
    /// 直近のジョブが終わった時刻。None = ジョブ実行中。
    idle_since: Option<std::time::Instant>,
    /// 撮影前の `active_world_line`。
    active_world_line: u32,
    /// 撮影前のデバッグカメラ（位置・回転・投影をまるごと）。
    camera: crate::engine::structs::objects::camera::DebugCamera,
    /// 隔離ワールド線が `canvas_world_lines` に登録されていたか（通常は false）。
    was_canvas_world_line: bool,
    /// 隔離ワールド線が `actor_edit_canvas_wls` に登録されていたか（通常は false）。
    was_actor_edit_canvas: bool,
}

impl ThumbnailJob {
    /// 撮影フレームか（frame_renderer から ID パス強制の判定に使う）。
    pub fn wants_capture_this_frame(&self) -> bool {
        self.phase == Phase::Capture
    }

    /// フレームを 1 枚描き終えたことを伝える（frame_renderer が present 後に呼ぶ）。
    ///
    /// 待機フレーム数は「イベントループが回った回数」ではなく
    /// **実際に描かれたフレーム数**で数える必要がある。about_to_wait は
    /// フレームと無関係に何度でも呼ばれるので、そこで数えると
    /// 1 枚も描かないうちに待機が明けてしまう。
    pub fn on_frame_rendered(&mut self) {
        self.phase = match self.phase {
            Phase::Warmup { frames_left } => {
                Phase::Warmup { frames_left: frames_left.saturating_sub(1) }
            }
            Phase::Settle { frames_left } => {
                Phase::Settle { frames_left: frames_left.saturating_sub(1) }
            }
            // Capture / Await はフレーム数ではなく読み戻しの到着で進む
            other => other,
        };
    }

    /// 撮影フレームの ID マスクを受け取る（frame_renderer が GPU サブミット後に呼ぶ）。
    ///
    /// `mask` が None なら ID バッファが用意されていなかったということなので、
    /// マスク無しのまま待機段階へ移し、次の poll で失敗として片付ける。
    pub fn receive_id_mask(&mut self, mask: Option<Vec<f32>>, width: u32, height: u32) {
        self.id_mask = mask;
        self.id_size = (width, height);
        self.phase = Phase::Await;
    }
}

// ============================================================
//  App 側の処理
// ============================================================

impl App {
    /// `RENDER_ACTOR_THUMBNAIL:` を受理する（IPC ディスパッチから 1 行で呼ばれる）。
    ///
    /// 引数は生の文字列で受け取り、ここで解釈する。解釈に失敗した内容も
    /// エディタへ理由付きで返したいので、パースを ipc.rs 側では行わない。
    pub(super) fn handle_render_actor_thumbnail(&mut self, args: &str) {
        // 1 枚ずつ逐次実行する前提。前のジョブが残っているうちは受け付けない。
        if self.thumbnail_job.is_some() {
            self.reply_thumbnail_error("前のサムネイル生成がまだ終わっていません");
            return;
        }

        let request = match actor_thumbnail::parse_request_args(args) {
            Ok(request) => request,
            Err(message) => {
                self.reply_thumbnail_error(&message);
                return;
            }
        };

        if let Err(message) = self.begin_thumbnail_job(request) {
            self.reply_thumbnail_error(&message);
        }
    }

    /// アクタを隔離ワールド線へ読み込み、状態を退避してジョブを開始する。
    fn begin_thumbnail_job(&mut self, request: ThumbnailRequest) -> Result<(), String> {
        let Some(draw_ctx) = self.draw_ctx.as_ref() else {
            return Err("レンダラーが初期化されていません".to_string());
        };

        // ── 1. セッションを開始する（2 匹目以降は既存のものを使い回す）──
        //   ここで退避した状態は、連続生成が途切れたときに一度だけ復元される。
        if self.thumbnail_session.is_none() {
            self.thumbnail_session = Some(ThumbnailSession {
                idle_since: None,
                active_world_line: self.active_world_line,
                camera: self.camera.clone(),
                was_canvas_world_line: self.canvas_world_lines.contains(&THUMBNAIL_WORLD_LINE),
                was_actor_edit_canvas: self.actor_edit_canvas_wls.contains(&THUMBNAIL_WORLD_LINE),
            });
        }
        if let Some(session) = self.thumbnail_session.as_mut() {
            // ジョブ実行中はアイドル判定の対象外
            session.idle_since = None;
        }

        // ── 2. 隔離ワールド線を掃除してからアクタを 1 体だけ読み込む ──
        let scene = self.scene.get_or_insert_with(|| Scene::new("main"));
        despawn_world_line(scene, THUMBNAIL_WORLD_LINE);

        let path = actor_thumbnail::resolve_actor_path(&request.actor_path);
        let actor = Scene::load_actor_into(
            &path,
            draw_ctx,
            &mut scene.world,
            // スクリプトは一切生成しない（撮影中に魚が泳ぎ出さないようにする）
            None,
            THUMBNAIL_WORLD_LINE,
            None,
        )
        .map_err(|e| format!("アクタを読み込めません（{}）: {e}", path.display()))?;

        // 2D アクタ（キャンバス）は本機能の対象外。ワールド線を汚さずに弾く。
        if actor.is_2d() {
            despawn_world_line(scene, THUMBNAIL_WORLD_LINE);
            return Err("2D アクタ（キャンバス）のサムネイルには対応していません".to_string());
        }
        scene.actors.push(actor);

        // ── 3. 隔離ワールド線を 3D として登録し、そこへ切り替える ──
        self.canvas_world_lines.remove(&THUMBNAIL_WORLD_LINE);
        self.actor_edit_canvas_wls.remove(&THUMBNAIL_WORLD_LINE);
        self.active_world_line = THUMBNAIL_WORLD_LINE;

        // 前回の読み戻し結果が残っていると古い絵を掴むので必ず捨てる
        screenshot::clear_thumbnail_color();

        let view = request.view;
        self.thumbnail_job = Some(ThumbnailJob {
            request,
            phase: Phase::Warmup { frames_left: WARMUP_FRAMES },
            started_at: std::time::Instant::now(),
            id_mask: None,
            id_size: (0, 0),
            // Warmup 明けに compute_thumbnail_framing で必ず上書きされる暫定値
            framing: actor_thumbnail::compute_framing(
                [0.0; 3], [0.0; 3], view, 1, 1, FRAMING_MARGIN_RATIO,
            ),
            refine_passes: 0,
        });

        self.request_thumbnail_frame();
        Ok(())
    }

    /// ジョブを 1 段進める（毎周 about_to_wait から呼ぶ。ジョブが無ければ即 return）。
    pub(super) fn poll_thumbnail_job(&mut self) {
        if self.thumbnail_job.is_none() {
            // ジョブが無いなら、連続生成が途切れていないかだけ見る
            self.end_thumbnail_session_if_idle();
            return;
        }

        // デッドライン超過は最優先で打ち切る（フレームが回らない状況でのハング防止）
        let expired = self
            .thumbnail_job
            .as_ref()
            .is_some_and(|job| job.started_at.elapsed().as_secs() >= JOB_DEADLINE_SECONDS);
        if expired {
            self.finish_thumbnail_job(Err(format!(
                "{JOB_DEADLINE_SECONDS} 秒以内に描画が完了しませんでした（フレームが回っていない可能性）"
            )));
            return;
        }

        let phase = self.thumbnail_job.as_ref().expect("存在を確認済み").phase;
        match phase {
            Phase::Warmup { frames_left } => {
                if frames_left > 0 {
                    // まだ AABB が埋まっていない可能性がある。フレームを回して待つ
                    //（カウンタを減らすのは on_frame_rendered = 実際に描けたときだけ）。
                    self.request_thumbnail_frame();
                    return;
                }
                // AABB が確定したはず。構図を決めてカメラを据える。
                match self.compute_thumbnail_framing() {
                    Ok(framing) => {
                        self.apply_thumbnail_camera(&framing);
                        if let Some(job) = self.thumbnail_job.as_mut() {
                            job.framing = framing;
                            job.phase = Phase::Settle { frames_left: SETTLE_FRAMES };
                        }
                        self.request_thumbnail_frame();
                    }
                    Err(message) => self.finish_thumbnail_job(Err(message)),
                }
            }

            Phase::Settle { frames_left } => {
                if frames_left > 0 {
                    // カリングが落ち着くまでフレームを回して待つ（減算は on_frame_rendered）。
                    self.request_thumbnail_frame();
                    return;
                }
                // 撮影フレームへ。カラーの読み戻しを予約してから 1 枚描かせる。
                // （ID パスの強制は wants_capture_this_frame() が Capture 段階で担う）
                screenshot::request_thumbnail_color();
                if let Some(job) = self.thumbnail_job.as_mut() {
                    job.phase = Phase::Capture;
                }
                self.request_thumbnail_frame();
            }

            Phase::Capture => {
                // 撮影フレームがまだ描かれていない。描かれれば receive_id_mask が
                // 段階を Await へ進めてくれる。
                self.request_thumbnail_frame();
            }

            Phase::Await => {
                // カラーが揃うまで待つ（ID マスクは receive_id_mask で受領済み）
                let Some(color) = screenshot::take_thumbnail_color_result() else {
                    self.request_thumbnail_frame();
                    return;
                };

                // 実測した被写体の広がりから、構図が十分かを判断する。
                // 足りなければカメラを直して Settle へ戻り、もう一度撮る。
                if self.try_refine_thumbnail_framing() {
                    // 前回の読み戻し結果は捨てる（古い絵を合成しないため）
                    screenshot::clear_thumbnail_color();
                    self.request_thumbnail_frame();
                    return;
                }

                let result = self.compose_thumbnail(color);
                self.finish_thumbnail_job(result);
            }
        }
    }

    /// 撮影中はフレームを能動的に要求する（非表示ウィンドウでも進むようにする）。
    fn request_thumbnail_frame(&mut self) {
        if let Some(window) = &self.window {
            window.request_redraw();
        }
    }

    /// 隔離ワールド線に読み込んだアクタのワールド AABB を求め、構図を決める。
    ///
    /// # なぜ描画バッチ（`shared_model_batches`）から取らないのか
    /// あのマップは batch_key をキーにした**永続**キャッシュで、そのフレームに
    /// 存在しないモデルのエントリも `world_aabbs` を抱えたまま残る。
    /// 全バッチを合成すると、直前まで描いていたシーン全体（地形を含む）の
    /// 古い境界まで巻き込んでしまい、AABB が桁違いに大きくなる
    /// ＝被写体が数ピクセルに潰れて 1 ピクセルも写らない、という失敗になる。
    ///
    /// そこで ECS を直接引く。描画と**同じ絞り込み**（`collect_mcs_in_world_line`）を
    /// 使い、各 ModelComponent の CPU モデルのローカル AABB を、描画と**同じ行列**
    /// （`render_matrix` ＝ インスタンス行列 × 表示オフセット）で変換して合成する。
    /// これなら「画面に出るもの」と AABB が定義上ずれない。
    fn compute_thumbnail_framing(&self) -> Result<Framing, String> {
        let job = self.thumbnail_job.as_ref().ok_or("ジョブがありません")?;
        let scene = self.scene.as_ref().ok_or("シーンがありません")?;

        let mut bounds: Option<([f32; 3], [f32; 3])> = None;
        let mcs = super::actor_utils::collect_mcs_in_world_line(
            &scene.actors,
            &scene.world,
            THUMBNAIL_WORLD_LINE,
        );
        for (_, _, _, mc) in &mcs {
            // 非表示のモデルは絵に出ないので構図にも入れない
            if !mc.visible {
                continue;
            }
            let Some(model) = mc.model.as_ref() else { continue };
            let (local_min, local_max) = model.local_aabb();
            for instance in &mc.instance_mats {
                let world = mc.render_matrix(*instance);
                let transformed = actor_thumbnail::transform_aabb(local_min, local_max, &world);
                bounds = Some(match bounds {
                    Some(current) => actor_thumbnail::merge_aabb(current, transformed),
                    None => transformed,
                });
            }
        }

        let (min, max) = bounds.ok_or(
            "アクタの境界（AABB）を取得できませんでした（描画対象のモデルがありません）",
        )?;

        let (width, height) = self.thumbnail_viewport_size();
        Ok(actor_thumbnail::compute_framing(
            min,
            max,
            job.request.view,
            width,
            height,
            FRAMING_MARGIN_RATIO,
        ))
    }

    /// 実際に描画されるフレームバッファの大きさ（ピクセル）。
    ///
    /// ID バッファはウィンドウのリサイズに追随して作り直されるので、
    /// これがカラーターゲットと同じ「実効解像度」の最も確実な情報源になる。
    fn thumbnail_viewport_size(&self) -> (u32, u32) {
        if let Some(id_buf) = &self.id_buffer {
            if id_buf.width > 0 && id_buf.height > 0 {
                return (id_buf.width, id_buf.height);
            }
        }
        match &self.window {
            Some(window) => {
                let size = window.inner_size();
                (size.width.max(1), size.height.max(1))
            }
            None => (1, 1),
        }
    }

    /// 算出した構図をデバッグカメラへ即座に反映する（補間は一切挟まない）。
    ///
    /// サムネイル生成中は frame_renderer 側でカメラ更新をまるごと止めてあるので、
    /// ここで入れた値がそのまま撮影に使われる。
    fn apply_thumbnail_camera(&mut self, framing: &Framing) {
        let (width, height) = self.thumbnail_viewport_size();

        // 位置・回転（回転は yaw→pitch の順。camera_ops::apply_camera_data と同じ合成）
        self.camera.base.transform.position =
            Vector3::new(framing.eye[0], framing.eye[1], framing.eye[2]);
        self.camera.yaw = framing.yaw;
        self.camera.pitch = framing.pitch;
        let yaw_q = Quaternion::from_axis_angle(Vector3::new(0.0, 1.0, 0.0), framing.yaw);
        let pitch_q = Quaternion::from_axis_angle(Vector3::new(1.0, 0.0, 0.0), framing.pitch);
        self.camera.base.transform.rotation = yaw_q * pitch_q;

        // 投影: 正射へ即座に切り替える（blend と target を同値にして補間を起こさせない）
        self.camera.ortho_blend = 1.0;
        self.camera.ortho_target = 1.0;
        self.camera.ortho_half_h = framing.ortho_half_h;
        self.camera.base.projection.near = framing.near;
        self.camera.base.projection.far = framing.far;
        // 正射の横幅は half_h × aspect で決まるので、アスペクト比を実解像度に合わせる
        self.camera.base.set_aspect_ratio(width, height);
        self.camera.viewport_h_px = height as f32;
    }

    /// 撮った ID マスクから構図を測り直し、必要ならカメラを直して撮り直させる。
    ///
    /// # 戻り値
    /// `true` = 撮り直す（段階を Settle へ戻した）。`false` = このまま合成してよい。
    ///
    /// 追い込み回数が [`MAX_REFINE_PASSES`] に達したら、たとえ理想的でなくても
    /// 打ち切って合成する（画が出ないより、多少寄りが甘くても出るほうがよい）。
    fn try_refine_thumbnail_framing(&mut self) -> bool {
        let Some(job) = self.thumbnail_job.as_ref() else { return false };
        if job.refine_passes >= MAX_REFINE_PASSES {
            return false;
        }
        let Some(id_mask) = job.id_mask.as_ref() else { return false };
        let (mask_w, mask_h) = job.id_size;

        // 1 ピクセルも写っていないなら追い込みようがない（compose 側で失敗させる）
        let Some(bounds) = actor_thumbnail::measure_mask_bounds(id_mask, mask_w, mask_h) else {
            return false;
        };

        let crop = actor_thumbnail::center_square_crop(mask_w, mask_h);
        let decision = actor_thumbnail::refine_framing(
            bounds,
            crop,
            mask_w,
            mask_h,
            job.framing.ortho_half_h,
            FRAMING_MARGIN_RATIO,
        );

        let RefineDecision::Retry { ortho_half_h, pan_right, pan_up } = decision else {
            return false;
        };

        // 画面右・上のワールドベクトルへパン量を戻し、視点と注視点を平行移動する。
        let view = job.request.view;
        let right = view.right();
        let up = view.up_vector();
        let mut framing = job.framing;
        for axis in 0..3 {
            let shift = right[axis] * pan_right + up[axis] * pan_up;
            framing.eye[axis] += shift;
            framing.target[axis] += shift;
        }
        framing.ortho_half_h = ortho_half_h;

        self.apply_thumbnail_camera(&framing);
        if let Some(job) = self.thumbnail_job.as_mut() {
            job.framing = framing;
            job.refine_passes += 1;
            job.id_mask = None;
            job.phase = Phase::Settle { frames_left: SETTLE_FRAMES };
        }
        true
    }

    /// カラーと ID マスクを合成して PNG を書き出す。
    fn compose_thumbnail(
        &mut self,
        color: Result<(Vec<u8>, u32, u32), String>,
    ) -> Result<PathBuf, String> {
        let (mut pixels, color_w, color_h) =
            color.map_err(|e| format!("カラーバッファを読み戻せませんでした: {e}"))?;

        let job = self.thumbnail_job.as_ref().ok_or("ジョブがありません")?;
        let id_mask = job
            .id_mask
            .as_ref()
            .ok_or("ID バッファを読み戻せませんでした（マスクを作れません）")?;
        let (id_w, id_h) = job.id_size;

        // カラーと ID の解像度が食い違うと、ピクセルの対応が取れずマスクが破綻する。
        // （リサイズが撮影フレームに割り込んだ場合など）
        if (color_w, color_h) != (id_w, id_h) {
            return Err(format!(
                "カラー（{color_w}x{color_h}）と ID バッファ（{id_w}x{id_h}）の解像度が一致しません"
            ));
        }

        // 1. ID != 0 のピクセルだけ不透明にする
        let opaque = actor_thumbnail::apply_id_mask_alpha(&mut pixels, id_mask);
        if opaque == 0 {
            return Err(ERROR_EMPTY_RENDER.to_string());
        }

        // 2. 中央の正方形だけ切り出す（構図はこの正方形に収まるよう決めてある）
        let rect: CropRect = actor_thumbnail::center_square_crop(color_w, color_h);
        let (mut square, square_w, square_h) =
            actor_thumbnail::crop_rgba(&pixels, color_w, color_h, rect);
        if square_w != square_h || square_w == 0 {
            return Err(format!("正方形を切り出せませんでした（{square_w}x{square_h}）"));
        }

        // 3. アルファブリード → 縮小 → PNG
        let out_path = PathBuf::from(&job.request.out_png);
        let size_px = job.request.size_px;
        actor_thumbnail::write_thumbnail_png(&mut square, square_w, size_px, &out_path)?;
        Ok(out_path)
    }

    /// ジョブを終了し、状態を復帰して応答を返す。
    ///
    /// 成否によらず**必ず**ここを通す（復帰を 1 か所に集約して漏れを防ぐ）。
    fn finish_thumbnail_job(&mut self, result: Result<PathBuf, String>) {
        if self.thumbnail_job.take().is_none() {
            return;
        }

        // ── 1. 隔離ワールド線のエンティティを片付ける ─────────
        if let Some(scene) = self.scene.as_mut() {
            despawn_world_line(scene, THUMBNAIL_WORLD_LINE);
        }

        // ── 2. まだシーンへは戻さない ─────────────────────────
        //   ここで active_world_line を戻すと、その 1 フレームのためだけに
        //   シーン全体のレイトレーシング加速構造が組み直され、連続生成が破綻する
        //   （SESSION_IDLE_SECONDS のコメント参照）。
        //   次の要求が来なくなってから end_thumbnail_session_if_idle が復元する。
        if let Some(session) = self.thumbnail_session.as_mut() {
            session.idle_since = Some(std::time::Instant::now());
        }

        // ── 3. 読み戻しレーンに残骸を残さない ─────────────────
        screenshot::clear_thumbnail_color();

        // ── 4. 応答 ───────────────────────────────────────────
        let reply = match result {
            Ok(path) => actor_thumbnail::format_done(&path.to_string_lossy()),
            Err(message) => actor_thumbnail::format_error(&message),
        };
        if let Some(ipc) = &self.ipc {
            ipc.send(&reply);
        }

    }

    /// 連続生成が途切れていたら、セッションを畳んでシーンを元に戻す。
    ///
    /// 毎周 `poll_thumbnail_job` から呼ばれる。ジョブ実行中（`idle_since == None`）や
    /// まだ [`SESSION_IDLE_SECONDS`] 経っていない間は何もしない。
    fn end_thumbnail_session_if_idle(&mut self) {
        let expired = self.thumbnail_session.as_ref().is_some_and(|session| {
            session
                .idle_since
                .is_some_and(|at| at.elapsed().as_secs_f32() >= SESSION_IDLE_SECONDS)
        });
        if !expired {
            return;
        }
        let Some(session) = self.thumbnail_session.take() else { return };

        // 隔離ワールド線に残っている最後のアクタを片付ける
        if let Some(scene) = self.scene.as_mut() {
            despawn_world_line(scene, THUMBNAIL_WORLD_LINE);
        }

        // セッション開始前の状態へ戻す
        self.active_world_line = session.active_world_line;
        self.camera = session.camera;
        if session.was_canvas_world_line {
            self.canvas_world_lines.insert(THUMBNAIL_WORLD_LINE);
        } else {
            self.canvas_world_lines.remove(&THUMBNAIL_WORLD_LINE);
        }
        if session.was_actor_edit_canvas {
            self.actor_edit_canvas_wls.insert(THUMBNAIL_WORLD_LINE);
        } else {
            self.actor_edit_canvas_wls.remove(&THUMBNAIL_WORLD_LINE);
        }

        // 元のワールド線の絵に戻すため、もう 1 枚描かせる
        self.request_thumbnail_frame();
    }

    /// ジョブを起こす前の失敗（引数不正など）をそのまま返す。
    fn reply_thumbnail_error(&self, message: &str) {
        if let Some(ipc) = &self.ipc {
            ipc.send(&actor_thumbnail::format_error(message));
        }
    }
}

// ============================================================
//  ワールド線の掃除
// ============================================================

/// 指定ワールド線に属するアクタを、ECS エンティティごとシーンから取り除く。
///
/// アクタ編集タブ（ipc_handler.rs の `OpenActor`）と同じ手順:
/// アクタ本体・コンポーネントスロット・子孫のエンティティをすべて集めて despawn し、
/// アクタ配列からも落とす。ワールド線 0（ユーザーのシーン）には触れない。
fn despawn_world_line(scene: &mut Scene, world_line: u32) {
    fn collect_entities(actor: &Actor, out: &mut Vec<crate::engine::ecs::Entity>) {
        out.push(actor.entity);
        out.extend(actor.slot_entities());
        for child in actor.children() {
            collect_entities(child, out);
        }
    }

    let mut to_despawn = Vec::new();
    for actor in scene.actors.iter().filter(|a| a.world_line == world_line) {
        collect_entities(actor, &mut to_despawn);
    }
    for entity in to_despawn {
        scene.world.despawn(entity);
    }
    scene.actors.retain(|a| a.world_line != world_line);
}

// ============================================================
//  単体テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 隔離ワールド線 ID は、エディタが払い出すアクタ編集タブの値と衝突しないこと。
    ///
    /// アクタ編集タブのワールド線はタブを開くたびに 1 から順に増える。
    /// 現実に到達しない大きさであることを、定数の変更で崩さないための番人。
    #[test]
    fn thumbnail_world_line_is_far_out_of_band() {
        assert!(
            THUMBNAIL_WORLD_LINE > 1_000_000,
            "隔離ワールド線 {THUMBNAIL_WORLD_LINE} が小さすぎる（編集タブと衝突しうる）"
        );
        // 通常シーンのワールド線 0 とは必ず異なる
        assert_ne!(THUMBNAIL_WORLD_LINE, 0);
    }

    /// 待機フレーム数がデッドライン（実時間）に対して現実的であること。
    ///
    /// 60FPS を大きく下回る 10FPS でも、全フェーズを消化しきってなお余裕がある設定か。
    #[test]
    fn job_deadline_leaves_room_for_all_phases() {
        const PESSIMISTIC_FPS: u64 = 10;
        let needed_frames = (WARMUP_FRAMES + SETTLE_FRAMES + 2) as u64; // + Capture + Await
        let budget_frames = JOB_DEADLINE_SECONDS * PESSIMISTIC_FPS;
        assert!(
            budget_frames > needed_frames * 2,
            "デッドライン {JOB_DEADLINE_SECONDS} 秒（{budget_frames} フレーム相当）が              必要フレーム数 {needed_frames} に対して短すぎる"
        );
    }

    /// on_frame_rendered が待機カウンタだけを減らし、撮影段階を勝手に進めないこと。
    #[test]
    fn on_frame_rendered_only_advances_waiting_phases() {
        // Warmup / Settle は 1 フレームごとに 1 減る
        assert_eq!(
            tick(Phase::Warmup { frames_left: 2 }),
            Phase::Warmup { frames_left: 1 }
        );
        assert_eq!(
            tick(Phase::Settle { frames_left: 1 }),
            Phase::Settle { frames_left: 0 }
        );
        // 0 で止まる（アンダーフローしない）
        assert_eq!(
            tick(Phase::Warmup { frames_left: 0 }),
            Phase::Warmup { frames_left: 0 }
        );
        // Capture / Await はフレームでは進まない（読み戻しの到着だけが進める）
        assert_eq!(tick(Phase::Capture), Phase::Capture);
        assert_eq!(tick(Phase::Await), Phase::Await);
    }

    /// [`ThumbnailJob::on_frame_rendered`] の段階遷移だけをテストするヘルパ。
    ///
    /// ジョブ本体は GPU 由来の状態（退避カメラ等）を抱えていて素朴には作れないので、
    /// 遷移規則そのものを同じ形で写して検証する。
    fn tick(phase: Phase) -> Phase {
        match phase {
            Phase::Warmup { frames_left } => {
                Phase::Warmup { frames_left: frames_left.saturating_sub(1) }
            }
            Phase::Settle { frames_left } => {
                Phase::Settle { frames_left: frames_left.saturating_sub(1) }
            }
            other => other,
        }
    }
}
