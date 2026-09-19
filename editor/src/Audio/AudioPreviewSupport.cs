// ============================================================
//  AudioPreviewSupport.cs — 「試聴を試せる形式か」の対応表（純ロジック）
//
//  【役割】
//  プロジェクトパネルのタイルに出す試聴ボタンを、押せる状態にするか
//  無効にして理由を出すかを決める。
//
//  【なぜ事前に表で決めるのか】
//  実際に鳴らせるかは開いてみるまで分からないが、フォルダを開くたびに
//  全音声ファイルを開いて確かめると I/O が跳ね上がる（数十ファイルあると体感で分かる）。
//  そこで「まず開く価値があるか」を拡張子で判断し、実際の可否は押したときに確かめる。
//  押して失敗した場合はトーストとログで伝える（例外にしない）。
//
//  【.ogg を無効にする理由】
//  NAudio 2.2.1 の AudioFileReader は WAV / MP3 / AIFF を読む。
//  それで開けない形式は MediaFoundationReader（Windows の復号器）へ回すが、
//  Windows は標準で Vorbis(.ogg) の復号器を持たない。
//  ＝ 押しても必ず失敗するので、最初から無効にして理由を出す方が親切。
//  （利用者が別途コーデックを入れている場合も押せないままになる。
//    その割り切りは docs/editor_project_panel.md に明記する）
//
//  【WPF 非依存・NAudio 非依存】
//  単体テスト（editor/tests/ProjectPanelLogicTests）から直接リンクするため、
//  UI 型にも復号器にも依存しない。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;

namespace SEEDEditor.Audio;

/// <summary>
/// 試聴（プレビュー再生）を試せる音声形式の対応表。状態を持たない静的クラス。
/// </summary>
public static class AudioPreviewSupport
{
    // ── 定数 ─────────────────────────────────────────────────────

    /// <summary>
    /// 試聴を試せる拡張子。
    ///
    /// <para>
    /// <c>.wav</c>・<c>.mp3</c> は <c>AudioFileReader</c> が直接読む。
    /// <c>.flac</c> は Windows 10 以降の復号器（MediaFoundation）が読むため候補に入れる。
    /// 実際に開けなければ再生時に失敗として扱う。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> Playable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac",
    };

    /// <summary>試聴できない形式のツールチップ文面（{0}=拡張子）。</summary>
    private const string UnsupportedFormat =
        "{0} は Windows の標準の復号器で開けないため、ここでは試聴できません。"
      + "ダブルクリックすると既定のプレイヤーで開きます。";

    /// <summary>そもそも音声ではないときの文面。</summary>
    private const string NotAudioReason = "音声ファイルではありません。";

    // ── 判定 ─────────────────────────────────────────────────────

    /// <summary>
    /// この拡張子の試聴を試せるか（＝ボタンを押せる状態にするか）。
    /// </summary>
    /// <param name="extension">先頭ドット付きの拡張子。大文字小文字は問わない。</param>
    public static bool CanAttempt(string? extension)
        => !string.IsNullOrEmpty(extension) && Playable.Contains(extension);

    /// <summary>このファイルの試聴を試せるか（拡張子で判断する）。</summary>
    /// <param name="path">ファイルパス。</param>
    public static bool CanAttemptPath(string? path)
        => !string.IsNullOrEmpty(path) && CanAttempt(Path.GetExtension(path));

    /// <summary>
    /// 試聴できない理由（ツールチップに出す文面）を返す。
    /// 試聴できる形式なら <c>null</c>。
    /// </summary>
    /// <param name="path">ファイルパス。</param>
    public static string? UnsupportedReason(string? path)
    {
        if (string.IsNullOrEmpty(path)) return NotAudioReason;

        var ext = Path.GetExtension(path);
        if (CanAttempt(ext)) return null;

        // 音声として扱う形式（Assets/AssetPreviewKinds.cs が唯一の情報源）のうち、
        // 復号器が無いものだけが「無効なボタン」になる。
        return SEEDEditor.Assets.AssetPreviewKinds.IsAudioExtension(ext)
            ? string.Format(UnsupportedFormat, ext)
            : NotAudioReason;
    }

    /// <summary>試聴を試せる拡張子の一覧（診断・ドキュメント用）。</summary>
    public static IReadOnlyCollection<string> PlayableExtensions => new List<string>(Playable);
}
