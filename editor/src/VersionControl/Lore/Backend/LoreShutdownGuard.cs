// ============================================================
//  LoreShutdownGuard.cs — Lore.Shutdown() をプロセスで 1 回だけ呼ぶための番人
//
//  【役割】
//  LoreVcs のネイティブ側リソースを解放する Lore.Shutdown() は
//  **プロセス全体で 1 回だけ** 呼ぶもの。作業コピーごとの Dispose で呼ぶと、
//  まだ使っている別の呼び出しを壊す。呼び出し箇所をここへ一本化する。
//
//  【呼ぶ場所】
//  editor/src/App.xaml.cs の OnExit（アプリ終了時）。
//  プロジェクトを閉じるときには呼ばない（同じプロセスで開き直す可能性がある）。
//
//  【呼ばなかった場合】
//  プロセス終了で OS が回収するので致命的にはならない。ただしログファイルの
//  フラッシュなどが行われない可能性があるため、通常終了では必ず通す。
//
//  【依存】
//  LoreVcs にのみ依存（WPF には依存しない）。
// ============================================================

using System;
using System.Threading;
using LoreVcs;

// 自分の名前空間 SEEDEditor.VersionControl.Lore が LoreVcs の静的クラス Lore を
// 隠してしまうため、明示的な別名で参照する（`Lore.` と書くと名前空間の方に解決される）。
using LoreApi = global::LoreVcs.Lore;

namespace SEEDEditor.VersionControl.Lore.Backend;

/// <summary>
/// <c>Lore.Shutdown()</c>（コード上は LoreApi）の呼び出しを 1 回に制限する。
/// </summary>
public static class LoreShutdownGuard
{
    /// <summary>まだ呼んでいないことを表す値。</summary>
    private const int NOT_SHUT_DOWN = 0;

    /// <summary>既に呼んだことを表す値。</summary>
    private const int ALREADY_SHUT_DOWN = 1;

    /// <summary>呼び出し済みかどうかの印（複数スレッドから来ても 1 回にする）。</summary>
    private static int _state = NOT_SHUT_DOWN;

    /// <summary>既に <see cref="Shutdown"/> が呼ばれたか。</summary>
    public static bool HasShutDown => Volatile.Read(ref _state) == ALREADY_SHUT_DOWN;

    /// <summary>
    /// Lore のネイティブ側を終了させる。2 回目以降は何もしない。
    /// </summary>
    /// <param name="log">診断ログの出力先（省略可）。</param>
    /// <returns>この呼び出しで実際に終了処理を行ったなら真。</returns>
    public static bool Shutdown(Action<string>? log = null)
    {
        if (Interlocked.Exchange(ref _state, ALREADY_SHUT_DOWN) == ALREADY_SHUT_DOWN)
            return false;

        try
        {
            LoreApi.Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            // 終了処理の失敗でエディタの終了を止めない。
            log?.Invoke($"[VCS] Lore の終了処理に失敗しました: {ex.Message}");
            return false;
        }
    }
}
