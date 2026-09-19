// ============================================================
//  AudioPreviewController.cs — 試聴の状態遷移（純ロジック）
//
//  【役割】
//  「いまどのファイルを鳴らしているか」を 1 か所で持ち、
//  再生・停止・鳴り終わりの遷移を決める。音は出さない（IAudioPreviewPlayer に任せる）。
//
//  【守る決まり】
//   1. 同時に鳴るのは 1 つだけ。別のファイルを再生したら前のは止まる。
//   2. 同じファイルの再生ボタンをもう一度押したら止まる（トグル）。
//   3. 最後まで鳴り終わったら「何も鳴っていない」状態へ戻る。
//   4. 停止・失敗・終了のいずれでも、状態変化は 1 本の通知
//      （PlayingPathChanged）にまとめる。UI はこれだけ見ていれば
//      すべてのボタンの見た目を正しく保てる。
//   5. 再生開始に失敗したら「鳴っていない」状態のままにする
//      （押しても鳴らないのにボタンが停止アイコンで固まる、を防ぐ）。
//
//  【スレッド】
//  IAudioPreviewPlayer.PlaybackEnded は UI 以外のスレッドから飛んでくる。
//  このクラスは UI に触れないので受けてよいが、PlayingPathChanged を購読する
//  UI 側がディスパッチャへ渡す責任を持つ（購読側のコメントに明記すること）。
//
//  【WPF 非依存・NAudio 非依存】
//  単体テスト（editor/tests/ProjectPanelLogicTests）が偽の再生装置を相手に
//  状態遷移だけを検証できるよう、UI 型にも復号器にも依存しない。
// ============================================================

using System;
using System.IO;

namespace SEEDEditor.Audio;

/// <summary>
/// 試聴の状態（どのファイルを鳴らしているか）を持つ制御役。
/// </summary>
public sealed class AudioPreviewController : IDisposable
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>
    /// 試聴の音量（0.0〜1.0）。
    ///
    /// <para>
    /// 素材の確認が目的なので控えめにする。効果音の中には 0dB 近くまで
    /// 振り切っているものがあり、等倍で鳴らすと驚くほど大きい。
    /// </para>
    /// </summary>
    public const float PreviewVolume = 0.5f;

    /// <summary>ファイルが見つからないときの文面（{0}=パス）。</summary>
    private const string MissingFileFormat = "音声ファイルが見つかりません: {0}";

    /// <summary>試聴できない形式を押されたときの予備の文面。</summary>
    private const string UnsupportedFallback = "この形式は試聴できません。";

    // ── 状態 ─────────────────────────────────────────────────────

    /// <summary>実際に音を出す装置（差し替え可能な境界）。</summary>
    private readonly IAudioPreviewPlayer _player;

    /// <summary>破棄済みか（破棄後の操作を黙って無視するため）。</summary>
    private bool _disposed;

    /// <summary>いま鳴らしているファイルの絶対パス。何も鳴っていなければ null。</summary>
    public string? PlayingPath { get; private set; }

    /// <summary>
    /// 鳴らしているファイルが変わったときに発火する（停止・鳴り終わりでは null になる）。
    ///
    /// <para>
    /// <b>UI スレッドとは限らない。</b> 購読側はディスパッチャへ渡すこと。
    /// </para>
    /// </summary>
    public event Action<string?>? PlayingPathChanged;

    // ── 生成・破棄 ───────────────────────────────────────────────

    /// <summary>
    /// 制御役を作る。
    /// </summary>
    /// <param name="player">実際に音を出す装置（テストでは偽物を渡す）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> が null。</exception>
    public AudioPreviewController(IAudioPreviewPlayer player)
    {
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _player.PlaybackEnded += OnPlaybackEnded;
    }

    /// <summary>再生を止め、装置を破棄する。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _player.PlaybackEnded -= OnPlaybackEnded;
        try { _player.Stop(); } catch { /* 破棄時の失敗は無視する */ }
        try { _player.Dispose(); } catch { /* 同上 */ }

        // 破棄後に UI が参照しないよう、状態だけは畳んでおく。
        PlayingPath = null;
    }

    // ── 操作 ─────────────────────────────────────────────────────

    /// <summary>
    /// 試聴ボタンが押されたときの処理（トグル）。
    ///
    /// <list type="bullet">
    ///   <item><description>同じファイルが鳴っていれば止める。</description></item>
    ///   <item><description>違うファイルが鳴っていれば、それを止めて新しい方を鳴らす。</description></item>
    ///   <item><description>何も鳴っていなければ鳴らす。</description></item>
    /// </list>
    /// </summary>
    /// <param name="path">音声ファイルの絶対パス。</param>
    /// <returns>
    /// 失敗した場合の理由（画面に出す）。成功・停止したときは <c>null</c>。
    /// </returns>
    public string? Toggle(string? path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return null;

        if (IsPlaying(path))
        {
            Stop();
            return null;
        }
        return Play(path!);
    }

    /// <summary>
    /// 指定ファイルを鳴らす（既に鳴っているものは止める）。
    /// </summary>
    /// <param name="path">音声ファイルの絶対パス。</param>
    /// <returns>失敗した場合の理由。成功したときは <c>null</c>。</returns>
    public string? Play(string? path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return null;

        // 形式の可否を先に見る。押せないはずのボタンが何かの拍子に押されても
        // 復号器まで行かせない（環境依存の例外を UI の奥で起こさないため）。
        if (!AudioPreviewSupport.CanAttemptPath(path))
            return AudioPreviewSupport.UnsupportedReason(path) ?? UnsupportedFallback;

        if (!File.Exists(path))
            return string.Format(MissingFileFormat, path);

        // 先に前の再生を止める。装置側の Play も内部で止めるが、
        // ここで状態を畳んでおかないと「開始に失敗したのに前のパスが残る」ことになる。
        StopInternal(notify: false);

        var result = _player.Play(path!, PreviewVolume);
        if (!result.Success)
        {
            // 失敗したら「何も鳴っていない」ままにする（前項 5）。
            SetPlayingPath(null);
            return result.ErrorMessage;
        }

        SetPlayingPath(path);
        return null;
    }

    /// <summary>
    /// 鳴っていれば止める。鳴っていなければ何もしない。
    /// フォルダ移動・パネル終了・エディタ終了・Play 開始から呼ぶ。
    /// </summary>
    public void Stop() => StopInternal(notify: true);

    /// <summary>
    /// 指定ファイルが今鳴っているか（ボタンの見た目を決めるのに使う）。
    /// </summary>
    /// <param name="path">判定するファイルの絶対パス。</param>
    public bool IsPlaying(string? path)
    {
        if (string.IsNullOrEmpty(path) || PlayingPath is null) return false;
        // Windows のパスは大文字小文字を区別しない。
        // 同じファイルを別表記で開いたときにトグルが効かないのを防ぐ。
        return string.Equals(PlayingPath, path, StringComparison.OrdinalIgnoreCase);
    }

    // ── 内部 ─────────────────────────────────────────────────────

    /// <summary>停止の実体。通知するかどうかを呼び出し側が選べる。</summary>
    /// <param name="notify">状態変化の通知を出すか。</param>
    private void StopInternal(bool notify)
    {
        if (PlayingPath is null) return;

        try { _player.Stop(); }
        catch { /* 止められなくても状態は畳む（鳴りっぱなしよりボタンのずれの方が軽い） */ }

        if (notify) SetPlayingPath(null);
        else        PlayingPath = null;
    }

    /// <summary>
    /// 装置から「鳴り終わった」と言われたときの処理。
    ///
    /// <para>
    /// 鳴り終わりの通知は遅れて届くことがある。既に別のファイルへ切り替わっていた場合に
    /// そのまま状態を畳むと、鳴っている最中のボタンが再生アイコンへ戻ってしまう。
    /// そのため「今鳴っているのが、終わったと言われたファイルのときだけ」畳む。
    /// </para>
    /// </summary>
    /// <param name="endedPath">鳴り終わったファイルの絶対パス。</param>
    private void OnPlaybackEnded(string endedPath)
    {
        if (_disposed) return;
        if (!IsPlaying(endedPath)) return;
        SetPlayingPath(null);
    }

    /// <summary>現在のパスを差し替え、変化があれば通知する。</summary>
    /// <param name="path">新しいパス（null = 何も鳴っていない）。</param>
    private void SetPlayingPath(string? path)
    {
        if (string.Equals(PlayingPath, path, StringComparison.OrdinalIgnoreCase)) return;

        var previous = PlayingPath;
        PlayingPath  = path;

        // 前に鳴っていたボタンと、今鳴り始めたボタンの両方を描き直させたいので、
        // 変化した事実だけを 1 回通知する（購読側が両方を更新する）。
        _ = previous;
        PlayingPathChanged?.Invoke(path);
    }
}
