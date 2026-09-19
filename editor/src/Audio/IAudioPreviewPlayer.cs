// ============================================================
//  IAudioPreviewPlayer.cs — 「実際に音を出す部分」の境界
//
//  【なぜ境界を切るのか】
//  試聴の難しさは音の出し方ではなく、状態遷移のほうにある。
//    ・別のファイルを再生したら前のを止める
//    ・最後まで鳴ったらボタンを再生アイコンへ戻す
//    ・フォルダ移動・パネル終了・Play 開始で止める
//    ・一覧を作り直して項目が消えても、再生中の参照を握り続けない
//  これらを実デバイスごと検証しようとすると、音が鳴る・環境依存で落ちる・
//  自動テストに乗らない、と三重に困る。
//  そこで「音を出す」だけをこのインタフェースの向こうへ追い出し、
//  状態遷移は AudioPreviewController（純ロジック）で偽物を相手にテストする。
//
//  【WPF 非依存・NAudio 非依存】
//  この宣言自体は何にも依存しない。実装（NAudioPreviewPlayer）だけが NAudio を使う。
// ============================================================

using System;

namespace SEEDEditor.Audio;

/// <summary>
/// 試聴の再生開始を試みた結果。
/// </summary>
/// <param name="Success">再生を開始できたか。</param>
/// <param name="ErrorMessage">失敗した理由（成功時は null）。画面にそのまま出せる日本語。</param>
public readonly record struct AudioPreviewStartResult(bool Success, string? ErrorMessage)
{
    /// <summary>成功を表す結果。</summary>
    public static AudioPreviewStartResult Ok() => new(true, null);

    /// <summary>失敗を表す結果を作る。</summary>
    /// <param name="message">画面に出す理由。</param>
    public static AudioPreviewStartResult Fail(string message) => new(false, message);
}

/// <summary>
/// 試聴の再生装置。1 度に 1 ファイルだけを鳴らせればよい。
///
/// <para>
/// 実装は例外を投げないこと（出力デバイスが無い・ファイルが壊れている等は
/// <see cref="AudioPreviewStartResult"/> で返す）。試聴の失敗でエディタが落ちてはならない。
/// </para>
/// </summary>
public interface IAudioPreviewPlayer : IDisposable
{
    /// <summary>
    /// 指定ファイルの再生を始める。既に何か鳴っていれば、それを止めてから始める。
    /// </summary>
    /// <param name="path">音声ファイルの絶対パス。</param>
    /// <param name="volume">音量（0.0〜1.0）。</param>
    /// <returns>成否と、失敗したときの理由。</returns>
    AudioPreviewStartResult Play(string path, float volume);

    /// <summary>
    /// 再生中なら止める。鳴っていなければ何もしない。
    /// <para>
    /// この呼び出しが原因で <see cref="PlaybackEnded"/> を発火してはならない
    /// （「利用者が止めた」と「最後まで鳴り終わった」を呼び出し側が区別できなくなるため）。
    /// </para>
    /// </summary>
    void Stop();

    /// <summary>
    /// 最後まで鳴り終わった（または再生中に異常終了した）ときに発火する。
    /// 引数は鳴り終わったファイルの絶対パス。
    ///
    /// <para>
    /// <b>UI スレッドとは限らないスレッドから飛んでくる。</b>
    /// 購読側は UI へ触る前に必ずディスパッチャへ渡すこと。
    /// </para>
    /// </summary>
    event Action<string>? PlaybackEnded;
}
