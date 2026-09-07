// ============================================================
//  merge_collect.rs — 統合バッチ収集（フレーム間で使い回す集約バッファ）
//
//  》含む処理「
//  - MergeInfo:      同一 batch_key を持つ全 ModelComponent を 1 バッチへ統合した集約結果
//  - MergeCollector: その集約先を **フレームをまたいで保持**し、毎フレームの
//                    ヒープ確保・文字列確保をゼロに保つ収集器
//
//  【なぜ必要か — 旧実装の問題】
//  フレームループの「描画/統合バッチ更新」区間は、毎フレーム
//    ① `HashMap<String, MergeInfo>` を新規に作り、
//    ② ModelComponent ごとに `batch_key()`（String を新規確保）でキーを作り、
//    ③ 統合インスタンスごとに 5 本の Vec へ push する（＝毎フレーム再確保）
//  という構造だった。①〜③ はどれも「描画結果を決める計算」ではなく**入れ物を作り直す
//  コスト**であり、MC 数・インスタンス数に比例して純粋な無駄として積み上がる。
//
//  本モジュールは同じ集約結果を得たうえで、
//    - 集約先の HashMap と各 Vec を**フレーム間で再利用**（容量を保ったまま clear）
//    - キー文字列は呼び出し側の使い回しバッファへ書き込み、**新規キーのときだけ**確保
//  とすることで、定常状態のヒープ確保を実質ゼロにする。
//  集約の内容（どの MC がどのバッチのどの位置に載るか）は旧実装とビット単位で同一。
//
//  【代表 CPU モデルの「先勝ち」を守る】
//  統合バッチの CPU モデルは「そのフレームで最初に現れた MC のもの」でなければならない
//  （後段の `gpu_model_by_path` が同じ先勝ち規則で GpuModel を選ぶため。食い違うと
//  添字表と GPU データがずれて描画時にパニックする）。集約先を使い回すと前フレームの
//  代表が残ってしまうので、`live` フラグでフレーム初出を判定し、そこで採り直す。
// ============================================================

use std::collections::HashMap;
use std::sync::Arc;

use crate::engine::core::loader::model::Model;
use crate::engine::core::renderer::skin_system::SkinAnimPose;

/// 同一 batch_key を持つ全 ModelComponent を 1 バッチへ統合するための集約先。
///
/// 各 Vec は「統合インスタンス順」で並ぶ（同じ添字が同じインスタンスを指す）。
pub(crate) struct MergeInfo {
    /// このバッチの代表 CPU モデル（そのフレームで最初に現れた MC のもの）。
    pub cpu_model: Arc<Model>,
    /// 統合インスタンス i のルート変換行列（描画オフセット合成済み）。
    pub mats: Vec<[[f32; 4]; 4]>,
    /// 統合インスタンス i の Animator 駆動再生指定（None = 静止・先頭フレーム凍結）。
    /// `ModelComponent::anim_drive` 由来で、アニメ index・時刻に加えて
    /// クロスフェードのフェード元と weight まで含む。
    /// 同一 MC の全インスタンスに同じ値を複製する。
    pub pose_overrides: Vec<Option<SkinAnimPose>>,
    /// 統合インスタンス i の絶対 ID（元 MC の id_base + 元インスタンス idx）。
    pub abs_ids: Vec<u32>,
    /// 統合インスタンス i のセマンティックタグ（0..15。0 = タグ無し）。
    /// `ModelComponent::render_tag` 由来で、同一 MC の全インスタンスへ同じ値を複製する
    /// （タグはアクタ単位の属性であり、インスタンス個別には持たせない）。
    pub render_tags: Vec<u8>,
    /// 統合インスタンス i の「LOD を適用しない」フラグ。
    /// `ModelComponent::disable_lod` 由来で、同一 MC の全インスタンスへ同じ値を複製する。
    pub disable_lods: Vec<bool>,
    /// このフレームに 1 度でも参照されたか（＝このフレームの描画対象か）。
    ///
    /// フレーム冒頭で全エントリ false に落とし、初出で true に立てる。
    /// 収集後に false のエントリは「もう描かれていない batch_key」として捨てる
    /// （旧実装が毎フレーム HashMap を作り直していたのと同じ結果になる）。
    pub live: bool,
}

impl MergeInfo {
    /// 空の集約先を作る（新しい batch_key が現れたときだけ呼ばれる）。
    fn new(cpu_model: Arc<Model>) -> Self {
        Self {
            cpu_model,
            mats:           Vec::new(),
            pose_overrides: Vec::new(),
            abs_ids:        Vec::new(),
            render_tags:    Vec::new(),
            disable_lods:   Vec::new(),
            live:           true,
        }
    }

    /// 中身だけ捨てて確保済み容量は残す（フレーム冒頭のリセット）。
    fn reset_keep_capacity(&mut self) {
        self.mats.clear();
        self.pose_overrides.clear();
        self.abs_ids.clear();
        self.render_tags.clear();
        self.disable_lods.clear();
        self.live = false;
    }

    /// このバッチの統合インスタンス数。
    pub fn len(&self) -> usize { self.mats.len() }
}

/// batch_key → 集約結果 の表を、フレームをまたいで保持する収集器。
#[derive(Default)]
pub(crate) struct MergeCollector {
    /// batch_key → 集約結果。容量はフレーム間で使い回す。
    map: HashMap<String, MergeInfo>,
}

impl MergeCollector {
    /// フレーム冒頭のリセット。全エントリを「未出現・空」にするが、確保は解放しない。
    pub fn begin_frame(&mut self) {
        for info in self.map.values_mut() {
            info.reset_keep_capacity();
        }
    }

    /// 指定 batch_key の集約先を取得する（無ければ作る）。
    ///
    /// `cpu_model` は「このフレームでこの batch_key に最初に現れた MC の CPU モデル」。
    /// フレーム初出のときだけ代表として採用する（＝先勝ち。2 回目以降は無視する）。
    pub fn entry_for(&mut self, key: &str, cpu_model: &Arc<Model>) -> &mut MergeInfo {
        // 【2 回引く理由】`HashMap::entry` は所有キー（String）を要求するため、
        // ヒット時にも毎回 String を確保することになる。`contains_key` → `get_mut` なら
        // 確保が起きるのは「新しい batch_key が現れたフレーム」だけで済む。
        // ハッシュ計算 2 回のコストは、確保・解放 1 組より安い。
        if !self.map.contains_key(key) {
            self.map.insert(key.to_string(), MergeInfo::new(Arc::clone(cpu_model)));
        }
        let info = self.map.get_mut(key).expect("直前に挿入したので必ず存在する");
        if !info.live {
            // このフレームの初出。代表 CPU モデルを採り直す（前フレームの代表を持ち越さない）。
            info.live      = true;
            info.cpu_model = Arc::clone(cpu_model);
        }
        info
    }

    /// 収集後に、このフレーム未出現のエントリを捨てる。
    ///
    /// 以降 `iter()` が返すのは「このフレームの描画対象 batch_key」だけになり、
    /// 旧実装（毎フレーム作り直した HashMap）と同じ集合になる。
    pub fn finish_frame(&mut self) {
        self.map.retain(|_, info| info.live);
    }

    /// このフレームの (batch_key, 集約結果) を走査する。
    pub fn iter(&self) -> impl Iterator<Item = (&String, &MergeInfo)> {
        self.map.iter()
    }

    /// このフレームの batch_key を走査する（stale prune の alive 集合用）。
    pub fn keys(&self) -> impl Iterator<Item = &String> {
        self.map.keys()
    }

    /// このフレームのバッチ数。
    pub fn len(&self) -> usize { self.map.len() }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の空 CPU モデル。中身は集約ロジックに関与しないので全フィールド空でよい。
    fn dummy_model() -> Arc<Model> {
        Arc::new(Model {
            name:       String::new(),
            nodes:      Vec::new(),
            root_nodes: Vec::new(),
            meshes:     Vec::new(),
            materials:  Vec::new(),
            textures:   Vec::new(),
            animations: Vec::new(),
            skins:      Vec::new(),
        })
    }

    /// 集約先はフレームをまたいで使い回されるが、内容は毎フレーム作り直したのと同じになる。
    #[test]
    fn reuses_entries_but_clears_content_each_frame() {
        let mut c = MergeCollector::default();
        let m = dummy_model();

        c.begin_frame();
        c.entry_for("a", &m).mats.push([[1.0; 4]; 4]);
        c.finish_frame();
        assert_eq!(c.len(), 1);
        assert_eq!(c.iter().next().unwrap().1.len(), 1);

        // 次フレーム: 同じキーへ 2 本積む → 前フレームぶんが残らないこと。
        c.begin_frame();
        {
            let e = c.entry_for("a", &m);
            e.mats.push([[2.0; 4]; 4]);
            e.mats.push([[3.0; 4]; 4]);
        }
        c.finish_frame();
        assert_eq!(c.iter().next().unwrap().1.len(), 2, "前フレームの内容が残ってはいけない");
    }

    /// このフレームに現れなかった batch_key は捨てられる（＝毎フレーム作り直しと同じ集合）。
    #[test]
    fn drops_keys_absent_this_frame() {
        let mut c = MergeCollector::default();
        let m = dummy_model();

        c.begin_frame();
        c.entry_for("a", &m);
        c.entry_for("b", &m);
        c.finish_frame();
        assert_eq!(c.len(), 2);

        c.begin_frame();
        c.entry_for("a", &m);
        c.finish_frame();
        let keys: Vec<&String> = c.keys().collect();
        assert_eq!(keys.len(), 1);
        assert_eq!(keys[0], "a");
    }

    /// 代表 CPU モデルは「そのフレームの初出 MC」のもの（先勝ち）で、
    /// 2 回目以降の呼び出しでは差し替わらない。
    #[test]
    fn representative_model_is_first_of_the_frame() {
        let mut c = MergeCollector::default();
        let first  = dummy_model();
        let second = dummy_model();

        c.begin_frame();
        c.entry_for("a", &first);
        c.entry_for("a", &second);
        c.finish_frame();
        let rep = Arc::clone(&c.iter().next().unwrap().1.cpu_model);
        assert!(Arc::ptr_eq(&rep, &first), "同一フレーム内は先勝ち");

        // 次フレームで初出が変われば代表も採り直される（前フレームの代表を持ち越さない）。
        c.begin_frame();
        c.entry_for("a", &second);
        c.finish_frame();
        let rep = Arc::clone(&c.iter().next().unwrap().1.cpu_model);
        assert!(Arc::ptr_eq(&rep, &second), "フレームが変わったら初出の MC が代表になる");
    }

    /// インスタンスを 1 本も積まないエントリ（非表示 MC）も「このフレームの描画対象」として残る。
    /// これが消えると、統合バッチが更新されないまま前フレームの姿で描かれ続ける。
    #[test]
    fn zero_instance_entry_survives_the_frame() {
        let mut c = MergeCollector::default();
        let m = dummy_model();
        c.begin_frame();
        c.entry_for("hidden", &m); // mats を積まない = 非表示 MC のケース
        c.finish_frame();
        assert_eq!(c.len(), 1);
        assert_eq!(c.iter().next().unwrap().1.len(), 0);
    }
}
