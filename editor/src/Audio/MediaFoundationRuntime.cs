// ============================================================
//  MediaFoundationRuntime.cs — Media Foundation の初期化を 1 か所へ集約
//
//  【役割】
//  Windows の復号／符号化基盤（Media Foundation）は、使う前にプロセスで
//  一度だけ Startup() を呼ぶ必要がある。呼ばずに使うと復号器の生成に失敗し、
//  二重に呼ぶのも避けたい。その「一度だけ」をここで保証する。
//
//  【なぜ共有するのか】
//  無音カット（AudioSilenceTrimmer）と試聴（NAudioPreviewPlayer）の両方が
//  MediaFoundationReader を使う。初期化フラグを各クラスが持つと、
//  「片方だけ初期化済み」という状態が生まれて原因の分かりにくい失敗になる。
//  プロセスで 1 つの事実は 1 か所に置く。
//
//  【終了処理をしない理由】
//  MediaFoundationApi.Shutdown() は用意されているが呼ばない。
//  再生・変換の途中でシャットダウンすると復号器が不正な状態になる一方、
//  プロセス終了時は OS が確実に片付けるため、呼ばない方が安全。
// ============================================================

using NAudio.MediaFoundation;

namespace SEEDEditor.Audio;

/// <summary>
/// Media Foundation の初期化状態を持つ静的クラス（スレッド安全）。
/// </summary>
public static class MediaFoundationRuntime
{
    /// <summary>初期化済みかどうか。</summary>
    private static bool _started;

    /// <summary><see cref="_started"/> の排他用ロック。</summary>
    private static readonly object Gate = new();

    /// <summary>
    /// Media Foundation を一度だけ初期化する。
    /// 2 回目以降の呼び出しは何もしない。複数スレッドから同時に呼んでよい。
    /// </summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started) return;
            MediaFoundationApi.Startup();
            _started = true;
        }
    }
}
