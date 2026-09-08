// ============================================================
//  font/inline/color_runs.rs — 本文の「色付き区間」表
//
//  【役割】
//  展開済み本文（`InlineDoc.text`）のどのバイト範囲を、どの色で描くかだけを持つ。
//  描画側（`canvas_text::emit_glyph_quads`）はグリフごとにこの表を引き、
//  本体（body）の色だけを差し替える。
//
//  【なぜ「範囲の配列」なのか】
//  1 文字ごとに色を持たせると、レイアウト（折り返し・整列）が扱う
//  「文字列 + バイト範囲」という表現に色の配列が並走することになり、
//  切り詰め・行分割のたびに同期を取る必要が出る。
//  区間表なら本文のバイト位置さえ分かれば引けるので、レイアウト側の
//  データ構造を一切変えずに済む（画像位置表 `InlineImages` と同じ考え方）。
//
//  【縁取り・影は追従しない（確定仕様）】
//  縁取りと影は「文字の形」を強調する装飾であり、区間ごとに色が変わると
//  読みづらくなるだけなので、`{color}` は本体の色だけを変える。
//  インライン画像も着色しない（アイコンは自前の色を持つ）。
// ============================================================

use std::ops::Range;

/// 本文の色付き区間表（開始バイト位置の昇順・重なりなし）。
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ColorRuns {
    /// (バイト範囲, RGBA) の昇順リスト。
    runs: Vec<(Range<usize>, [f32; 4])>,
}

impl ColorRuns {
    /// 区間の並びから表を作る（開始位置の昇順へ整列し、空区間は捨てる）。
    ///
    /// 空区間を捨てるのは `{color}{/color}` のような無意味な指定で
    /// 走査カーソルが空回りしないようにするため。
    pub fn from_runs(mut runs: Vec<(Range<usize>, [f32; 4])>) -> Self {
        runs.retain(|(r, _)| r.start < r.end);
        runs.sort_by_key(|(r, _)| r.start);
        Self { runs }
    }

    /// 色付き区間を 1 つも持たないか（＝従来どおり単色で描ける）。
    #[inline]
    pub fn is_empty(&self) -> bool {
        self.runs.is_empty()
    }

    /// 区間の件数。
    #[inline]
    pub fn len(&self) -> usize {
        self.runs.len()
    }

    /// 指定バイト位置の文字色を引く。どの区間にも入らなければ `default`。
    ///
    /// `cursor` は**呼び出し側が保持する走査位置**。本文を先頭から順に舐める
    /// 描画ループでは、これにより 1 グリフあたり償却 O(1) で引ける
    /// （毎回二分探索すると 1 フレームで数千回の log n が積み上がる）。
    /// 位置が後戻りした場合はカーソルを自動的に巻き戻すので、
    /// 呼び出し順に制約は無い（正しさはカーソルに依存しない）。
    pub fn color_at(&self, offset: usize, cursor: &mut usize, default: [f32; 4]) -> [f32; 4] {
        if self.runs.is_empty() {
            return default;
        }
        // カーソルが表の外を指していたら先頭へ戻す（呼び出し側の使い回し対策）。
        if *cursor > self.runs.len() {
            *cursor = 0;
        }
        // 位置が後戻りしていたら、読み飛ばした区間まで巻き戻す。
        while *cursor > 0 && offset < self.runs[*cursor - 1].0.end {
            *cursor -= 1;
        }
        // 終端が現在位置以下の区間は読み飛ばす。
        while *cursor < self.runs.len() && self.runs[*cursor].0.end <= offset {
            *cursor += 1;
        }
        match self.runs.get(*cursor) {
            Some((r, color)) if r.start <= offset => *color,
            _ => default,
        }
    }
}

// ============================================================
//  単体テスト（純関数）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 既定色（テストで「区間外」を表す値）。
    const DEF: [f32; 4] = [1.0, 1.0, 1.0, 1.0];
    /// 区間 A の色。
    const RED: [f32; 4] = [1.0, 0.0, 0.0, 1.0];
    /// 区間 B の色。
    const BLUE: [f32; 4] = [0.0, 0.0, 1.0, 1.0];

    /// 空の表はどの位置でも既定色を返す。
    #[test]
    fn empty_returns_default() {
        let runs = ColorRuns::default();
        let mut c = 0;
        assert!(runs.is_empty());
        assert_eq!(runs.color_at(0, &mut c, DEF), DEF);
        assert_eq!(runs.color_at(100, &mut c, DEF), DEF);
    }

    /// 区間の境界: start は含み、end は含まない。
    #[test]
    fn range_boundaries_are_half_open() {
        let runs = ColorRuns::from_runs(vec![(2..5, RED)]);
        let mut c = 0;
        assert_eq!(runs.color_at(1, &mut c, DEF), DEF, "開始の 1 つ手前は既定色");
        assert_eq!(runs.color_at(2, &mut c, DEF), RED, "開始位置は含む");
        assert_eq!(runs.color_at(4, &mut c, DEF), RED, "終端の 1 つ手前まで");
        assert_eq!(runs.color_at(5, &mut c, DEF), DEF, "終端は含まない");
    }

    /// 複数区間を順に舐めても正しく切り替わる（カーソルが進む経路）。
    #[test]
    fn multiple_runs_switch_in_order() {
        let runs = ColorRuns::from_runs(vec![(0..2, RED), (4..6, BLUE)]);
        let mut c = 0;
        let got: Vec<[f32; 4]> = (0..7).map(|i| runs.color_at(i, &mut c, DEF)).collect();
        assert_eq!(got, vec![RED, RED, DEF, DEF, BLUE, BLUE, DEF]);
    }

    /// 位置が後戻りしてもカーソルが巻き戻り、正しい色を返す。
    #[test]
    fn cursor_rewinds_on_backward_offset() {
        let runs = ColorRuns::from_runs(vec![(0..2, RED), (4..6, BLUE)]);
        let mut c = 0;
        assert_eq!(runs.color_at(5, &mut c, DEF), BLUE);
        assert_eq!(runs.color_at(1, &mut c, DEF), RED, "後戻りしても引ける");
    }

    /// 範囲外（本文末尾より後ろ）は既定色。
    #[test]
    fn beyond_last_run_is_default() {
        let runs = ColorRuns::from_runs(vec![(0..2, RED)]);
        let mut c = 0;
        assert_eq!(runs.color_at(999, &mut c, DEF), DEF);
        // カーソルが末尾を越えた状態から引き直しても壊れない。
        assert_eq!(runs.color_at(0, &mut c, DEF), RED);
    }

    /// 空区間は捨てられ、区間は開始位置順へ整列される。
    #[test]
    fn empty_runs_are_dropped_and_sorted() {
        let runs = ColorRuns::from_runs(vec![(4..6, BLUE), (3..3, RED), (0..2, RED)]);
        assert_eq!(runs.len(), 2, "空区間は捨てられる");
        let mut c = 0;
        assert_eq!(runs.color_at(0, &mut c, DEF), RED);
        assert_eq!(runs.color_at(4, &mut c, DEF), BLUE);
    }
}
