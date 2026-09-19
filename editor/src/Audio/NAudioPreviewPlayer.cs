// ============================================================
//  NAudioPreviewPlayer.cs — 実デバイスへ音を出す試聴装置（NAudio 2.2.1）
//
//  【役割】
//  IAudioPreviewPlayer の実装。1 度に 1 ファイルだけを鳴らす。
//  状態遷移（どれが鳴っているか・ボタンの見た目）は AudioPreviewController の担当で、
//  ここは「開く・鳴らす・止める・後片付け」だけを行う。
//
//  【復号器の選び方】
//   1. AudioFileReader … WAV / MP3 / AIFF を NAudio 自身が読む。軽く、失敗が少ない。
//   2. MediaFoundationReader … 1 で開けなかった形式を Windows の復号器へ回す
//      （FLAC は Windows 10 以降が読む）。使う前に Media Foundation の初期化が要る。
//   どちらでも開けない形式（標準では .ogg）はここへ来る前に AudioPreviewSupport が弾く。
//
//  【例外を投げない】
//  出力デバイスが無い・ファイルが壊れている・対応していないサンプルレート…
//  いずれも起こりうる。試聴の失敗でエディタが落ちてはならないので、
//  すべて捕まえて AudioPreviewStartResult として返す。
//
//  【なぜ WaveOut ではなく WaveOutEvent か】
//  WaveOut はウィンドウメッセージで再生位置を進めるため、
//  作成したスレッドにメッセージループが要る（UI スレッド以外から使うと鳴らない）。
//  WaveOutEvent は専用スレッドで回るのでその制約が無い。
//
//  【スレッド】
//  PlaybackStopped は、Play() を呼んだスレッドに同期コンテキストがあればそこへ、
//  無ければ再生スレッドから飛んでくる。どちらで来ても壊れないよう、
//  状態の出し入れは _gate で守り、イベントの発火はロックの外で行う。
// ============================================================

using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SEEDEditor.Audio;

/// <summary>
/// NAudio で実デバイスへ音を出す試聴装置。
/// </summary>
public sealed class NAudioPreviewPlayer : IAudioPreviewPlayer
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>音量の下限。</summary>
    private const float MinVolume = 0f;

    /// <summary>音量の上限。</summary>
    private const float MaxVolume = 1f;

    /// <summary>ファイルを開けなかったときの文面（{0}=ファイル名 {1}=理由）。</summary>
    private const string OpenFailedFormat = "{0} を試聴できませんでした — {1}";

    /// <summary>出力デバイスを準備できなかったときの文面（{0}=理由）。</summary>
    private const string DeviceFailedFormat =
        "音声の出力デバイスを準備できませんでした — {0}。"
      + "既定の再生デバイスが有効か確認してください。";

    /// <summary>破棄後に呼ばれたときの文面。</summary>
    private const string DisposedMessage = "試聴装置は既に破棄されています。";

    // ── 状態 ─────────────────────────────────────────────────────

    /// <summary>状態一式の排他用ロック。</summary>
    private readonly object _gate = new();

    /// <summary>出力デバイス（鳴っていなければ null）。</summary>
    private WaveOutEvent? _output;

    /// <summary>復号器（鳴っていなければ null）。<see cref="_output"/> と寿命を合わせる。</summary>
    private WaveStream? _reader;

    /// <summary>いま鳴らしているファイルの絶対パス。</summary>
    private string? _currentPath;

    /// <summary>
    /// 自分から止めたか（＝利用者が停止ボタンを押した／切り替えた）。
    /// PlaybackStopped は「鳴り終わった」ときにも「止めた」ときにも飛んでくるので、
    /// この印で区別し、止めたときは <see cref="PlaybackEnded"/> を発火しない。
    /// </summary>
    private bool _stopRequested;

    /// <summary>破棄済みか。</summary>
    private bool _disposed;

    /// <inheritdoc/>
    public event Action<string>? PlaybackEnded;

    // ── 再生 ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public AudioPreviewStartResult Play(string path, float volume)
    {
        if (string.IsNullOrWhiteSpace(path))
            return AudioPreviewStartResult.Fail(string.Format(OpenFailedFormat, "(未指定)", "パスが空です"));

        var displayName = Path.GetFileName(path);

        lock (_gate)
        {
            if (_disposed) return AudioPreviewStartResult.Fail(DisposedMessage);

            // 前の再生を確実に畳んでから始める（同時に 2 つ鳴らさない）。
            StopLocked();

            WaveStream? reader = null;
            WaveOutEvent? output = null;
            try
            {
                // 復号器の選び方は波形サムネイルと共有する（AudioDecoding）。
                reader = AudioDecoding.OpenReader(path, out var sampleProvider);

                // 音量は復号器ごとの差を避けるため、共通のプロバイダで掛ける
                // （AudioFileReader.Volume は AudioFileReader にしか無い）。
                var volumeProvider = new VolumeSampleProvider(sampleProvider)
                {
                    Volume = Math.Clamp(volume, MinVolume, MaxVolume),
                };

                output = new WaveOutEvent();
                output.PlaybackStopped += OnPlaybackStopped;
                output.Init(volumeProvider);
                output.Play();
            }
            catch (Exception ex)
            {
                // 途中で失敗したら、その場で確保したものを閉じてから理由を返す。
                try { output?.Dispose(); } catch { /* 片付けの失敗は伝えない */ }
                try { reader?.Dispose(); } catch { /* 同上 */ }

                // 出力デバイスまで作れたかどうかで文面を変える（原因の切り分けのため）。
                return AudioPreviewStartResult.Fail(
                    reader is null
                        ? string.Format(OpenFailedFormat, displayName, ex.Message)
                        : string.Format(DeviceFailedFormat, ex.Message));
            }

            _reader        = reader;
            _output        = output;
            _currentPath   = path;
            _stopRequested = false;
            return AudioPreviewStartResult.Ok();
        }
    }

    /// <inheritdoc/>
    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopLocked();
        }
    }

    // ── 内部 ─────────────────────────────────────────────────────

    /// <summary>
    /// 再生を止めて後片付けする（<see cref="_gate"/> を保持した状態で呼ぶこと）。
    /// </summary>
    private void StopLocked()
    {
        if (_output is null && _reader is null) return;

        // 「自分から止めた」印を先に立てる。
        // これを Stop() より後に置くと、止めた直後に飛んできた PlaybackStopped を
        // 「鳴り終わった」と取り違える。
        _stopRequested = true;

        try { _output?.Stop(); } catch { /* 既に止まっている場合など */ }
        DisposeDeviceLocked();
        _currentPath = null;
    }

    /// <summary>出力デバイスと復号器を閉じる（<see cref="_gate"/> を保持した状態で呼ぶこと）。</summary>
    private void DisposeDeviceLocked()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try { _output.Dispose(); } catch { /* 片付けの失敗は握りつぶす */ }
            _output = null;
        }
        if (_reader is not null)
        {
            try { _reader.Dispose(); } catch { /* 同上 */ }
            _reader = null;
        }
    }

    /// <summary>
    /// 再生が止まったときの通知（鳴り終わり・停止・再生中の異常のいずれでも来る）。
    /// 「鳴り終わり」のときだけ <see cref="PlaybackEnded"/> を発火する。
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        string? endedPath = null;

        lock (_gate)
        {
            // 自分から止めた場合は、既に後片付け済みで通知も不要。
            if (!_stopRequested)
            {
                endedPath = _currentPath;
                DisposeDeviceLocked();
                _currentPath = null;
            }
        }

        // 購読側が何をするか分からないので、ロックの外で発火する
        //（購読側から Stop() を呼ばれても自己デッドロックしない）。
        if (endedPath is not null) PlaybackEnded?.Invoke(endedPath);
    }
}
