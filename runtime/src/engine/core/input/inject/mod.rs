// ============================================================
//  inject/mod.rs — 外部（エディタ／MCP 経由の AI）からの入力注入
//
//  【何のための仕組みか】
//  AI がゲームを「実際に遊んで」スクリーンショットで確認できるようにするため、
//  IPC 経由でキーボード・マウス操作をランタイムへ注入する。
//  注入された入力は実入力と **OR 合成** され、スクリプトから見える
//  `SEED.Input.*` の値がそのまま変化する（スクリプト側に一切の分岐を持ち込まない）。
//
//  【責務の分割（単一責任）】
//  - `command.rs`  … IPC 文字列 → 注入コマンド型（純粋なパース。副作用なし）
//  - `state.rs`    … 注入された押下・移動量の保持と 1 フレーム分のエッジ管理
//  - `sequence.rs` … 時間軸付きイベント列の実時間再生（スケジューラ）
//  - `mod.rs`（本ファイル）… 上記 3 つを束ねてランタイムへ 1 個の窓口を出す
//
//  実入力との合成そのものは `input::Input`（親モジュール）が行う。
//  ここは「注入側の状態」だけを持ち、実入力には一切触れない。
// ============================================================

pub mod command;
pub mod sequence;
pub mod state;

pub use command::{parse_inject_command, InjectAction, InjectCommand, INJECT_COMMAND_PREFIX};
pub use sequence::InputSequencePlayer;
pub use state::InjectedInputState;

use std::time::{Duration, Instant};

// ============================================================
//  応答文字列（IPC）
// ============================================================

/// 受理応答。
pub const INJECT_REPLY_OK: &str = "INPUT_OK";
/// 失敗応答の接頭辞（`INPUT_ERROR:{reason}`）。
pub const INJECT_REPLY_ERROR_PREFIX: &str = "INPUT_ERROR:";
/// シーケンス完走の通知。
pub const INJECT_REPLY_SEQUENCE_DONE: &str = "INPUT_SEQUENCE_DONE";

/// Play 中でないときに返す理由文字列。
pub const INJECT_ERROR_NOT_PLAYING: &str = "not_playing";
/// 既に別のシーケンスを再生中のときに返す理由文字列。
pub const INJECT_ERROR_SEQUENCE_BUSY: &str = "sequence_busy";

// ============================================================
//  InputInjection — 注入機構ぜんぶの入れ物
// ============================================================

/// 注入状態（押下・移動量）とシーケンス再生器をまとめて保持する。
///
/// `Input` がフィールドとして 1 個だけ持つ。App 側に状態フィールドを増やさずに
/// 済ませるため、「前フレームは実行中だったか」もここで覚えている
/// （Play 停止を跨いだ押下スタックを自動解放するのに使う）。
pub struct InputInjection {
    /// 注入された押下・移動量の現在値。
    state: InjectedInputState,
    /// 再生中のシーケンス（None = 再生していない）。
    sequence: Option<InputSequencePlayer>,
    /// 直近の `tick` を呼んだ実時刻。シーケンスの経過時間を実時間で刻むために使う。
    last_tick_at: Option<Instant>,
    /// 直近の `tick` 時点で Play だったか。
    /// true から false への変化（＝ Play から抜けた）で全解放する。
    /// **一時停止はここに含めない**（Pause は Play の一部であり、
    /// 押下を解放してしまうと再開時に操作が失われるため）。
    was_playing: bool,
}

/// `tick` の結果。App 側が IPC 応答を送るかどうかを判断するために返す。
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct InjectTickOutcome {
    /// このフレームでシーケンスが完走した（`INPUT_SEQUENCE_DONE` を送る）。
    pub sequence_finished: bool,
    /// このフレームで Play から抜けたため全解放した（ログ用途）。
    pub released_by_stop: bool,
}

impl InputInjection {
    /// 何も注入されていない初期状態を作る。
    pub fn new() -> Self {
        Self {
            state: InjectedInputState::new(),
            sequence: None,
            last_tick_at: None,
            was_playing: false,
        }
    }

    /// 注入状態への参照（`Input` の合成クエリが読む）。
    #[inline]
    pub fn state(&self) -> &InjectedInputState {
        &self.state
    }

    /// シーケンスを再生中か。
    #[inline]
    pub fn is_sequence_playing(&self) -> bool {
        self.sequence.is_some()
    }

    /// 単発アクションを適用する（即座に注入状態へ反映される）。
    pub fn apply_action(&mut self, action: InjectAction) {
        self.state.apply(action);
    }

    /// シーケンス再生を開始する。
    ///
    /// 既に再生中なら `false` を返して何もしない（呼び出し側が
    /// `INPUT_ERROR:sequence_busy` を返す）。開始時点で t が 0 以下のイベントは
    /// 次の `tick`（同フレーム内）でまとめて発火する。
    pub fn start_sequence(&mut self, player: InputSequencePlayer) -> bool {
        if self.sequence.is_some() {
            return false;
        }
        self.sequence = Some(player);
        // 経過時間の起点を今にそろえる（前回 tick からの空白時間を持ち込まない）。
        self.last_tick_at = Some(Instant::now());
        true
    }

    /// 注入中の押下・移動量・シーケンスをすべて破棄する（安全弁）。
    pub fn release_all(&mut self) {
        self.state.release_all();
        self.sequence = None;
    }

    /// フレーム末に呼ぶ。押下エッジと 1 フレーム限りの累積値を畳む。
    ///
    /// `Input::end_frame` から呼ばれる。実入力の `end_frame` と同じタイミングで
    /// 畳むことで、注入のエッジ（GetKeyDown）も実入力とまったく同じ
    /// 「1 フレームだけ立つ」挙動になる。
    pub fn end_frame(&mut self) {
        self.state.end_frame();
    }

    /// フレーム先頭（IPC 処理の直後）に呼ぶ。シーケンスを実時間で進める。
    ///
    /// - `playing`: Play モードなら true。**false へ落ちた瞬間に全解放する**
    ///   （Play を止めたのに押しっぱなしが残らないようにする安全弁）。
    /// - `paused`: Play の一時停止中なら true。時計だけを止め、押下は保持する
    ///   （`INPUT_SEQUENCE` の t は実時間だが、一時停止中は進めない仕様）。
    /// - `now`: 現在時刻。テストから任意の時刻を渡せるよう引数にしている。
    pub fn tick(&mut self, playing: bool, paused: bool, now: Instant) -> InjectTickOutcome {
        let mut outcome = InjectTickOutcome::default();

        // ── (1) Play から抜けたら全解放する（押下スタックの安全弁）──────────
        //   一時停止では解放しない。Pause は「後で再開する」状態であり、
        //   ここで押下を落とすと再開後に操作が途切れてしまう。
        if self.was_playing && !playing {
            self.release_all();
            outcome.released_by_stop = true;
        }
        self.was_playing = playing;

        let running = playing && !paused;
        if !running {
            // 停止・一時停止中は時計を止める（再開時に飛ばないよう起点を今へ寄せる）。
            self.last_tick_at = Some(now);
            return outcome;
        }

        // ── (2) 経過時間を求める（初回 tick は 0 秒扱い）─────────────────────
        let dt = match self.last_tick_at {
            Some(prev) => now.saturating_duration_since(prev),
            None => Duration::ZERO,
        };
        self.last_tick_at = Some(now);

        // ── (3) シーケンスを進め、締め切りを迎えたイベントを注入する ──────────
        if let Some(player) = self.sequence.as_mut() {
            let due = player.advance(dt.as_secs_f32());
            for action in due {
                self.state.apply(action);
            }
            if player.is_finished() {
                self.sequence = None;
                outcome.sequence_finished = true;
            }
        }

        outcome
    }
}

impl Default for InputInjection {
    fn default() -> Self {
        Self::new()
    }
}

// ============================================================
//  ユニットテスト（束ね役の挙動）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;
    use winit::keyboard::KeyCode;

    /// テスト用: 指定秒だけ進んだ時刻を作る。
    fn at(base: Instant, secs: f32) -> Instant {
        base + Duration::from_secs_f32(secs)
    }

    /// シーケンスが実時間で順に発火し、完走時に 1 度だけ通知が出る。
    #[test]
    fn sequence_plays_in_real_time_and_reports_done_once() {
        let json = r#"[{"t":0.0,"key":"W","down":true},{"t":0.5,"key":"W","down":false}]"#;
        let player = InputSequencePlayer::from_json(json).expect("解釈できるはず");

        let mut inj = InputInjection::new();
        assert!(inj.start_sequence(player));

        let t0 = Instant::now();
        // t=0 のイベントは最初の tick で発火する。
        let o = inj.tick(true, false, t0);
        assert!(!o.sequence_finished);
        assert!(inj.state().is_key_held(KeyCode::KeyW), "t=0 の押下が入っていること");

        // 0.2 秒後: まだ離していない。
        inj.end_frame();
        let o = inj.tick(true, false, at(t0, 0.2));
        assert!(!o.sequence_finished);
        assert!(inj.state().is_key_held(KeyCode::KeyW));

        // 0.6 秒後: 離しイベントが発火し、これで全部消化＝完走。
        inj.end_frame();
        let o = inj.tick(true, false, at(t0, 0.6));
        assert!(o.sequence_finished, "完走通知が出ること");
        assert!(!inj.state().is_key_held(KeyCode::KeyW));

        // 完走通知は 1 度だけ。
        inj.end_frame();
        let o = inj.tick(true, false, at(t0, 1.0));
        assert!(!o.sequence_finished);
    }

    /// 一時停止中はシーケンスの時計が進まない。
    #[test]
    fn paused_does_not_advance_sequence() {
        let json = r#"[{"t":0.5,"key":"Space","down":true}]"#;
        let mut inj = InputInjection::new();
        inj.start_sequence(InputSequencePlayer::from_json(json).unwrap());

        let t0 = Instant::now();
        inj.tick(true, false, t0);
        // 1 秒ぶん「一時停止のまま」経過させる。
        inj.tick(true, true, at(t0, 1.0));
        assert!(!inj.state().is_key_held(KeyCode::Space), "停止中は発火しないこと");

        // 再開直後は起点が寄せられているので、まだ 0.5 秒経っていない。
        inj.tick(true, false, at(t0, 1.1));
        assert!(!inj.state().is_key_held(KeyCode::Space));
        // 再開から 0.5 秒でようやく発火する。
        inj.tick(true, false, at(t0, 1.7));
        assert!(inj.state().is_key_held(KeyCode::Space));
    }

    /// Play を抜けたら注入中の押下は自動で解放される。
    #[test]
    fn leaving_play_releases_everything() {
        let mut inj = InputInjection::new();
        inj.apply_action(InjectAction::Key { key: KeyCode::KeyW, down: true });
        let t0 = Instant::now();
        inj.tick(true, false, t0);
        assert!(inj.state().is_key_held(KeyCode::KeyW));

        let o = inj.tick(false, false, at(t0, 0.1));
        assert!(o.released_by_stop);
        assert!(!inj.state().is_key_held(KeyCode::KeyW), "Play 停止で解放されること");
    }

    /// 再生中に別のシーケンスを受け付けない（busy）。
    #[test]
    fn second_sequence_is_rejected_while_playing() {
        let mut inj = InputInjection::new();
        let a = InputSequencePlayer::from_json(r#"[{"t":1.0,"key":"W","down":true}]"#).unwrap();
        let b = InputSequencePlayer::from_json(r#"[{"t":0.0,"key":"S","down":true}]"#).unwrap();
        assert!(inj.start_sequence(a));
        assert!(!inj.start_sequence(b), "多重再生は拒否されること");
    }
}
