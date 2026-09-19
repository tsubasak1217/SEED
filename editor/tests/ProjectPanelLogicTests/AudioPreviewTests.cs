using System;
using System.Collections.Generic;
using System.IO;
using ProjectSystemTests;   // TempDir
using SEEDEditor.Audio;
using SpriteRigTests;       // TestHarness / Check

namespace ProjectPanelLogicTests;

/// <summary>
/// 音声の試聴（プロジェクトパネルのタイルの再生ボタン）の状態遷移テスト。
///
/// <para>
/// 実デバイスへは一切出力しない。<see cref="IAudioPreviewPlayer"/> の偽物を差し込み、
/// 「どのファイルを鳴らしている状態か」という遷移だけを検証する
/// ＝自動テストで音が鳴らない、環境（出力デバイスの有無）に左右されない。
/// </para>
///
/// <para>
/// 実際に音が出せるか（NAudio が形式を開けるか）はここでは見ない。
/// それは環境依存なので、失敗したときにトーストとログで伝える設計にしてある。
/// </para>
/// </summary>
public static class AudioPreviewTests
{
    /// <summary>このファイルのテストをランナーへ登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("試聴: 押すと鳴り始める",                     PlayStarts);
        harness.Add("試聴: 同じファイルをもう一度押すと止まる",   ToggleStops);
        harness.Add("試聴: 別のファイルを押すと前のが止まる",     SwitchStopsPrevious);
        harness.Add("試聴: 鳴り終わると停止状態へ戻る",           EndedResets);
        harness.Add("試聴: 遅れて来た鳴り終わりは無視される",     StaleEndedIgnored);
        harness.Add("試聴: 自分で止めた場合は終了通知を出さない", StopDoesNotNotifyEnded);
        harness.Add("試聴: 開始に失敗したら停止状態のまま",       FailedStartKeepsStopped);
        harness.Add("試聴: 存在しないファイルは理由を返す",       MissingFileReported);
        harness.Add("試聴: 復号できない形式は理由を返す",         UnsupportedFormatReported);
        harness.Add("試聴: 大文字小文字の違うパスは同一視する",   PathCaseInsensitive);
        harness.Add("試聴: 破棄すると止まり以後は何もしない",     DisposeStops);
        harness.Add("試聴: 状態変化の通知は変わったときだけ",     ChangeNotifiedOnce);
        harness.Add("試聴: 試せる形式の対応表",                   SupportTable);
    }

    // ── 偽の再生装置 ────────────────────────────────────────────

    /// <summary>
    /// 実デバイスの代わりに、呼ばれた操作を記録するだけの再生装置。
    /// 「鳴り終わり」は <see cref="RaiseEnded"/> で好きなタイミングに起こせる。
    /// </summary>
    private sealed class FakePlayer : IAudioPreviewPlayer
    {
        /// <summary>Play が呼ばれたパスの記録（呼ばれた順）。</summary>
        public List<string> PlayCalls { get; } = new();

        /// <summary>Stop が呼ばれた回数。</summary>
        public int StopCalls { get; private set; }

        /// <summary>Dispose が呼ばれた回数。</summary>
        public int DisposeCalls { get; private set; }

        /// <summary>次の Play をこの理由で失敗させる（null なら成功）。</summary>
        public string? FailWith { get; set; }

        /// <summary>直近に渡された音量。</summary>
        public float LastVolume { get; private set; }

        /// <inheritdoc/>
        public event Action<string>? PlaybackEnded;

        /// <inheritdoc/>
        public AudioPreviewStartResult Play(string path, float volume)
        {
            LastVolume = volume;
            if (FailWith is not null) return AudioPreviewStartResult.Fail(FailWith);
            PlayCalls.Add(path);
            return AudioPreviewStartResult.Ok();
        }

        /// <inheritdoc/>
        public void Stop() => StopCalls++;

        /// <inheritdoc/>
        public void Dispose() => DisposeCalls++;

        /// <summary>「最後まで鳴り終わった」を起こす。</summary>
        /// <param name="path">鳴り終わったファイル。</param>
        public void RaiseEnded(string path) => PlaybackEnded?.Invoke(path);
    }

    /// <summary>テスト用に、中身の無い音声ファイルを作る（存在確認しか使わないため）。</summary>
    /// <param name="temp">一時フォルダ。</param>
    /// <param name="name">ファイル名。</param>
    /// <returns>作ったファイルの絶対パス。</returns>
    private static string MakeAudioFile(TempDir temp, string name)
    {
        var path = temp.Combine(name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    // ── 遷移 ────────────────────────────────────────────────────

    private static void PlayStarts()
    {
        using var temp = new TempDir();
        var file = MakeAudioFile(temp, "se.wav");
        var fake = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        Check.Equal(null, controller.Toggle(file), "失敗理由は無い");
        Check.Equal(file, controller.PlayingPath,  "鳴っているファイル");
        Check.Equal(1, fake.PlayCalls.Count,       "装置へ 1 回だけ依頼した");
        Check.True(controller.IsPlaying(file),     "そのファイルが鳴っている");
        Check.Close(AudioPreviewController.PreviewVolume, fake.LastVolume, 1e-6, "音量は控えめな定数");
    }

    private static void ToggleStops()
    {
        using var temp = new TempDir();
        var file = MakeAudioFile(temp, "se.wav");
        var fake = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        controller.Toggle(file);
        Check.Equal(null, controller.Toggle(file), "2 回目も失敗理由は無い");
        Check.Equal(null, controller.PlayingPath,  "止まっている");
        Check.Equal(1, fake.PlayCalls.Count,       "再生は 1 回だけ");
        Check.True(fake.StopCalls >= 1,            "装置へ停止を依頼した");
    }

    private static void SwitchStopsPrevious()
    {
        using var temp = new TempDir();
        var first  = MakeAudioFile(temp, "a.wav");
        var second = MakeAudioFile(temp, "b.mp3");
        var fake   = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        controller.Toggle(first);
        controller.Toggle(second);

        Check.Equal(second, controller.PlayingPath, "後から押した方が鳴る");
        Check.True(!controller.IsPlaying(first),    "前のは止まっている");
        Check.Equal(2, fake.PlayCalls.Count,        "2 回再生した");
        Check.True(fake.StopCalls >= 1,             "切り替えで停止を挟んだ");
    }

    private static void EndedResets()
    {
        using var temp = new TempDir();
        var file = MakeAudioFile(temp, "se.wav");
        var fake = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        controller.Toggle(file);
        fake.RaiseEnded(file);

        Check.Equal(null, controller.PlayingPath, "鳴り終わったら停止状態へ戻る");
        Check.True(!controller.IsPlaying(file),   "ボタンは再生アイコンへ戻せる");
    }

    private static void StaleEndedIgnored()
    {
        using var temp = new TempDir();
        var first  = MakeAudioFile(temp, "a.wav");
        var second = MakeAudioFile(temp, "b.wav");
        var fake   = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        controller.Toggle(first);
        controller.Toggle(second);

        // 切り替え前のファイルの「鳴り終わり」が遅れて届いた場合。
        // これで状態を畳むと、鳴っている最中の b のボタンが再生アイコンへ戻ってしまう。
        fake.RaiseEnded(first);

        Check.Equal(second, controller.PlayingPath, "今鳴っている方は影響を受けない");
    }

    private static void StopDoesNotNotifyEnded()
    {
        using var temp = new TempDir();
        var file = MakeAudioFile(temp, "se.wav");
        var fake = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        int notifications = 0;
        controller.PlayingPathChanged += _ => notifications++;

        controller.Toggle(file);   // 1 回目: null -> file
        controller.Stop();         // 2 回目: file -> null
        controller.Stop();         // 変化しないので通知は増えない

        Check.Equal(2, notifications, "通知は状態が変わったときだけ");
        Check.Equal(null, controller.PlayingPath, "止まっている");
    }

    private static void FailedStartKeepsStopped()
    {
        using var temp = new TempDir();
        var file = MakeAudioFile(temp, "se.wav");
        var fake = new FakePlayer { FailWith = "出力デバイスがありません" };
        using var controller = new AudioPreviewController(fake);

        var error = controller.Toggle(file);

        Check.Equal("出力デバイスがありません", error, "理由がそのまま返る");
        // ここが null でないと、鳴っていないのにボタンが停止アイコンのまま固まる。
        Check.Equal(null, controller.PlayingPath, "失敗したら停止状態のまま");
    }

    private static void MissingFileReported()
    {
        using var temp = new TempDir();
        var missing = temp.Combine("no_such.wav");
        var fake    = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        var error = controller.Toggle(missing);

        Check.True(error != null,                 "理由が返る");
        Check.Equal(null, controller.PlayingPath, "鳴らない");
        Check.Equal(0, fake.PlayCalls.Count,      "装置まで行かない");
    }

    private static void UnsupportedFormatReported()
    {
        using var temp = new TempDir();
        var ogg  = MakeAudioFile(temp, "bgm.ogg");
        var fake = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        var error = controller.Toggle(ogg);

        Check.True(error != null,            "理由が返る");
        Check.Equal(0, fake.PlayCalls.Count, "復号器まで行かせない");
    }

    private static void PathCaseInsensitive()
    {
        using var temp = new TempDir();
        var file = MakeAudioFile(temp, "SE.wav");
        var fake = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        controller.Toggle(file);

        // Windows のパスは大小を区別しない。別表記で押してもトグルが効くこと。
        Check.True(controller.IsPlaying(file.ToLowerInvariant()), "小文字表記でも同じファイル");
        Check.Equal(null, controller.Toggle(file.ToLowerInvariant()), "トグルで止まる");
        Check.Equal(null, controller.PlayingPath, "止まっている");
    }

    private static void DisposeStops()
    {
        using var temp = new TempDir();
        var file = MakeAudioFile(temp, "se.wav");
        var fake = new FakePlayer();
        var controller = new AudioPreviewController(fake);

        controller.Toggle(file);
        controller.Dispose();

        Check.Equal(1, fake.DisposeCalls,         "装置も破棄される");
        Check.Equal(null, controller.PlayingPath, "状態は畳まれる");

        // 破棄後の操作は黙って無視する（終了処理の順番に依存して落ちないように）。
        Check.Equal(null, controller.Toggle(file), "破棄後の再生は無視される");
        controller.Stop();
        controller.Dispose();
        Check.Equal(1, fake.DisposeCalls, "二重破棄しない");
    }

    private static void ChangeNotifiedOnce()
    {
        using var temp = new TempDir();
        var first  = MakeAudioFile(temp, "a.wav");
        var second = MakeAudioFile(temp, "b.wav");
        var fake   = new FakePlayer();
        using var controller = new AudioPreviewController(fake);

        var seen = new List<string?>();
        controller.PlayingPathChanged += p => seen.Add(p);

        controller.Toggle(first);
        controller.Toggle(second);
        fake.RaiseEnded(second);

        Check.Equal(3, seen.Count,   "通知は 3 回（開始・切替・終了）");
        Check.Equal(first,  seen[0], "1 回目");
        Check.Equal(second, seen[1], "2 回目");
        Check.Equal(null,   seen[2], "3 回目");
    }

    // ── 対応表 ──────────────────────────────────────────────────

    private static void SupportTable()
    {
        Check.True(AudioPreviewSupport.CanAttempt(".wav"),  "wav は試せる");
        Check.True(AudioPreviewSupport.CanAttempt(".MP3"),  "大文字でも試せる");
        Check.True(AudioPreviewSupport.CanAttempt(".flac"), "flac は試す（Windows の復号器に任せる）");
        Check.True(!AudioPreviewSupport.CanAttempt(".ogg"), "ogg は標準の復号器が無いので試さない");
        Check.True(!AudioPreviewSupport.CanAttempt(".png"), "画像は対象外");
        Check.True(!AudioPreviewSupport.CanAttempt(null),   "null は対象外");

        Check.Equal(null, AudioPreviewSupport.UnsupportedReason(@"C:\a\se.wav"), "試せる形式に理由は無い");

        var oggReason = AudioPreviewSupport.UnsupportedReason(@"C:\a\bgm.ogg");
        Check.True(oggReason != null && oggReason.Contains(".ogg"), "ogg の理由に拡張子が入る");

        var notAudio = AudioPreviewSupport.UnsupportedReason(@"C:\a\note.txt");
        Check.True(notAudio != null, "音声でないものにも理由がある");
        Check.True(notAudio != oggReason, "音声でない場合と復号できない場合で文面が違う");
    }
}
