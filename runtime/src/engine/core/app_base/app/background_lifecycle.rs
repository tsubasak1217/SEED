// ============================================================
//  background_lifecycle.rs — バックグラウンドへの出入り（Android の suspended / resumed）
//
//  【担当】
//  surface_lifecycle.rs（描画サーフェスの破棄・再生成）と並んで suspended / 2 回目以降の resumed から
//  呼ばれ、サーフェス以外の「背面へ回るとき・前面へ戻るときにやること」を受け持つ。
//
//    背面へ（enter_background）:
//      1. セーブの未書き出し分を書き出す … Android はアプリを閉じるとプロセスごと即終了する
//         （MainActivity.onDestroy。Windows の CloseRequested に当たる経路が無い）ため、背面へ回る
//         この時点が実質の「終了時の保存」。バックグラウンドのプロセスは OS に予告なく殺されることもある
//      2. パイプラインキャッシュを保存する … 同じ理由で Renderer の Drop が走らないため（次回起動の短縮）
//      3. シミュレーションを止める … 物理スレッドを眠らせる（core::background_gate を立てる）
//         背面の印は書き出しが済んでから立てる（印を見た後の書き換えは、この書き出しに含まれないと
//         言い切れるようにするため。検証用フック runtime/android/native/src/debug_save_test.rs が頼る順序）
//      4. 音声を止める … 出力ストリームごと一時停止する（audio_output_sync.rs。背面の印を見て決めるので 3 の後）
//    前面へ（enter_foreground）:
//      1. シミュレーションを再開する（物理スレッドが条件変数ですぐ起きる）
//      2. 背面にいた時間をゲームの時間から捨てる（Clock::forget_elapsed。取り戻しの連続実行を防ぐ）
//      3. 音声を戻す … ただし音声フォーカスを失ったまま（着信中・他のアプリが再生中）なら止めたまま
//
//  【同期で行う理由】
//  suspended の処理が終わるまで Android の UI スレッドはウィンドウの破棄を待っている
//  （android-activity の glue が終わりを待つ）。ここで書き終えてから戻れば、直後にプロセスが
//  殺されてもセーブは残る。書き込むのはセーブ（数 KB）とパイプラインキャッシュ（数 MB。内容が
//  変わらなければ書かない）だけなので、UI スレッドの待ちは短い。音声の一時停止（AAudio の requestPause）は
//  待たずに戻る。
//
//  デスクトップには suspended が届かないので、このファイルの処理は一切走らない
//  （初回の resumed で呼ぶ ensure_foreground は、もともと前面なので何も変えない）。
// ============================================================

use crate::engine::core::background_gate;
use crate::engine::core::save;

use super::App;

/// ログの行頭（ライフサイクル診断と同じ印）。
const LOG_TAG: &str = "[SEED LIFECYCLE]";

impl App {
    /// バックグラウンドへ回った（suspended）。セーブとパイプラインキャッシュを書き出し、シミュレーションと音声を止める。
    ///
    /// 描画サーフェスの破棄（handle_suspended）より先に呼ぶ（書き出しを最優先するため。
    /// どちらも suspended から戻る前に終わる）。
    pub(super) fn enter_background(&mut self) {
        // ① セーブの未書き出し分を同期で書き出す（ここで戻る前にディスクへ届く）。
        let outcome = save::flush_if_dirty();
        eprintln!("[SEED SAVE] suspended: {}", outcome.describe());

        // ② パイプラインキャッシュ（中身が変わっていなければ書かない。結果は pipeline_cache がログに出す）。
        if let Some(renderer) = &self.renderer {
            renderer.save_pipeline_cache();
        }

        // ③ 物理スレッドを止める（次の周回から眠る）。書き出しの後に立てる理由はファイル先頭のコメント。
        background_gate::enter_background();
        eprintln!("{LOG_TAG} background: シミュレーションを止めました（物理スレッドは前面へ戻るまで眠ります）");

        // ④ 音声を止める（出力ストリームごと一時停止。③の印を見て決める。まだ何も鳴らしていなければ何もしない）。
        self.sync_audio_output();
    }

    /// 前面へ戻った（2 回目以降の resumed でサーフェスを作り直せたとき）。シミュレーションと音声を再開する。
    pub(super) fn enter_foreground(&mut self) {
        // 背面にいた時間はゲームの時間に入れない（最初のフレームの delta を「今から」にする）。
        self.clock.forget_elapsed();
        background_gate::enter_foreground();
        eprintln!("{LOG_TAG} foreground: シミュレーションを再開しました");
        // 音声を戻す（音声フォーカスを失ったままなら止めたまま。audio_output_sync.rs）。
        self.sync_audio_output();
    }

    /// 初回の resumed（初期化）の後に呼ぶ。前面であることを確定させる。
    ///
    /// 初期化より前に suspended が届いていた（ウィンドウができる前に背面へ回った）場合に、
    /// 物理スレッドが眠ったまま起きなくなるのを防ぐ。通常は既に前面なので何も変わらない。
    pub(super) fn ensure_foreground(&mut self) {
        if background_gate::is_background() {
            self.enter_foreground();
        }
    }
}
