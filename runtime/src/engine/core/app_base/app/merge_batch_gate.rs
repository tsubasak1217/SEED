// ============================================================
//  merge_batch_gate.rs — 統合バッチ更新のダーティゲート
//
//  》含む処理「
//  - MergeBatchSignature: 統合バッチ 1 件の GPU アップロード入力スナップショット
//  - MergeBatchGate:      前フレームとの一致判定＋速度バッファ整定を織り込んだスキップ判定
//
//  フレームループの「描画/統合バッチ更新」区間は、毎フレーム全統合バッチに対して
//  `mark_dirty()` → `update()`（rayon でのワールド行列再計算・全ノードバッファ書込・
//  ID バッファ書込）を無条件で実行していた。入力（インスタンス行列・絶対 ID・
//  セマンティックタグ・アニメ権威時刻・距離 LOD 振り分け結果）が前フレームから
//  1 ビットも変わっていないフレームでは、この再計算の出力は完全に同一になる。
//  本モジュールはその「入力不変」を判定してスキップするためのゲートを提供する。
// ============================================================

use crate::engine::core::renderer::skin_system::SkinAnimPose;

/// 入力不変が「何フレーム連続したら」スキップに入るかのしきい値。
///
/// **2 でなければならない理由（速度バッファの整定）**:
/// `InstancedModelBatch::update()` はモーションベクタ用に「前フレーム行列バッファ」を
/// 毎回アップロードする。行列が B → A へ変化したフレーム N では、GPU 上の前フレーム
/// バッファは B（＝正しい速度 B→A）になる。ここで N+1 を即スキップすると、実際には
/// 静止しているのに前フレームバッファが B のまま残り、速度が 1 フレーム余計に出て
/// しまう（TAA・モーションブラーの見た目が変わる）。
/// 一致 1 回目（N+1）は通常どおり update して前フレームバッファを A へ整定させ、
/// 一致 2 回目（N+2）以降でスキップする。これで GPU 上の状態は「毎フレーム update
/// した場合」と完全に同一になり、見た目は 1 ビットも変わらない。
pub(crate) const MERGE_SKIP_STABLE_FRAMES: u32 = 2;

/// 統合バッチ 1 件について、そのフレームに `update()` を実行した／省いた**理由**。
///
/// 「静止しているはずのシーンで毎フレーム 40 バッチが更新される」といった症状を
/// 計測から切り分けるために、ゲートの判定結果を分類して持ち出せるようにしたもの。
/// `Skipped` 以外はすべて「このフレームは `update()` を実行した」を意味する。
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum MergeGateReason {
    /// 入力不変が整定フレーム数ぶん続いたので `update()` を省いた。
    Skipped,
    /// スナップショット未取得（初回フレーム／バッチ再生成直後）。
    FirstFrame,
    /// インスタンス行列列が変化した。
    Mats,
    /// 絶対 ID 列が変化した（ピッキング ID バッファが陳腐化する）。
    AbsIds,
    /// セマンティックタグ列が変化した。
    Tags,
    /// Animator 駆動の再生指定（アニメ index・時刻・クロスフェード weight）が変化した。
    /// 重い入力（行列・ID・タグ・LOD）も同時に動いた／整定前なので、通常の全更新を行う。
    Pose,
    /// **再生指定だけ**が変化し、重い入力（行列・絶対 ID・タグ・LOD 無効フラグ・
    /// LOD バケット割り当て）は整定済みで 1 ビットも動いていない。
    ///
    /// この場合、`InstancedModelBatch::update()` の出力のうち実際に変わるのは
    /// スキンの再生指定アップロードだけで、ワールド行列キャッシュ・LOD 振り分け・
    /// 全ノードバッファ・ID バッファ・前フレーム行列は前回とビット単位で同一になる。
    /// 呼び出し元は全更新の代わりに `update_poses_only()` を使ってよい。
    ///
    /// 「整定済み」の条件が要るのは速度バッファのため。重い入力が動いた次のフレームは
    /// GPU 上の前フレーム行列がまだ 1 フレーム古いので、そこは通常の更新を通す
    /// （`MERGE_SKIP_STABLE_FRAMES` と同じ理屈）。
    PoseOnly,
    /// 「LOD を適用しない」フラグ列が変化した。
    DisableLod,
    /// 距離 LOD のバケット割り当てが変化した（カメラ移動など）。
    Lod,
    /// 速度バッファのリセット要求フレーム（Play⇄Edit 切替・シーンロード・RT リサイズ）。
    VelocityReset,
    /// 入力は一致したが、速度バッファ整定のための 1 回目なので更新した。
    Settling,
}

impl MergeGateReason {
    /// このフレームは `update()` を省いてよいか。
    pub fn is_skip(self) -> bool { matches!(self, MergeGateReason::Skipped) }

    /// 再生指定だけのアップロード（`update_poses_only`）で足りるか。
    pub fn is_pose_only(self) -> bool { matches!(self, MergeGateReason::PoseOnly) }

    /// 集計・JSON 出力用の安定した識別子（列挙子名と一致させる）。
    pub fn as_str(self) -> &'static str {
        match self {
            MergeGateReason::Skipped       => "Skipped",
            MergeGateReason::FirstFrame    => "FirstFrame",
            MergeGateReason::Mats          => "Mats",
            MergeGateReason::AbsIds        => "AbsIds",
            MergeGateReason::Tags          => "Tags",
            MergeGateReason::Pose          => "Pose",
            MergeGateReason::PoseOnly      => "PoseOnly",
            MergeGateReason::DisableLod    => "DisableLod",
            MergeGateReason::Lod           => "Lod",
            MergeGateReason::VelocityReset => "VelocityReset",
            MergeGateReason::Settling      => "Settling",
        }
    }
}

/// 統合バッチ 1 件について、`InstancedModelBatch::update()` の出力を決める CPU 入力一式（借用）。
///
/// `update()` の出力（ワールド行列キャッシュ・LOD 別ノードバッファ・ID バッファ・
/// スキンのアニメ時刻）を決めるのはこの 4 つと「カメラ位置による LOD 振り分け結果」だけ。
/// 後者は行列ではなく振り分け結果そのものを別途比較するため（`MergeBatchGate::decide` の
/// `lod_buckets_unchanged` 引数）、ここには含めない。
///
/// 毎フレーム全バッチぶん作られるため**借用のみ**で持つ（定常時のヒープ確保をゼロにする）。
#[derive(Clone, Copy, Debug)]
pub(crate) struct MergeBatchInputs<'a> {
    /// 統合インスタンスのルート変換行列列（`MergeInfo::mats`）。
    pub mats: &'a [[[f32; 4]; 4]],
    /// 統合インスタンス i の絶対 ID（`MergeInfo::abs_ids`）。ピッキング ID バッファの入力。
    pub abs_ids: &'a [u32],
    /// 統合インスタンス i のセマンティックタグ（`MergeInfo::render_tags`）。
    pub render_tags: &'a [u8],
    /// 統合インスタンス i の Animator 駆動再生指定（`MergeInfo::pose_overrides`）。
    /// スキンの再生指定アップロードの入力。**クロスフェードの weight やフェード元まで
    /// 含む**ので、行列が静止したままブレンドだけが進むフレームでもスキップに入らない。
    pub pose_overrides: &'a [Option<SkinAnimPose>],
    /// 統合インスタンス i の「LOD を適用しない」フラグ（`MergeInfo::disable_lods`）。
    /// LOD 振り分けの入力そのものなので、行列が静止したままこのフラグだけが変わった
    /// フレーム（インスペクタのチェック操作）でもスキップに入らないようにする。
    pub disable_lods: &'a [bool],
}

/// 直近フレームの入力を保持するスナップショット（所有）。
///
/// 比較は各列の**完全一致（ビット一致相当）**。f32 の NaN は自分自身と等しくならないため、
/// NaN を含む行列は常に「変化した」と判定されて再計算に落ちる（安全側）。
#[derive(Clone, Debug, Default, PartialEq)]
struct MergeBatchSnapshot {
    mats:           Vec<[[f32; 4]; 4]>,
    abs_ids:        Vec<u32>,
    render_tags:    Vec<u8>,
    pose_overrides: Vec<Option<SkinAnimPose>>,
    disable_lods:   Vec<bool>,
}

impl MergeBatchSnapshot {
    /// このスナップショットが与えられた入力と完全一致するか。
    fn matches(&self, i: &MergeBatchInputs<'_>) -> bool {
        self.first_mismatch(i).is_none()
    }

    /// 最初に食い違ったフィールドを返す（全一致なら `None`）。
    ///
    /// 一致判定そのものは `matches` と同じだが、「なぜ再計算に落ちたか」を
    /// 診断できるよう**どの列が違ったか**まで返す。比較順は
    /// 「変化の主因になりやすい順（行列 → ID → タグ → ポーズ → LOD 無効）」で、
    /// 複数が同時に変わったフレームは先頭のものが理由として記録される。
    fn first_mismatch(&self, i: &MergeBatchInputs<'_>) -> Option<MergeGateReason> {
        if self.mats.as_slice()           != i.mats           { return Some(MergeGateReason::Mats); }
        if self.abs_ids.as_slice()        != i.abs_ids        { return Some(MergeGateReason::AbsIds); }
        if self.render_tags.as_slice()    != i.render_tags    { return Some(MergeGateReason::Tags); }
        if self.pose_overrides.as_slice() != i.pose_overrides { return Some(MergeGateReason::Pose); }
        if self.disable_lods.as_slice()   != i.disable_lods   { return Some(MergeGateReason::DisableLod); }
        None
    }

    /// 与えられた入力でスナップショットを取り直す（確保は「変化したフレーム」だけ）。
    fn store(&mut self, i: &MergeBatchInputs<'_>) {
        self.mats.clear();           self.mats.extend_from_slice(i.mats);
        self.abs_ids.clear();        self.abs_ids.extend_from_slice(i.abs_ids);
        self.render_tags.clear();    self.render_tags.extend_from_slice(i.render_tags);
        self.pose_overrides.clear(); self.pose_overrides.extend_from_slice(i.pose_overrides);
        self.disable_lods.clear();   self.disable_lods.extend_from_slice(i.disable_lods);
    }
}

/// 統合バッチ 1 件ぶんのダーティゲート状態（フレームをまたいで保持する）。
#[derive(Clone, Debug, Default)]
pub(crate) struct MergeBatchGate {
    /// 直近フレームの入力スナップショット。未取得（初回・バッチ再生成直後）は None。
    last: Option<MergeBatchSnapshot>,
    /// 入力が連続一致したフレーム数（一致するたびに +1、変化で 0 へ戻る）。
    stable_frames: u32,
    /// **重い入力だけ**が連続一致したフレーム数。
    ///
    /// 重い入力＝行列・絶対 ID・タグ・LOD 無効フラグ・LOD バケット割り当て・強制更新。
    /// 再生指定（pose_overrides）だけが動くフレームでも切れずに伸びるので、
    /// アニメ再生中でも「行列は止まっている」ことを検出できる。
    heavy_stable_frames: u32,
}

impl MergeBatchGate {
    /// このフレームの `update()` をスキップしてよいかを判定し、内部状態を更新する。
    ///
    /// - `inputs`:              このフレームの入力（借用）。
    /// - `lod_buckets_unchanged`: 現在のカメラ位置で距離 LOD を振り直しても、前回 `update()` が
    ///                          確定させた LOD バケット割り当てと完全一致するか。
    ///                          （カメラ移動そのものではなく「振り分け結果が変わったか」で見る。
    ///                          カメラは毎フレーム微動しうるが、バケットが変わらない限り
    ///                          `update()` の出力は同一になるため）
    /// - `force_update`:        速度リセット要求など、無条件で再計算すべきフレームか。
    ///
    /// 戻り値 = このフレームの判定理由。`MergeGateReason::is_skip()` が `true` の
    /// ときだけ `update()` を省いてよい（それ以外はすべて「更新した理由」）。
    pub fn decide(
        &mut self,
        inputs: &MergeBatchInputs<'_>,
        lod_buckets_unchanged: bool,
        force_update: bool,
    ) -> MergeGateReason {
        // どの列が食い違ったか（＝再計算の直接原因）をまず確定させる。
        // スナップショット未取得は「初回フレーム」として区別する。
        let mismatch = match self.last.as_ref() {
            None    => Some(MergeGateReason::FirstFrame),
            Some(s) => s.first_mismatch(inputs),
        };
        // 「重い入力」が動いたか。＝全更新（ワールド行列再計算・全ノードバッファ書込・
        // ID バッファ書込）をやり直さないと出力が合わなくなる変化。
        // 再生指定（Pose）だけはスキンのアップロードにしか効かないので、ここには含めない。
        let heavy_changed = force_update
            || !lod_buckets_unchanged
            || matches!(
                mismatch,
                Some(MergeGateReason::FirstFrame)
                    | Some(MergeGateReason::Mats)
                    | Some(MergeGateReason::AbsIds)
                    | Some(MergeGateReason::Tags)
                    | Some(MergeGateReason::DisableLod)
            );

        if heavy_changed {
            if mismatch.is_some() {
                // スナップショットの詰め直しは「変化したフレーム」だけ（定常時は確保ゼロ）。
                self.last.get_or_insert_with(MergeBatchSnapshot::default).store(inputs);
            }
            self.stable_frames       = 0;
            self.heavy_stable_frames = 0;
            // 理由は「全更新を強制した原因」を記録する。
            // 重い列（行列・ID・タグ・LOD 無効・初回）の変化があればそれを、
            // 無ければ強制更新 → LOD 振り分け変化の順。
            // 再生指定（Pose）だけが動いていても、全更新を強制したのは Pose ではないので
            // ここでは採らない（軽量経路に落とせなかった真の理由を残すため）。
            return match mismatch {
                Some(m) if m != MergeGateReason::Pose => m,
                _ if force_update                     => MergeGateReason::VelocityReset,
                _                                     => MergeGateReason::Lod,
            };
        }

        // ここから先、重い入力は 1 ビットも動いていない。
        self.heavy_stable_frames = self.heavy_stable_frames.saturating_add(1);

        if mismatch == Some(MergeGateReason::Pose) {
            // 再生指定だけが進んだフレーム。スナップショットへ取り込み、
            // 速度バッファが整定済みなら軽量アップロードに落とす。
            self.last.get_or_insert_with(MergeBatchSnapshot::default).store(inputs);
            self.stable_frames = 0;
            return if self.heavy_stable_frames >= MERGE_SKIP_STABLE_FRAMES {
                MergeGateReason::PoseOnly
            } else {
                MergeGateReason::Pose
            };
        }

        // 連続一致数を伸ばし、速度バッファ整定ぶんを超えたらスキップする。
        self.stable_frames = self.stable_frames.saturating_add(1);
        if self.stable_frames >= MERGE_SKIP_STABLE_FRAMES {
            MergeGateReason::Skipped
        } else {
            MergeGateReason::Settling
        }
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の最小入力（1 インスタンス）の実体。`MergeBatchInputs` はこれを借用する。
    struct Fixture {
        mats:           Vec<[[f32; 4]; 4]>,
        abs_ids:        Vec<u32>,
        render_tags:    Vec<u8>,
        pose_overrides: Vec<Option<SkinAnimPose>>,
        disable_lods:   Vec<bool>,
    }

    impl Fixture {
        fn new(x: f32, id: u32, tag: u8, t: Option<SkinAnimPose>) -> Self {
            Self::with_disable_lod(x, id, tag, t, false)
        }
        fn with_disable_lod(
            x: f32, id: u32, tag: u8, t: Option<SkinAnimPose>, disable_lod: bool,
        ) -> Self {
            let mut m = [[0.0f32; 4]; 4];
            m[3][0] = x;
            Self {
                mats:           vec![m],
                abs_ids:        vec![id],
                render_tags:    vec![tag],
                pose_overrides: vec![t],
                disable_lods:   vec![disable_lod],
            }
        }
        fn inputs(&self) -> MergeBatchInputs<'_> {
            MergeBatchInputs {
                mats:           &self.mats,
                abs_ids:        &self.abs_ids,
                render_tags:    &self.render_tags,
                pose_overrides: &self.pose_overrides,
                disable_lods:   &self.disable_lods,
            }
        }
    }

    /// テスト用の最小入力を作る短縮形。
    fn sig_of(x: f32, id: u32, tag: u8, t: Option<SkinAnimPose>) -> Fixture {
        Fixture::new(x, id, tag, t)
    }

    /// 「LOD を適用しない」フラグだけが変化しても再計算へ倒れること。
    /// （インスペクタのチェック操作は行列・ID・タグ・アニメ時刻を一切変えないため、
    ///   ゲートがこのフラグを見ていないと ON/OFF が画面に反映されない）
    #[test]
    fn changed_disable_lod_alone_forces_update() {
        let mut gate = MergeBatchGate::default();
        let base = Fixture::with_disable_lod(1.0, 0, 0, None, false);
        for _ in 0..3 { gate.decide(&base.inputs(), true, false); }
        assert!(gate.decide(&base.inputs(), true, false).is_skip(), "前提: スキップ状態");
        let toggled = Fixture::with_disable_lod(1.0, 0, 0, None, true);
        assert!(!gate.decide(&toggled.inputs(), true, false).is_skip(), "LOD 無効フラグの変化で再計算");
    }

    /// 初回フレームは必ず更新する（スナップショット未取得のため）。
    #[test]
    fn first_frame_always_updates() {
        let mut gate = MergeBatchGate::default();
        assert!(!gate.decide(&sig_of(1.0, 0, 0, None).inputs(), true, false).is_skip());
    }

    /// 入力不変: 一致 1 回目は速度バッファ整定のため更新し、2 回目以降スキップする。
    #[test]
    fn unchanged_input_skips_after_settling_frame() {
        let mut gate = MergeBatchGate::default();
        let s = sig_of(1.0, 0, 0, None);
        assert!(!gate.decide(&s.inputs(), true, false).is_skip(), "初回は更新");
        assert!(!gate.decide(&s.inputs(), true, false).is_skip(), "一致1回目は速度整定のため更新");
        assert!(gate.decide(&s.inputs(), true, false).is_skip(), "一致2回目からスキップ");
        assert!(gate.decide(&s.inputs(), true, false).is_skip(), "以降も継続してスキップ");
    }

    /// 行列が変化したら即座に再計算へ戻る。
    #[test]
    fn changed_matrix_forces_update() {
        let mut gate = MergeBatchGate::default();
        let a = sig_of(1.0, 0, 0, None);
        for _ in 0..4 { gate.decide(&a.inputs(), true, false); }
        assert!(gate.decide(&a.inputs(), true, false).is_skip(), "前提: スキップ状態に入っている");
        let b = sig_of(2.0, 0, 0, None);
        assert!(!gate.decide(&b.inputs(), true, false).is_skip(), "行列変化で再計算");
        assert!(!gate.decide(&b.inputs(), true, false).is_skip(), "変化直後の一致1回目は整定のため更新");
        assert!(gate.decide(&b.inputs(), true, false).is_skip(), "整定後は再びスキップ");
    }

    /// 絶対 ID・タグ・アニメ時刻の変化も、それぞれ単独で再計算を起こす。
    #[test]
    fn changed_ids_tags_or_anim_time_force_update() {
        for changed in [
            sig_of(1.0, 9, 0, None),          // abs_ids が変化（ピッキング ID バッファが陳腐化）
            sig_of(1.0, 0, 3, None),          // render_tags が変化（ワールド行列キャッシュへ焼き込む値）
            sig_of(1.0, 0, 0, Some(SkinAnimPose::single(0, 0.5))),     // アニメ権威時刻が変化（スキン時刻アップロード）
        ] {
            let mut gate = MergeBatchGate::default();
            let base = sig_of(1.0, 0, 0, None);
            for _ in 0..3 { gate.decide(&base.inputs(), true, false); }
            assert!(gate.decide(&base.inputs(), true, false).is_skip(), "前提: スキップ状態");
            assert!(!gate.decide(&changed.inputs(), true, false).is_skip(), "入力変化で再計算");
        }
    }

    /// クロスフェード中は「行列も時刻も同じで weight だけが進む」フレームがある。
    /// この差分を取りこぼすとブレンドが GPU へ上がらず、混合途中のポーズで固まる。
    #[test]
    fn changed_blend_weight_alone_forces_update() {
        let pose_a = SkinAnimPose { anim_a: 0, time_a: 0.5, anim_b: 1, time_b: 0.25, weight: 0.3 };
        let mut pose_b = pose_a;
        pose_b.weight = 0.6; // weight だけが進んだフレーム

        let mut gate = MergeBatchGate::default();
        let base = sig_of(1.0, 0, 0, Some(pose_a));
        for _ in 0..3 { gate.decide(&base.inputs(), true, false); }
        assert!(gate.decide(&base.inputs(), true, false).is_skip(), "前提: スキップ状態");
        assert!(
            !gate.decide(&sig_of(1.0, 0, 0, Some(pose_b)).inputs(), true, false).is_skip(),
            "weight だけの変化でも再計算へ倒れる"
        );
    }

    /// LOD バケット割り当てが変わったフレームはスキップしない（カメラ移動で LOD が切り替わる場合）。
    #[test]
    fn lod_bucket_change_forces_update() {
        let mut gate = MergeBatchGate::default();
        let s = sig_of(1.0, 0, 0, None);
        for _ in 0..3 { gate.decide(&s.inputs(), true, false); }
        assert!(gate.decide(&s.inputs(), true, false).is_skip(), "前提: スキップ状態");
        assert!(!gate.decide(&s.inputs(), false, false).is_skip(), "LOD 振り分けが変われば再計算");
        assert!(!gate.decide(&s.inputs(), true, false).is_skip(), "整定フレームは更新");
        assert!(gate.decide(&s.inputs(), true, false).is_skip(), "その後スキップへ復帰");
    }

    /// 速度リセット要求フレームは入力不変でも必ず更新する。
    #[test]
    fn force_update_overrides_stable_input() {
        let mut gate = MergeBatchGate::default();
        let s = sig_of(1.0, 0, 0, None);
        for _ in 0..3 { gate.decide(&s.inputs(), true, false); }
        assert!(gate.decide(&s.inputs(), true, false).is_skip(), "前提: スキップ状態");
        assert!(!gate.decide(&s.inputs(), true, true).is_skip(), "強制更新フレームはスキップしない");
    }

    /// NaN を含む行列は常に「変化した」と判定され、スキップに入らない（安全側）。
    #[test]
    fn nan_matrix_never_skips() {
        let mut gate = MergeBatchGate::default();
        let s = sig_of(f32::NAN, 0, 0, None);
        for _ in 0..5 {
            assert!(!gate.decide(&s.inputs(), true, false).is_skip(), "NaN 入力はスキップしない");
        }
    }

    /// 判定理由がどの列の変化かまで分解されること（診断ダンプの根拠）。
    #[test]
    fn reports_specific_reason_per_changed_field() {
        // 初回フレームは FirstFrame
        let mut gate = MergeBatchGate::default();
        let base = sig_of(1.0, 0, 0, None);
        assert_eq!(gate.decide(&base.inputs(), true, false), MergeGateReason::FirstFrame);
        // 一致 1 回目は Settling、2 回目から Skipped
        assert_eq!(gate.decide(&base.inputs(), true, false), MergeGateReason::Settling);
        assert_eq!(gate.decide(&base.inputs(), true, false), MergeGateReason::Skipped);
        // 行列の変化
        assert_eq!(gate.decide(&sig_of(2.0, 0, 0, None).inputs(), true, false), MergeGateReason::Mats);
        // 絶対 ID の変化
        assert_eq!(gate.decide(&sig_of(2.0, 7, 0, None).inputs(), true, false), MergeGateReason::AbsIds);
        // タグの変化
        assert_eq!(gate.decide(&sig_of(2.0, 7, 5, None).inputs(), true, false), MergeGateReason::Tags);
        // ポーズの変化
        assert_eq!(
            gate.decide(&sig_of(2.0, 7, 5, Some(SkinAnimPose::single(0, 0.25))).inputs(), true, false),
            MergeGateReason::Pose
        );
    }

    /// 入力不変のまま LOD／強制更新だけが立った場合の理由分類。
    #[test]
    fn reports_lod_and_velocity_reset_reasons() {
        let mut gate = MergeBatchGate::default();
        let s = sig_of(1.0, 0, 0, None);
        for _ in 0..3 { gate.decide(&s.inputs(), true, false); }
        assert_eq!(gate.decide(&s.inputs(), false, false), MergeGateReason::Lod);
        for _ in 0..3 { gate.decide(&s.inputs(), true, false); }
        assert_eq!(gate.decide(&s.inputs(), true, true), MergeGateReason::VelocityReset);
    }

    /// 「LOD を適用しない」フラグ単独の変化は DisableLod として記録される。
    #[test]
    fn reports_disable_lod_reason() {
        let mut gate = MergeBatchGate::default();
        let base = Fixture::with_disable_lod(1.0, 0, 0, None, false);
        for _ in 0..3 { gate.decide(&base.inputs(), true, false); }
        let toggled = Fixture::with_disable_lod(1.0, 0, 0, None, true);
        assert_eq!(gate.decide(&toggled.inputs(), true, false), MergeGateReason::DisableLod);
    }

    /// アニメ再生中（再生指定だけが毎フレーム進む）でも、行列が静止していれば
    /// 整定後は軽量経路（PoseOnly）へ落ちること。
    /// これが無いと「1 体でもアニメしていればバッチ全体が毎フレーム全更新」になる。
    #[test]
    fn pose_only_change_falls_back_to_light_path_after_settling() {
        let mut gate = MergeBatchGate::default();
        // 初回 → 整定
        let f0 = sig_of(1.0, 0, 0, Some(SkinAnimPose::single(0, 0.00)));
        assert_eq!(gate.decide(&f0.inputs(), true, false), MergeGateReason::FirstFrame);
        // 以降、行列は不変のまま再生時刻だけが毎フレーム進む。
        // 重い入力の整定（MERGE_SKIP_STABLE_FRAMES）までは通常の全更新。
        let f1 = sig_of(1.0, 0, 0, Some(SkinAnimPose::single(0, 0.01)));
        assert_eq!(gate.decide(&f1.inputs(), true, false), MergeGateReason::Pose);
        // 整定後は軽量経路へ
        for t in 2..6 {
            let f = sig_of(1.0, 0, 0, Some(SkinAnimPose::single(0, t as f32 * 0.01)));
            assert_eq!(gate.decide(&f.inputs(), true, false), MergeGateReason::PoseOnly,
                       "行列が静止していれば再生指定だけの更新に落ちる");
        }
        assert!(MergeGateReason::PoseOnly.is_pose_only());
        assert!(!MergeGateReason::PoseOnly.is_skip(), "PoseOnly はスキップではない（軽量更新）");
    }

    /// 行列が動いたフレームの直後は、再生指定だけが進んでいても通常の全更新へ戻る
    /// （速度バッファの前フレーム行列を整定させるため）。
    #[test]
    fn pose_only_requires_settled_velocity_buffer() {
        let mut gate = MergeBatchGate::default();
        let pose = |t: f32| Some(SkinAnimPose::single(0, t));
        // まず PoseOnly まで持っていく
        for t in 0..5 {
            gate.decide(&sig_of(1.0, 0, 0, pose(t as f32 * 0.01)).inputs(), true, false);
        }
        assert_eq!(
            gate.decide(&sig_of(1.0, 0, 0, pose(0.05)).inputs(), true, false),
            MergeGateReason::PoseOnly
        );
        // 行列が動いた → 全更新
        assert_eq!(
            gate.decide(&sig_of(2.0, 0, 0, pose(0.06)).inputs(), true, false),
            MergeGateReason::Mats
        );
        // その次のフレームは、再生指定だけの変化でも整定のため全更新
        assert_eq!(
            gate.decide(&sig_of(2.0, 0, 0, pose(0.07)).inputs(), true, false),
            MergeGateReason::Pose
        );
        // さらに次から軽量経路
        assert_eq!(
            gate.decide(&sig_of(2.0, 0, 0, pose(0.08)).inputs(), true, false),
            MergeGateReason::PoseOnly
        );
    }

    /// LOD バケットが動いたフレームは、再生指定だけの変化でも全更新へ倒れる。
    #[test]
    fn lod_change_overrides_pose_only() {
        let mut gate = MergeBatchGate::default();
        let pose = |t: f32| Some(SkinAnimPose::single(0, t));
        for t in 0..5 {
            gate.decide(&sig_of(1.0, 0, 0, pose(t as f32 * 0.01)).inputs(), true, false);
        }
        assert_eq!(
            gate.decide(&sig_of(1.0, 0, 0, pose(0.05)).inputs(), false, false),
            MergeGateReason::Lod
        );
    }
}
