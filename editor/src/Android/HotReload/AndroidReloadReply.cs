// ============================================================
//  AndroidReloadReply.cs — 実行中の差し替えの命令と応答の照合（純粋な処理。docs/android.md §23）
//
//  【命令 → 応答の宛先】（書式の正典はランタイムの runtime/src/engine/core/app_base/hot_reload/wire.rs）
//    RELOAD_SCENE             → 宛先 scene
//    RELOAD_SCENE:{相対パス}   → 宛先 scene:{相対パス}
//    RELOAD_ASSET:{相対パス}   → 宛先 asset:{相対パス}
//    RELOAD_SCRIPTS           → 応答は SCRIPTS_RELOADED:{型数},{再生成数}（失敗は SCRIPTS_RELOADED:-1,{理由}）
//  応答 RELOAD_DONE:{宛先}|{所要ミリ秒}|{詳細} / RELOAD_SKIPPED:{宛先}|{理由} / RELOAD_FAILED:{宛先}|{理由}。
//  相対パスはランタイムが正規化した形（区切り /・assets:// と ./ を外す）で宛先に入るので、送る側も同じ形にしてから送る
//  （NormalizeRelative。ランタイムの normalize_relative と同じ規則）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SEEDEditor.Ipc;

namespace SEEDEditor.Android.HotReload;

/// <summary>差し替えの応答の結果。</summary>
public enum AndroidReloadOutcome
{
    /// <summary>適用した（RELOAD_DONE / SCRIPTS_RELOADED の型数が 0 以上）。</summary>
    Done,

    /// <summary>適用しなかった（RELOAD_SKIPPED。失敗ではない）。</summary>
    Skipped,

    /// <summary>失敗した（RELOAD_FAILED / SCRIPTS_RELOADED:-1）。</summary>
    Failed,

    /// <summary>時間内に応答が無かった・通信路が切れた（エディタ側で決める）。</summary>
    NoReply,
}

/// <summary>差し替えの応答 1 件。</summary>
/// <param name="Target">宛先（scene / scene:{相対パス} / asset:{相対パス} / scripts）。</param>
/// <param name="Outcome">結果。</param>
/// <param name="ElapsedMs">端末での所要時間（ミリ秒。応答に無ければ null）。</param>
/// <param name="Detail">詳細（適用: 読み直したシーン・inplace 等／スキップ・失敗: 理由）。</param>
public sealed record AndroidReloadReply(string Target, AndroidReloadOutcome Outcome, double? ElapsedMs, string Detail);

/// <summary>差し替えの命令と応答の照合。</summary>
public static class AndroidReloadReplies
{
    /// <summary>スクリプトの読み直しの応答の宛先（エディタ側だけの名前。ランタイムは SCRIPTS_RELOADED で返す）。</summary>
    public const string ScriptsTarget = "scripts";

    /// <summary>応答の詳細: キャッシュを捨てるだけで済んだ（ランタイムの DETAIL_IN_PLACE）。</summary>
    public const string InPlaceDetail = "inplace";

    /// <summary>応答の詳細の接頭辞: シーンを読み直した（scene:{読み直したシーン}。ランタイムの DETAIL_SCENE_PREFIX）。</summary>
    public const string SceneDetailPrefix = "scene:";

    /// <summary>仮想パスのスキーム。</summary>
    private const string AssetsScheme = "assets://";

    /// <summary>相対パスの区切り。</summary>
    private const char PathSeparator = '/';

    /// <summary>親フォルダ（アセットルートの外へ出るので使えない）。</summary>
    private const string ParentSegment = "..";

    /// <summary>今のフォルダ（読み飛ばす）。</summary>
    private const string CurrentSegment = ".";

    /// <summary>スクリプトの読み直しが成功したときの型数の下限（負値は失敗。ランタイムの RELOAD_FAILED_COUNT = -1）。</summary>
    private const int MinSucceededCount = 0;

    /// <summary>
    /// 相対パスをランタイムと同じ形にする（区切り /・assets:// と ./ を外す。純粋な処理）。
    /// </summary>
    /// <param name="path">アセットルートからの相対パス（区切りは / か \・assets:// 付きでもよい）。</param>
    /// <returns>正規化した相対パス。空・絶対パス・.. を含むときは null。</returns>
    public static string? NormalizeRelative(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(AssetsScheme, StringComparison.Ordinal)) trimmed = trimmed[AssetsScheme.Length..];
        var unified = trimmed.Replace('\\', PathSeparator);
        if (unified.Length == 0 || unified.StartsWith(PathSeparator) || unified.Contains(':')) return null;
        var segments = new List<string>();
        foreach (var segment in unified.Split(PathSeparator))
        {
            if (segment.Length == 0 || segment == CurrentSegment) continue;
            if (segment == ParentSegment) return null;
            segments.Add(segment);
        }
        return segments.Count == 0 ? null : string.Join(PathSeparator, segments);
    }

    /// <summary>
    /// 命令の応答の宛先（どの応答を待てばよいか。純粋な処理）。
    /// </summary>
    /// <param name="command">送る命令（1 行）。</param>
    /// <returns>宛先（差し替えの命令でなければ null）。</returns>
    public static string? TargetOf(string command)
    {
        if (command == RuntimeIpcCommands.ReloadScripts) return ScriptsTarget;
        if (command == RuntimeIpcCommands.ReloadScene) return RuntimeIpcCommands.ReloadSceneTarget;
        if (command.StartsWith(RuntimeIpcCommands.ReloadScenePrefix, StringComparison.Ordinal))
        {
            return RuntimeIpcCommands.ReloadSceneTargetPrefix + command[RuntimeIpcCommands.ReloadScenePrefix.Length..];
        }
        if (command.StartsWith(RuntimeIpcCommands.ReloadAssetPrefix, StringComparison.Ordinal))
        {
            return RuntimeIpcCommands.ReloadAssetTargetPrefix + command[RuntimeIpcCommands.ReloadAssetPrefix.Length..];
        }
        return null;
    }

    /// <summary>
    /// ランタイムから届いた 1 行を差し替えの応答として読む（純粋な処理）。
    /// </summary>
    /// <param name="line">届いた行。</param>
    /// <param name="reply">読めた応答。</param>
    /// <returns>差し替えの応答なら true。</returns>
    public static bool TryParse(string line, out AndroidReloadReply reply)
    {
        reply = null!;
        if (line.StartsWith(RuntimeIpcCommands.ScriptsReloadedPrefix, StringComparison.Ordinal))
        {
            reply = ParseScriptsReloaded(line[RuntimeIpcCommands.ScriptsReloadedPrefix.Length..]);
            return true;
        }
        var (prefix, outcome) = new[]
        {
            (RuntimeIpcCommands.ReloadDonePrefix, AndroidReloadOutcome.Done),
            (RuntimeIpcCommands.ReloadSkippedPrefix, AndroidReloadOutcome.Skipped),
            (RuntimeIpcCommands.ReloadFailedPrefix, AndroidReloadOutcome.Failed),
        }.FirstOrDefault(candidate => line.StartsWith(candidate.Item1, StringComparison.Ordinal));
        if (prefix is null) return false;

        var fields = line[prefix.Length..].Split(RuntimeIpcCommands.ReloadFieldSeparator);
        var target = fields[0];
        if (outcome == AndroidReloadOutcome.Done)
        {
            double? elapsed = fields.Length > 1
                && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : null;
            reply = new AndroidReloadReply(target, outcome, elapsed, fields.Length > 2 ? fields[2] : string.Empty);
        }
        else
        {
            reply = new AndroidReloadReply(target, outcome, null, fields.Length > 1 ? fields[1] : string.Empty);
        }
        return true;
    }

    /// <summary>SCRIPTS_RELOADED の本文（{型数},{再生成数} か -1,{理由}）を読む。</summary>
    /// <param name="payload">接頭辞の後ろ。</param>
    /// <returns>応答。</returns>
    private static AndroidReloadReply ParseScriptsReloaded(string payload)
    {
        var comma = payload.IndexOf(RuntimeIpcCommands.ArgumentSeparator);
        var head = comma < 0 ? payload : payload[..comma];
        var tail = comma < 0 ? string.Empty : payload[(comma + 1)..];
        if (!int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < MinSucceededCount)
        {
            return new AndroidReloadReply(ScriptsTarget, AndroidReloadOutcome.Failed, null, tail);
        }
        return new AndroidReloadReply(ScriptsTarget, AndroidReloadOutcome.Done, null,
            string.Format(CultureInfo.InvariantCulture, "{0} 型・再生成 {1} 件", count, tail));
    }
}
