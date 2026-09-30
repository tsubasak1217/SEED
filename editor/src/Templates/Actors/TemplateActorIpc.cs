// ============================================================
//  TemplateActorIpc.cs — テンプレートアクタの追加命令（エディタ → ランタイム）
//
//  【ワイヤ形式】
//    ADD_TEMPLATE_ACTOR:{world_line},{parent_dfs|-1},{path}
//      world_line … 追加先の世界線（ルートへ入れるときに使う。ビューポートは 0）
//      parent_dfs … 親の DFS 番号（-1 ならルート）
//      path       … まっさらにした木の一時ファイルの絶対パス（TemplateActorStaging）。
//                   パスはカンマを含みうるので**必ず最後**に置く（ランタイムは先頭から 2 つだけ区切る）
//  ランタイム側の受け口は runtime/src/engine/core/app_base/ipc.rs（IpcCommand::AddTemplateActor）と
//  app/template_actor_ops.rs。Undo はランタイムが 1 操作として積む（2D/3D の追加と同じ）。
// ============================================================

using System;
using System.Globalization;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// テンプレートアクタの追加命令の文字列を組み立てる。状態を持たない。
/// </summary>
public static class TemplateActorIpc
{
    /// <summary>命令の接頭辞（ランタイムの ipc.rs と一致させる）。</summary>
    public const string AddCommandPrefix = "ADD_TEMPLATE_ACTOR:";

    /// <summary>ルートへ入れることを表す親の番号。</summary>
    public const int RootParentId = -1;

    /// <summary>欄の区切り。</summary>
    private const char FieldSeparator = ',';

    /// <summary>
    /// 追加命令を組み立てる。
    /// </summary>
    /// <param name="target">追加先。</param>
    /// <param name="stagedPath">まっさらにした木の一時ファイルの絶対パス。</param>
    /// <returns>ランタイムへ送る 1 行。</returns>
    /// <exception cref="ArgumentException">パスが空、または改行を含む（1 行の命令に載らない）とき。</exception>
    public static string BuildAddCommand(TemplateActorTarget target, string stagedPath)
    {
        if (string.IsNullOrWhiteSpace(stagedPath) || stagedPath.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("一時ファイルのパスが命令に載せられない形です", nameof(stagedPath));

        var parent = (target.ParentDfs ?? RootParentId).ToString(CultureInfo.InvariantCulture);
        var wl     = target.WorldLine.ToString(CultureInfo.InvariantCulture);
        return AddCommandPrefix + wl + FieldSeparator + parent + FieldSeparator + stagedPath;
    }
}
