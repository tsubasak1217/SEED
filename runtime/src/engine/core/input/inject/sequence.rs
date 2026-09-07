// ============================================================
//  sequence.rs — 時間軸付き入力シーケンスの解釈と再生
//
//  `INPUT_SEQUENCE:{json}` で受け取った「t 秒後にこの操作」を並べた列を、
//  **実時間**で 1 フレームずつ消化するスケジューラ。
//
//  【なぜ時間軸が要るか】
//  「マウスを左から右へ振る」「W を 0.5 秒だけ押す」といった操作は、
//  1 フレームに全部注入しても意味を成さない（速度が算出されない・
//  GetKeyDown と GetKeyUp が同フレームで潰れる）。複数フレームに割って
//  注入して初めて、実際に人が操作したのと同じ入力列になる。
//
//  このファイルは時計を持たない。経過秒（dt）は呼び出し側から渡される
//  （＝ Play の一時停止で時計を止める判断は App 側の責務）。
// ============================================================

use serde::Deserialize;

use super::command::InjectAction;

// ============================================================
//  定数
// ============================================================

/// 1 イベントに書ける操作の個数（キー・マウスボタン・移動などのうち 1 つだけ）。
/// 複数書かれた場合は曖昧なので拒否する。
const ACTIONS_PER_EVENT: usize = 1;

/// 相対移動 / 絶対座標の配列要素数（[x, y]）。
const VEC2_LEN: usize = 2;

// ============================================================
//  SequenceEvent — 「t 秒後にこの操作」
// ============================================================

/// シーケンス中の 1 イベント。
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SequenceEvent {
    /// シーケンス開始からの経過秒（実時間）。
    pub t: f32,
    /// その時刻に注入する操作。
    pub action: InjectAction,
}

// ============================================================
//  InputSequencePlayer — 再生器
// ============================================================

/// 時刻順に並べたイベント列を、経過秒に応じて順に吐き出す。
///
/// 「同じ t のイベントが複数ある」場合は **JSON に書かれた順**で同一フレームに
/// まとめて発火する（安定ソートを使うため入力順が保たれる）。
#[derive(Debug, Clone)]
pub struct InputSequencePlayer {
    /// t の昇順に並べたイベント列（同 t は入力順を保つ）。
    events: Vec<SequenceEvent>,
    /// 次に発火するイベントの添字。
    next: usize,
    /// 再生開始からの累積経過秒。
    elapsed: f32,
}

impl InputSequencePlayer {
    /// イベント列から再生器を作る（t の昇順へ安定ソートする）。
    pub fn new(mut events: Vec<SequenceEvent>) -> Self {
        // total_cmp ではなく partial_cmp で十分（t は解釈時に有限であることを保証済み）。
        events.sort_by(|a, b| a.t.partial_cmp(&b.t).unwrap_or(std::cmp::Ordering::Equal));
        Self { events, next: 0, elapsed: 0.0 }
    }

    /// `INPUT_SEQUENCE:` の JSON 本文から直接作る（テスト・診断用の近道）。
    ///
    /// 本番経路は `command::parse_inject_command` を通るが、そちらも中で
    /// `parse_sequence_events` + `new` を呼ぶだけなので挙動は同一である。
    pub fn from_json(json: &str) -> Result<Self, String> {
        Ok(Self::new(parse_sequence_events(json)?))
    }

    /// 経過秒ぶん進め、締め切りを迎えたイベントの操作を返す。
    ///
    /// 初回は dt = 0 で呼ばれる想定で、その時点で `t <= 0` のイベントが発火する。
    /// 負の dt は 0 として扱う（時計が巻き戻る環境でも順序を壊さない）。
    pub fn advance(&mut self, dt: f32) -> Vec<InjectAction> {
        self.elapsed += dt.max(0.0);

        let mut due = Vec::new();
        while self.next < self.events.len() && self.events[self.next].t <= self.elapsed {
            due.push(self.events[self.next].action);
            self.next += 1;
        }
        due
    }

    /// 全イベントを消化し終えたか。
    #[inline]
    pub fn is_finished(&self) -> bool {
        self.next >= self.events.len()
    }

    /// 未発火のイベント数（テスト・診断用）。
    #[inline]
    pub fn remaining(&self) -> usize {
        self.events.len() - self.next
    }
}

// ============================================================
//  JSON 表現の解釈
// ============================================================

/// `INPUT_SEQUENCE:` の JSON 1 要素に対応する素の表現。
///
/// 未知のキーは拒否する（`deny_unknown_fields`）。綴り間違いを黙って無視すると
/// 「送ったのに動かない」という最も切り分けにくい不具合になるため。
#[derive(Debug, Deserialize)]
#[serde(deny_unknown_fields)]
struct SequenceEventJson {
    /// シーケンス開始からの経過秒。省略時は 0（＝即時）。
    #[serde(default)]
    t: f32,
    /// キー名（`INPUT_KEY` と同じ表記）。`down` と対で使う。
    #[serde(default)]
    key: Option<String>,
    /// マウスボタン名（left / right / middle）。`down` と対で使う。
    #[serde(default)]
    mouse_button: Option<String>,
    /// 押下 / 解放。`key` または `mouse_button` を書いたときのみ必須。
    #[serde(default)]
    down: Option<bool>,
    /// 相対移動 [dx, dy]。
    #[serde(default)]
    mouse_move: Option<Vec<f32>>,
    /// 絶対座標 [x, y]。
    #[serde(default)]
    mouse_pos: Option<Vec<f32>>,
    /// ホイール量。
    #[serde(default)]
    scroll: Option<f32>,
}

/// `INPUT_SEQUENCE:` の JSON 部分を [`SequenceEvent`] の列へ変換する。
///
/// エラーは `INPUT_ERROR:{reason}` にそのまま載る短い識別子で返す。
pub fn parse_sequence_events(json: &str) -> Result<Vec<SequenceEvent>, String> {
    let raw: Vec<SequenceEventJson> = serde_json::from_str(json.trim())
        .map_err(|e| format!("bad_json:{e}"))?;

    if raw.is_empty() {
        return Err("empty_sequence".to_string());
    }

    let mut events = Vec::with_capacity(raw.len());
    for (index, item) in raw.into_iter().enumerate() {
        let action = event_action(&item, index)?;
        if !item.t.is_finite() {
            return Err(format!("bad_time:{index}"));
        }
        events.push(SequenceEvent { t: item.t, action });
    }
    Ok(events)
}

/// JSON 1 要素からちょうど 1 個の操作を取り出す。
///
/// 0 個（何も書いていない）も 2 個以上（曖昧）も拒否する。
/// 「1 イベント = 1 操作」に固定することで、同時刻の複数操作は
/// 「同じ t の要素を複数並べる」という 1 通りの書き方に収束する。
fn event_action(item: &SequenceEventJson, index: usize) -> Result<InjectAction, String> {
    let mut found: Option<InjectAction> = None;
    let mut count = 0usize;

    if let Some(name) = item.key.as_deref() {
        count += 1;
        let key = super::command::key_name_to_code(name)
            .ok_or_else(|| format!("unknown_key:{name}"))?;
        let down = item.down.ok_or_else(|| format!("missing_down:{index}"))?;
        found = Some(InjectAction::Key { key, down });
    }
    if let Some(name) = item.mouse_button.as_deref() {
        count += 1;
        let button = super::command::mouse_button_name_to_code(name)
            .ok_or_else(|| format!("unknown_mouse_button:{name}"))?;
        let down = item.down.ok_or_else(|| format!("missing_down:{index}"))?;
        found = Some(InjectAction::MouseButton { button, down });
    }
    if let Some(v) = item.mouse_move.as_deref() {
        count += 1;
        let [dx, dy] = vec2(v, index)?;
        found = Some(InjectAction::MouseMove { dx, dy });
    }
    if let Some(v) = item.mouse_pos.as_deref() {
        count += 1;
        let [x, y] = vec2(v, index)?;
        found = Some(InjectAction::MousePos { x, y });
    }
    if let Some(amount) = item.scroll {
        count += 1;
        if !amount.is_finite() {
            return Err(format!("bad_number:{index}"));
        }
        found = Some(InjectAction::Scroll { amount });
    }

    match found {
        Some(action) if count == ACTIONS_PER_EVENT => Ok(action),
        Some(_) => Err(format!("ambiguous_event:{index}")),
        None => Err(format!("empty_event:{index}")),
    }
}

/// 2 要素の実数配列を検証して取り出す。
fn vec2(v: &[f32], index: usize) -> Result<[f32; VEC2_LEN], String> {
    if v.len() != VEC2_LEN {
        return Err(format!("bad_vec2:{index}"));
    }
    if !v[0].is_finite() || !v[1].is_finite() {
        return Err(format!("bad_number:{index}"));
    }
    Ok([v[0], v[1]])
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use winit::event::MouseButton;
    use winit::keyboard::KeyCode;

    /// t 順に発火し、同 t は入力順のまま同一フレームでまとめて出る。
    #[test]
    fn fires_in_time_order_and_groups_same_time() {
        let json = r#"[
            {"t":0.5,"key":"S","down":true},
            {"t":0.0,"key":"W","down":true},
            {"t":0.5,"mouse_button":"left","down":true}
        ]"#;
        let mut p = InputSequencePlayer::new(parse_sequence_events(json).unwrap());

        // 初回（dt=0）は t=0 のものだけ。
        let due = p.advance(0.0);
        assert_eq!(due, vec![InjectAction::Key { key: KeyCode::KeyW, down: true }]);
        assert!(!p.is_finished());

        // まだ 0.5 秒に届かない。
        assert!(p.advance(0.2).is_empty());

        // 0.5 秒到達で同 t の 2 件が入力順に出る。
        let due = p.advance(0.3);
        assert_eq!(
            due,
            vec![
                InjectAction::Key { key: KeyCode::KeyS, down: true },
                InjectAction::MouseButton { button: MouseButton::Left, down: true },
            ]
        );
        assert!(p.is_finished());
        assert!(p.advance(10.0).is_empty(), "完走後は何も出ないこと");
    }

    /// 1 フレームが長く飛んでも、締め切りを過ぎたイベントは取りこぼさない。
    #[test]
    fn long_frame_flushes_all_overdue_events() {
        let json = r#"[{"t":0.1,"key":"A","down":true},{"t":0.2,"key":"A","down":false}]"#;
        let mut p = InputSequencePlayer::new(parse_sequence_events(json).unwrap());
        let due = p.advance(1.0);
        assert_eq!(due.len(), 2, "遅延フレームでも順に全部出ること");
        assert!(p.is_finished());
    }

    /// 各種イベント表現を解釈できる。
    #[test]
    fn parses_all_event_shapes() {
        let json = r#"[
            {"t":0.0,"mouse_move":[120,-3]},
            {"t":0.1,"mouse_pos":[640,360]},
            {"t":0.2,"scroll":-1.5},
            {"t":0.3,"key":"Space","down":false}
        ]"#;
        let events = parse_sequence_events(json).unwrap();
        assert_eq!(events[0].action, InjectAction::MouseMove { dx: 120.0, dy: -3.0 });
        assert_eq!(events[1].action, InjectAction::MousePos { x: 640.0, y: 360.0 });
        assert_eq!(events[2].action, InjectAction::Scroll { amount: -1.5 });
        assert_eq!(
            events[3].action,
            InjectAction::Key { key: KeyCode::Space, down: false }
        );
    }

    /// t は省略できる（0 扱い）。
    #[test]
    fn time_defaults_to_zero() {
        let events = parse_sequence_events(r#"[{"key":"W","down":true}]"#).unwrap();
        assert_eq!(events[0].t, 0.0);
    }

    /// 不正な JSON・空配列・曖昧な要素・欠けた down を拒否する。
    #[test]
    fn rejects_malformed_sequences() {
        assert!(parse_sequence_events("{not json").unwrap_err().starts_with("bad_json"));
        assert_eq!(parse_sequence_events("[]").unwrap_err(), "empty_sequence");
        assert!(parse_sequence_events(r#"[{"t":0.0}]"#)
            .unwrap_err()
            .starts_with("empty_event"));
        assert!(parse_sequence_events(r#"[{"key":"W"}]"#)
            .unwrap_err()
            .starts_with("missing_down"));
        assert!(
            parse_sequence_events(r#"[{"key":"W","down":true,"scroll":1.0}]"#)
                .unwrap_err()
                .starts_with("ambiguous_event")
        );
        assert!(parse_sequence_events(r#"[{"key":"Nope","down":true}]"#)
            .unwrap_err()
            .starts_with("unknown_key"));
        assert!(parse_sequence_events(r#"[{"mouse_move":[1]}]"#)
            .unwrap_err()
            .starts_with("bad_vec2"));
        // 未知のキー名（綴り間違い）は黙って無視せず拒否する。
        assert!(parse_sequence_events(r#"[{"t":0,"keyy":"W","down":true}]"#)
            .unwrap_err()
            .starts_with("bad_json"));
    }
}
