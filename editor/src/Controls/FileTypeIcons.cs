using System;
using System.Collections.Generic;

namespace SEEDEditor.Controls;

/// <summary>
/// ファイル拡張子からアイコンキーを引く唯一の対応表。
///
/// プロジェクトパネルのタイル、新規作成ウィンドウ、参照フィールドなど
/// 「ファイルを視覚表現するすべての箇所」がここを参照する。
/// 対応表を 1 箇所へ集約しているので、新しいアセット形式を足すときは
/// <see cref="IconKeyByExtension"/> に 1 行足すだけでエディタ全体に反映される
/// （手順は .claude/rules/editor-icons.md を参照）。
///
/// フォールバックの仕様は 2 段:
///   1. 表に無い拡張子は必ず汎用ファイルアイコンになる（<see cref="GenericFileIconKey"/>）。
///   2. サムネイルを持てる形式（<see cref="SupportsThumbnail"/> が true）でも、
///      プレビューの生成前・生成中・生成失敗の間は形式アイコンを出したままにする。
///      呼び出し側は「まず形式アイコンを描き、プレビューが取れたときだけ差し替える」
///      という順序を守ること（ProjectPanel.BuildFileItem がその実装例）。
/// </summary>
internal static class FileTypeIcons
{
    /// <summary>未知の拡張子に使う汎用ファイルアイコン。</summary>
    public const string GenericFileIconKey = "Icon.File.Generic";

    /// <summary>中身のあるフォルダのアイコン。</summary>
    public const string FolderIconKey = "Icon.File.Folder";

    /// <summary>空フォルダのアイコン（中身ありと区別する）。</summary>
    public const string EmptyFolderIconKey = "Icon.File.FolderEmpty";

    /// <summary>
    /// サムネイル（実画像プレビュー）を生成できる拡張子。
    /// ここに含まれる形式だけ、形式アイコンが後から実画像へ差し替わる。
    /// 含まれない形式は常に形式アイコンのまま表示される。
    /// </summary>
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga", ".hdr", ".exr", ".webp",
    };

    /// <summary>拡張子（先頭ドット付き・小文字）-> アイコンキー。</summary>
    private static readonly Dictionary<string, string> IconKeyByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── SEED 固有アセット ──
        [".scene"]    = "Icon.File.Scene",
        [".actor"]    = "Icon.File.Actor",
        [".actor2d"]  = "Icon.File.Actor2D",
        [".inputmap"] = "Icon.File.InputMap",
        [".anim"]     = "Icon.File.Anim",
        [".mat"]      = "Icon.File.Material",
        [".postfx"]   = "Icon.File.PostFx",
        [".tvox"]     = "Icon.File.Terrain",

        // ── スクリプト / シェーダ ──
        [".cs"]       = "Icon.File.Script",
        [".wgsl"]     = "Icon.File.Shader",
        [".lua"]      = "Icon.File.ScriptGeneric",
        [".py"]       = "Icon.File.ScriptGeneric",
        [".rs"]       = "Icon.File.ScriptGeneric",

        // ── 3D モデル ──
        [".glb"]      = "Icon.File.Model",
        [".gltf"]     = "Icon.File.Model",
        [".obj"]      = "Icon.File.Model",
        [".fbx"]      = "Icon.File.Model",

        // ── 画像（サムネイル生成対象。生成前・失敗時はこのアイコンのまま）──
        [".png"]      = "Icon.File.Image",
        [".jpg"]      = "Icon.File.Image",
        [".jpeg"]     = "Icon.File.Image",
        [".bmp"]      = "Icon.File.Image",
        [".gif"]      = "Icon.File.Image",
        [".tga"]      = "Icon.File.Image",
        [".hdr"]      = "Icon.File.Image",
        [".exr"]      = "Icon.File.Image",
        [".webp"]     = "Icon.File.Image",

        // ── 音声 ──
        [".wav"]      = "Icon.File.Audio",
        [".ogg"]      = "Icon.File.Audio",
        [".mp3"]      = "Icon.File.Audio",
        [".flac"]     = "Icon.File.Audio",

        // ── データ / 設定 ──
        [".json"]     = "Icon.File.Json",
        [".toml"]     = "Icon.File.Config",
        [".yaml"]     = "Icon.File.Config",
        [".yml"]      = "Icon.File.Config",
        [".ini"]      = "Icon.File.Config",
        [".cfg"]      = "Icon.File.Config",
        [".lock"]     = "Icon.File.Config",

        // ── テキスト ──
        [".txt"]      = "Icon.File.Text",
        [".md"]       = "Icon.File.Text",
        [".log"]      = "Icon.File.Text",
    };

    /// <summary>
    /// 拡張子に対応するアイコンキーを返す。表に無い拡張子は必ず
    /// <see cref="GenericFileIconKey"/> にフォールバックする。
    /// </summary>
    /// <param name="extension">先頭ドット付きの拡張子（大文字小文字は問わない）。空文字も可。</param>
    public static string GetIconKey(string? extension)
    {
        if (string.IsNullOrEmpty(extension)) return GenericFileIconKey;
        return IconKeyByExtension.TryGetValue(extension, out var key) ? key : GenericFileIconKey;
    }

    /// <summary>
    /// フォルダのアイコンキーを返す。
    /// </summary>
    /// <param name="isEmpty">中身が 1 件も無いフォルダなら true。</param>
    public static string GetFolderIconKey(bool isEmpty)
        => isEmpty ? EmptyFolderIconKey : FolderIconKey;

    /// <summary>
    /// この拡張子が実画像サムネイルへ差し替え可能かどうかを返す。
    /// false の形式は常に形式アイコンで表示する。
    /// </summary>
    /// <param name="extension">先頭ドット付きの拡張子。</param>
    public static bool SupportsThumbnail(string? extension)
        => !string.IsNullOrEmpty(extension) && ThumbnailExtensions.Contains(extension);
}
