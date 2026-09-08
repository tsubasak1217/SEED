// ============================================================
//  ui_draw_order.rs — UI 描画順（ゾーン → レイヤー → 種別）の統合ロジック
//
//  【解決した不具合】
//  以前は「スプライト」「プリミティブ」「テキスト」を **種別ごとに別リスト**として
//  ソートし、ゾーンごとに「全スプライト → 全プリミティブ → 全テキスト」の順で
//  描画していた。そのため `layer` は同一種別の中でしか効かず、
//  「layer 1002 のテキストが layer 2002 のスプライトより手前に出る」という
//  レイヤー指定を無視した重なりが起きていた（例: ポーズメニューのボタンの上に
//  チュートリアル吹き出しの文字が乗る）。
//
//  【本モジュールの役割】
//  レイヤー昇順にソート済みの 4 リスト（スプライト／プリミティブ／2D パーティクル／
//  テキスト）を 1 本の描画列（ラン列）へマージする
//  **純関数**を提供する。GPU にも wgpu にも依存しないため単体テストできる。
//
//  ラン（`UiDrawRun`）= 「同一種別が連続する区間」。隣接する同種別アイテムは
//  1 ランへ融合されるため、レイヤーの交互出現が無い UI では従来と同じ
//  ドローコール数で済む（最悪ケースは下記 `merge_ui_draw_runs` の説明を参照）。
// ============================================================

// ─── 種別 ────────────────────────────────────────────────────

/// UI 描画アイテムの種別。同一レイヤー内の前後関係（奥 → 手前）を決める。
///
/// docs/scripting_api.md §7.8 の規約
/// 「zone → layer → 同一 layer 内はスプライト → プリミティブ → パーティクル → テキスト」
/// の「種別」部分がこの列挙型で、宣言順がそのまま描画順（奥 → 手前）になる。
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
pub enum UiDrawKind {
    /// スプライト（SpriteComponent / SkinnedSpriteComponent）。最も奥。
    Sprite,
    /// スクリプト 2D プリミティブ（`SEED.Draw`）。
    Primitive,
    /// 2D パーティクル（CanvasTransform を持つアクターの ParticleEmitterComponent）。
    ///
    /// テキストより奥に置くのは「文字は常に読めるべき」という UI の原則による
    /// （同一レイヤーに置いた場合の既定。手前に出したいときは layer を上げる）。
    Particle,
    /// テキスト（TextComponent）。同一レイヤー内で最も手前。
    Text,
}

impl UiDrawKind {
    /// 同一レイヤー内のソートキー（小さいほど奥）。
    ///
    /// マジックナンバーを避けるため、比較は必ずこの関数を通す。
    pub fn draw_order(self) -> u8 {
        match self {
            UiDrawKind::Sprite => 0,
            UiDrawKind::Primitive => 1,
            UiDrawKind::Particle => 2,
            UiDrawKind::Text => 3,
        }
    }
}

/// マージ時に走査する種別の一覧（描画順の昇順）。
/// 種別を増やすときはここと `UiDrawKind::draw_order` の 2 箇所だけを直す。
const KIND_SCAN_ORDER: [UiDrawKind; 4] = [
    UiDrawKind::Sprite,
    UiDrawKind::Primitive,
    UiDrawKind::Particle,
    UiDrawKind::Text,
];

// ─── ラン ────────────────────────────────────────────────────

/// 「同一種別が連続する 1 区間」= 1 回のパイプライン切り替えで描ける単位。
///
/// `start..end` は **その種別自身のリスト**に対する半開区間である
/// （スプライトのランならスプライトリストの添字）。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct UiDrawRun {
    /// このランの種別。
    pub kind: UiDrawKind,
    /// 区間の開始添字（その種別のリスト内）。
    pub start: usize,
    /// 区間の終了添字（排他）。
    pub end: usize,
}

impl UiDrawRun {
    /// 区間に含まれるアイテム数。
    pub fn len(&self) -> usize {
        self.end - self.start
    }

    /// 空区間か（マージ結果には空ランは含まれないので通常 false）。
    pub fn is_empty(&self) -> bool {
        self.start >= self.end
    }
}

// ─── マージ本体 ──────────────────────────────────────────────

/// レイヤー昇順にソート済みの 4 リストを、1 本の描画ラン列へマージする。
///
/// # 引数
/// 各引数は「そのリストのアイテムのレイヤー値」を**昇順（安定ソート済み）**に
/// 並べたもの。値そのものは不要で、順序決定に必要なレイヤーだけを受け取る
/// （GPU 型に依存させないための設計）。
///
/// # 順序規則
/// `(layer 昇順, 種別 昇順)` の全順序で全アイテムを 1 列に並べ、
/// 同一レイヤー・同一種別の中は入力順（＝ヒエラルキー DFS 順）を保つ。
///
/// # 性能
/// 隣接する同種別アイテムは 1 ランへ融合するため、
/// - レイヤーの交互出現が無い UI（従来どおりの使い方）: 種別数ぶん＝最大 4 ラン。
/// - 最悪ケース: レイヤーと種別が 1 アイテムごとに入れ替わる UI で
///   ラン数 = アイテム総数（＝ 1 アイテム 1 ドローコール）。
///   これは「意図的に交互のレイヤーを振った」場合のみ発生する。
pub fn merge_ui_draw_runs(
    sprite_layers: &[i32],
    primitive_layers: &[i32],
    particle_layers: &[i32],
    text_layers: &[i32],
) -> Vec<UiDrawRun> {
    // 種別ごとの入力リストと走査カーソル（KIND_SCAN_ORDER と同じ並び）。
    let lists: [&[i32]; KIND_SCAN_ORDER.len()] =
        [sprite_layers, primitive_layers, particle_layers, text_layers];
    let mut cursors = [0usize; KIND_SCAN_ORDER.len()];
    let mut runs: Vec<UiDrawRun> = Vec::new();

    loop {
        // 未消費の先頭のうち (layer, 種別順) が最小のものを選ぶ。
        let mut best: Option<usize> = None;
        for (i, list) in lists.iter().enumerate() {
            let Some(&layer) = list.get(cursors[i]) else {
                continue;
            };
            match best {
                // 同一レイヤーのときは種別順（KIND_SCAN_ORDER の昇順 = i の昇順）が
                // 若いほうを先に出す。走査順が i 昇順なので `<` 比較だけで足りる。
                Some(b) if lists[b][cursors[b]] <= layer => {}
                _ => best = Some(i),
            }
        }
        let Some(i) = best else { break };

        // 直前のランと同種別なら融合、そうでなければ新しいランを開始する。
        let kind = KIND_SCAN_ORDER[i];
        match runs.last_mut() {
            Some(r) if r.kind == kind => r.end += 1,
            _ => runs.push(UiDrawRun {
                kind,
                start: cursors[i],
                end: cursors[i] + 1,
            }),
        }
        cursors[i] += 1;
    }

    runs
}

// ─── 単体テスト ──────────────────────────────────────────────

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト可読性のための短縮コンストラクタ。
    fn run(kind: UiDrawKind, start: usize, end: usize) -> UiDrawRun {
        UiDrawRun { kind, start, end }
    }

    /// 4 リストとも空 → ランは 1 本も出ない。
    #[test]
    fn empty_lists_produce_no_runs() {
        assert!(merge_ui_draw_runs(&[], &[], &[], &[]).is_empty());
    }

    /// 1 種別だけのときは全件が 1 ランへ融合される（従来と同じドローコール数）。
    #[test]
    fn single_kind_is_one_run() {
        let runs = merge_ui_draw_runs(&[0, 5, 10], &[], &[], &[]);
        assert_eq!(runs, vec![run(UiDrawKind::Sprite, 0, 3)]);

        let runs = merge_ui_draw_runs(&[], &[], &[], &[1, 2]);
        assert_eq!(runs, vec![run(UiDrawKind::Text, 0, 2)]);

        // パーティクルだけのときも同様に 1 ランへ融合される。
        let runs = merge_ui_draw_runs(&[], &[], &[3, 3, 4], &[]);
        assert_eq!(runs, vec![run(UiDrawKind::Particle, 0, 3)]);
    }

    /// 同一レイヤーではスプライト → プリミティブ → パーティクル → テキストの順になる。
    #[test]
    fn same_layer_orders_by_kind() {
        let runs = merge_ui_draw_runs(&[7], &[7], &[7], &[7]);
        assert_eq!(
            runs,
            vec![
                run(UiDrawKind::Sprite, 0, 1),
                run(UiDrawKind::Primitive, 0, 1),
                run(UiDrawKind::Particle, 0, 1),
                run(UiDrawKind::Text, 0, 1),
            ]
        );
    }

    /// 種別の描画順キーは宣言順（奥 → 手前）と一致する。
    /// パーティクルはプリミティブより手前・テキストより奥。
    #[test]
    fn draw_order_keys_are_monotonic() {
        assert!(UiDrawKind::Sprite.draw_order() < UiDrawKind::Primitive.draw_order());
        assert!(UiDrawKind::Primitive.draw_order() < UiDrawKind::Particle.draw_order());
        assert!(UiDrawKind::Particle.draw_order() < UiDrawKind::Text.draw_order());
    }

    /// 2D パーティクルは layer でスプライトと交互に並ぶ（種別内で閉じない）。
    ///
    /// レイヤー 0 の背景スプライト → レイヤー 10 のパーティクル →
    /// レイヤー 20 の前景スプライトの順（＝パーティクルが 2 枚のスプライトに挟まる）。
    #[test]
    fn particles_interleave_with_sprites_by_layer() {
        let runs = merge_ui_draw_runs(&[0, 20], &[], &[10], &[]);
        assert_eq!(
            runs,
            vec![
                run(UiDrawKind::Sprite, 0, 1),   // layer 0
                run(UiDrawKind::Particle, 0, 1), // layer 10
                run(UiDrawKind::Sprite, 1, 2),   // layer 20（パーティクルより手前）
            ]
        );
    }

    /// 本件の不具合の再現ケース:
    /// layer 1002 のテキストは layer 2002 のスプライトより **奥**へ並ぶ。
    #[test]
    fn text_below_higher_layer_sprite() {
        // スプライト: 1000（吹き出し背景）, 2002（ポーズボタン）
        // テキスト  : 1002（吹き出しの文字）
        let runs = merge_ui_draw_runs(&[1000, 2002], &[], &[], &[1002]);
        assert_eq!(
            runs,
            vec![
                run(UiDrawKind::Sprite, 0, 1), // layer 1000
                run(UiDrawKind::Text, 0, 1),   // layer 1002
                run(UiDrawKind::Sprite, 1, 2), // layer 2002（テキストより手前）
            ]
        );
    }

    /// レイヤーが交互に出るとランが分かれ、同レイヤー連続は融合される。
    #[test]
    fn interleaved_layers_split_and_coalesce() {
        // スプライト: 0, 0, 20   テキスト: 10, 30, 30
        let runs = merge_ui_draw_runs(&[0, 0, 20], &[], &[], &[10, 30, 30]);
        assert_eq!(
            runs,
            vec![
                run(UiDrawKind::Sprite, 0, 2), // layer 0 ×2 が 1 ランへ融合
                run(UiDrawKind::Text, 0, 1),   // layer 10
                run(UiDrawKind::Sprite, 2, 3), // layer 20
                run(UiDrawKind::Text, 1, 3),   // layer 30 ×2 が 1 ランへ融合
            ]
        );
    }

    /// 4 種別が入り混じるケース。プリミティブ／パーティクルが正しい位置へ挟まる。
    #[test]
    fn four_kinds_interleaved() {
        // スプライト: 0, 100  プリミティブ: 50, 100  パーティクル: 50  テキスト: 50, 100
        let runs = merge_ui_draw_runs(&[0, 100], &[50, 100], &[50], &[50, 100]);
        assert_eq!(
            runs,
            vec![
                run(UiDrawKind::Sprite, 0, 1),    // 0
                run(UiDrawKind::Primitive, 0, 1), // 50
                run(UiDrawKind::Particle, 0, 1),  // 50
                run(UiDrawKind::Text, 0, 1),      // 50
                run(UiDrawKind::Sprite, 1, 2),    // 100
                run(UiDrawKind::Primitive, 1, 2), // 100
                run(UiDrawKind::Text, 1, 2),      // 100
            ]
        );
    }

    /// 負のレイヤーでも順序規則は同じ（背景側へ回り込む）。
    #[test]
    fn negative_layers_are_ordered_correctly() {
        let runs = merge_ui_draw_runs(&[0], &[], &[], &[-5]);
        assert_eq!(
            runs,
            vec![run(UiDrawKind::Text, 0, 1), run(UiDrawKind::Sprite, 0, 1)]
        );
        // パーティクルも同じ（負レイヤーはスプライトより奥）。
        let runs = merge_ui_draw_runs(&[0], &[], &[-5], &[]);
        assert_eq!(
            runs,
            vec![run(UiDrawKind::Particle, 0, 1), run(UiDrawKind::Sprite, 0, 1)]
        );
    }

    /// マージ後のアイテム総数は入力の総数と一致する（取りこぼし・重複が無い）。
    #[test]
    fn total_item_count_is_preserved() {
        let sprites = [3, 3, 8, 12];
        let prims = [1, 8];
        let parts = [0, 8, 12];
        let texts = [8, 8, 9];
        let runs = merge_ui_draw_runs(&sprites, &prims, &parts, &texts);
        let total: usize = runs.iter().map(|r| r.len()).sum();
        assert_eq!(
            total,
            sprites.len() + prims.len() + parts.len() + texts.len()
        );
        // 各種別の区間が先頭から隙間なく連続していることも確認する。
        for kind in KIND_SCAN_ORDER {
            let mut expect = 0usize;
            for r in runs.iter().filter(|r| r.kind == kind) {
                assert_eq!(r.start, expect);
                expect = r.end;
            }
        }
    }
}
