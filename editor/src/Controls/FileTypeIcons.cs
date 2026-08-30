using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SEEDEditor.Controls;

/// <summary>
/// ファイル拡張子から表示アイコンを引く唯一の対応表。
///
/// プロジェクトパネルのタイル、新規作成ウィンドウ、参照フィールドなど
/// 「ファイルを視覚表現するすべての箇所」がここを参照する。
/// 対応表を 1 箇所へ集約しているので、新しいアセット形式を足すときは
/// <see cref="IconKeyByExtension"/> に 1 行足すだけでエディタ全体に反映される
/// （手順は .claude/rules/editor-icons.md を参照）。
///
/// アイコンの実体は 2 系統ある。どちらを使うかはこのクラスが一手に決め、
/// 呼び出し側は <see cref="GetImage"/> / <see cref="GetFolderImage"/> が返す
/// <see cref="ImageSource"/> をそのまま載せるだけでよい。
///   1. <b>既存の PNG アイコン</b>（<see cref="PngByExtension"/>）。
///      ユーザーが用意した editor/resources/icons/folderview/*.png。
///      従来から割り当てのあった形式はこちらを優先する。
///   2. <b>ベクターアイコン</b>（<see cref="IconKeyByExtension"/>）。
///      PNG の用意が無い後発の形式（音声・データ・テキスト等）に使う。
///
/// フォールバックの仕様は 2 段:
///   1. どちらの表にも無い拡張子は汎用アイコンになる
///      （画像 PNG = <see cref="LegacyFallbackPng"/>。従来の挙動を維持）。
///   2. サムネイルを持てる形式（<see cref="SupportsThumbnail"/> が true）でも、
///      プレビューの生成前・生成中・生成失敗の間は形式アイコンを出したままにする。
///      呼び出し側は「まず形式アイコンを描き、プレビューが取れたときだけ差し替える」
///      という順序を守ること（ProjectPanel.BuildFileItem がその実装例）。
/// </summary>
internal static class FileTypeIcons
{
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
        [".sprite_mesh"] = "Icon.File.SpriteMesh",

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
    /// この拡張子が実画像サムネイルへ差し替え可能かどうかを返す。
    /// false の形式は常に形式アイコンで表示する。
    /// </summary>
    /// <param name="extension">先頭ドット付きの拡張子。</param>
    public static bool SupportsThumbnail(string? extension)
        => !string.IsNullOrEmpty(extension) && ThumbnailExtensions.Contains(extension);

    // ── 既存 PNG アイコン（ユーザー資産）─────────────────────────────

    /// <summary>PNG アイコンを収めた pack URI のディレクトリ。</summary>
    private const string PngUriBase = "pack://application:,,,/resources/icons/folderview/";

    /// <summary>中身のあるフォルダの PNG。</summary>
    private const string FolderPng = "folder.png";

    /// <summary>空フォルダの PNG。</summary>
    private const string EmptyFolderPng = "folder_empty.png";

    /// <summary>どの表にも無い拡張子に使う汎用 PNG（従来の挙動を維持）。</summary>
    private const string LegacyFallbackPng = "image.png";

    /// <summary>
    /// 拡張子 -> PNG ファイル名。ここに載っている形式はベクターより優先される。
    ///
    /// 収録範囲は「ユーザーが自分で用意して従来から割り当てていた形式」に限る。
    /// PNG を持たない後発の形式はここへ足さず、<see cref="IconKeyByExtension"/>
    /// だけに足してベクターアイコンで表示する。
    /// </summary>
    private static readonly Dictionary<string, string> PngByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── SEED 固有アセット ──
        [".scene"]    = "scene.png",
        [".actor"]    = "actor.png",
        [".actor2d"]  = "actor2d.png",
        [".inputmap"] = "script.png",

        // ── スクリプト / シェーダ ──
        [".lua"]      = "script.png",
        [".cs"]       = "script.png",
        [".py"]       = "script.png",
        [".wgsl"]     = "script.png",

        // ── 3D モデル ──
        [".glb"]      = "model.png",
        [".gltf"]     = "model.png",
        [".obj"]      = "model.png",
        [".fbx"]      = "model.png",

        // ── 画像（サムネイル生成対象。生成前・失敗時はこの PNG のまま）──
        [".png"]      = "image.png",
        [".jpg"]      = "image.png",
        [".jpeg"]     = "image.png",
        [".bmp"]      = "image.png",
        [".gif"]      = "image.png",
        [".tga"]      = "image.png",
        [".hdr"]      = "image.png",
        [".exr"]      = "image.png",
        [".webp"]     = "image.png",
    };

    /// <summary>PNG ファイル名 -> 読み込み済み（Freeze 済み）ビットマップのキャッシュ。</summary>
    private static readonly Dictionary<string, ImageSource> PngCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// PNG を 1 度だけ読み込んで Freeze し、以降は使い回す。
    /// プロジェクトパネルは同じアイコンを何十個も並べるため、
    /// タイルごとに <see cref="BitmapImage"/> を作らないことが効く。
    /// </summary>
    /// <param name="fileName">folderview 配下の PNG ファイル名。</param>
    private static ImageSource LoadPng(string fileName)
    {
        if (PngCache.TryGetValue(fileName, out var cached)) return cached;

        var image = new BitmapImage(new Uri(PngUriBase + fileName, UriKind.Absolute));
        image.Freeze();
        PngCache[fileName] = image;
        return image;
    }

    // ── 表示用 ImageSource の取得（呼び出し側はこちらを使う）──────────

    /// <summary>
    /// 拡張子に対応する表示アイコンを返す。
    /// PNG の割り当てがあればそれを、無ければベクターアイコンを、
    /// どちらも無ければ汎用 PNG を返す。
    /// </summary>
    /// <param name="extension">先頭ドット付きの拡張子（大文字小文字は問わない）。空文字も可。</param>
    public static ImageSource GetImage(string? extension)
    {
        if (!string.IsNullOrEmpty(extension))
        {
            if (PngByExtension.TryGetValue(extension, out var png)) return LoadPng(png);
            if (IconKeyByExtension.TryGetValue(extension, out var key))
            {
                var vector = IconImages.Get(key);
                if (vector != null) return vector;
            }
        }
        return LoadPng(LegacyFallbackPng);
    }

    /// <summary>
    /// フォルダの表示アイコンを返す。
    /// </summary>
    /// <param name="isEmpty">中身が 1 件も無いフォルダなら true。</param>
    public static ImageSource GetFolderImage(bool isEmpty)
        => LoadPng(isEmpty ? EmptyFolderPng : FolderPng);
}
