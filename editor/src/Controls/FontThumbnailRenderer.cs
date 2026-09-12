using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SEEDEditor.Assets;

namespace SEEDEditor.Controls;

/// <summary>
/// フォントファイル（.ttf / .otf / .ttc）から「そのフォントで描いたサンプル文字列」の
/// サムネイル画像を作る。プロジェクトパネルのタイルが、どのフォントかを
/// ファイル名だけでなく見た目で判別できるようにするためのもの。
///
/// 作り方の要点:
///   1. <see cref="GlyphTypeface"/> としてフォントファイルを直接開く。
///      WPF のフォント検索（インストール済みフォント）を経由しないので、
///      プロジェクト内の未インストールフォントもそのまま描ける。
///      開けなかった場合だけ <see cref="Fonts.GetFontFamilies(Uri)"/> で
///      ファイル内のフェイス列挙を試す（.ttc コレクション対策）。
///   2. サンプル文字列は「そのフォントが実際に持っているグリフ」だけに絞る。
///      欧文専用フォントで "あ" を描こうとすると豆腐（.notdef）になるため、
///      cmap に無い文字は落とし、1 文字も残らなければサムネイル生成自体を諦める
///      （呼び出し側は従来どおり形式アイコンを出す）。
///   3. <see cref="DrawingVisual"/> へ <see cref="GlyphRun"/> を描き、
///      <see cref="RenderTargetBitmap"/> へ焼いて Freeze する。
///      Freeze 済みなので以降はどのタイルからでも共有できる。
///
/// スレッド: <see cref="GlyphRun"/> / <see cref="DrawingVisual"/> /
/// <see cref="RenderTargetBitmap"/> はいずれもスレッド親和性を持つため、
/// 生成は呼び出し元スレッド（＝UI スレッド）で完結させる。UI を固めないための
/// 「遅延」は呼び出し側が Dispatcher の優先度で行う（ProjectPanel.BuildFileItem 参照）。
/// 実処理は数 ms 程度で、同じフォントは 2 回目以降キャッシュから返る。
/// </summary>
internal static class FontThumbnailRenderer
{
    // ── 描画パラメータ（マジックナンバーはここへ集約）────────────

    /// <summary>生成するサムネイルの一辺（px）。タイル表示サイズより少し大きめに焼いて縮小耐性を持たせる。</summary>
    public const int DefaultThumbnailSize = 96;

    /// <summary>描画領域の内側に取る余白の割合（一辺に対する比率）。</summary>
    private const double PaddingRatio = 0.08;

    /// <summary>RenderTargetBitmap の DPI（画面等倍で焼く）。</summary>
    private const double RenderDpi = 96.0;

    /// <summary>GlyphRun に渡す pixelsPerDip（96 DPI 等倍なので 1.0）。</summary>
    private const float PixelsPerDip = 1.0f;

    /// <summary>グリフの塗り色（パネルのファイル名と同系の明るいグレー）。</summary>
    private static readonly Brush GlyphBrush = CreateFrozenBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));

    /// <summary>
    /// サンプル文字列の候補。先頭から順に「全文字のグリフを持っているか」を見て採用する。
    /// 全部そろう候補が無ければ、先頭候補から描ける文字だけを残して使う。
    /// 和文フォントなら "Aa あ"、欧文専用フォントなら "Aa" が選ばれる。
    /// </summary>
    private static readonly string[] SampleTextCandidates =
    {
        "Aa あ",
        "Aa",
        "AB",
        "123",
    };

    /// <summary>フォントが持たない文字を表すグリフ番号（.notdef）。</summary>
    private const ushort NotDefGlyphIndex = 0;

    /// <summary>フォント高さが取得できなかったときに使う既定値（em 単位の 1 行高）。</summary>
    private const double FallbackLineHeightEm = 1.2;

    // ── キャッシュ ────────────────────────────────────────────────

    /// <summary>キャッシュキー -> サムネイル（null = この版のフォントは描けなかった）。</summary>
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new();

    /// <summary>
    /// フォントファイルのサムネイルを得る（キャッシュ有り）。
    /// </summary>
    /// <param name="fontPath">フォントファイルの絶対パス。</param>
    /// <param name="thumbnailSize">生成する正方形サムネイルの一辺（px）。</param>
    /// <returns>
    /// Freeze 済みのサムネイル。読めないフォント・描けるグリフが無いフォントなら null
    /// （呼び出し側は形式アイコンのままにする）。
    /// </returns>
    public static BitmapSource? Render(string fontPath, int thumbnailSize = DefaultThumbnailSize)
    {
        var key = AssetPreviewCacheKey.BuildFromFile(fontPath, thumbnailSize.ToString());
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var bitmap = RenderCore(fontPath, thumbnailSize);
        Cache[key] = bitmap;
        return bitmap;
    }

    /// <summary>
    /// サムネイルを実際に生成する（キャッシュを通さない生の処理）。
    /// </summary>
    /// <param name="fontPath">フォントファイルの絶対パス。</param>
    /// <param name="thumbnailSize">正方形サムネイルの一辺（px）。</param>
    private static BitmapSource? RenderCore(string fontPath, int thumbnailSize)
    {
        if (!AssetPreviewKinds.SupportsFontThumbnail(System.IO.Path.GetExtension(fontPath))) return null;
        if (thumbnailSize <= 0) return null;

        var glyphTypeface = LoadGlyphTypeface(fontPath);
        if (glyphTypeface == null) return null;

        var glyphs = SelectSampleGlyphs(glyphTypeface);
        if (glyphs == null) return null;

        try
        {
            var run = BuildGlyphRun(glyphTypeface, glyphs, thumbnailSize);
            if (run == null) return null;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawGlyphRun(GlyphBrush, run);
            }

            var bitmap = new RenderTargetBitmap(
                thumbnailSize, thumbnailSize, RenderDpi, RenderDpi, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // 壊れたフォント・異常なメトリクスなど。形式アイコンへフォールバックさせる。
            return null;
        }
    }

    // ── フォント読み込み ──────────────────────────────────────────

    /// <summary>
    /// フォントファイルを <see cref="GlyphTypeface"/> として開く。
    ///
    /// まずファイルを直接指定して開き、それが失敗した場合だけ
    /// ファイル内のフェイス列挙（.ttc のようにファイル 1 つへ複数フォントが入る形式）を試す。
    /// </summary>
    /// <param name="fontPath">フォントファイルの絶対パス。</param>
    /// <returns>開けなければ null。</returns>
    private static GlyphTypeface? LoadGlyphTypeface(string fontPath)
    {
        Uri uri;
        try
        {
            uri = new Uri(fontPath, UriKind.Absolute);
        }
        catch
        {
            return null;
        }

        // 1) ファイルを直接開く（.ttf / .otf はここで通る）
        try
        {
            return new GlyphTypeface(uri);
        }
        catch
        {
            // 2) へ進む
        }

        // 2) ファイル内のフェイスを列挙して最初に開けたものを使う（.ttc 等）
        try
        {
            foreach (var family in Fonts.GetFontFamilies(uri))
            {
                foreach (var typeface in family.GetTypefaces())
                {
                    if (typeface.TryGetGlyphTypeface(out var glyphTypeface)) return glyphTypeface;
                }
            }
        }
        catch
        {
            // フォントとして解釈できないファイル（拡張子だけ .ttf の別物など）。
        }

        return null;
    }

    // ── サンプル文字列の決定 ──────────────────────────────────────

    /// <summary>
    /// サンプル文字列の候補から、このフォントで実際に描ける並びを選ぶ。
    /// </summary>
    /// <param name="glyphTypeface">対象フォント。</param>
    /// <returns>描くグリフ番号の並び。1 文字も描けなければ null。</returns>
    private static IReadOnlyList<ushort>? SelectSampleGlyphs(GlyphTypeface glyphTypeface)
    {
        IReadOnlyList<ushort>? partialBest = null;

        foreach (var candidate in SampleTextCandidates)
        {
            var mapped   = new List<ushort>(candidate.Length);
            bool complete = true;

            foreach (var ch in candidate)
            {
                if (glyphTypeface.CharacterToGlyphMap.TryGetValue(ch, out var index)
                    && index != NotDefGlyphIndex)
                {
                    mapped.Add(index);
                }
                else
                {
                    complete = false;
                }
            }

            // 全文字そろった候補が最優先（見た目が意図どおりになる）
            if (complete && mapped.Count > 0) return mapped;

            // 部分的にしか描けない候補は、最初に見つかったものを控えにしておく
            if (partialBest == null && mapped.Count > 0) partialBest = mapped;
        }

        return partialBest;
    }

    // ── グリフ列の配置 ────────────────────────────────────────────

    /// <summary>
    /// グリフ列を、指定サイズの正方形へ収まるよう中央寄せで配置した
    /// <see cref="GlyphRun"/> を組み立てる。
    ///
    /// 文字サイズは「横幅で決まる上限」と「行高で決まる上限」の小さい方を採るので、
    /// 横長のサンプルでも縦に大きいフォントでもはみ出さない。
    /// </summary>
    /// <param name="glyphTypeface">対象フォント。</param>
    /// <param name="glyphIndices">描くグリフ番号の並び。</param>
    /// <param name="boxSize">描画先の正方形の一辺（px）。</param>
    /// <returns>組み立てた GlyphRun。メトリクスが異常で配置できなければ null。</returns>
    private static GlyphRun? BuildGlyphRun(
        GlyphTypeface glyphTypeface, IReadOnlyList<ushort> glyphIndices, int boxSize)
    {
        double available = boxSize * (1.0 - PaddingRatio * 2.0);
        if (available <= 0) return null;

        // em 単位（フォントサイズ 1.0 のときの寸法）で並びの幅と行高を測る
        double totalAdvanceEm = 0;
        foreach (var index in glyphIndices) totalAdvanceEm += glyphTypeface.AdvanceWidths[index];
        if (totalAdvanceEm <= 0) return null;

        double lineHeightEm = glyphTypeface.Height > 0 ? glyphTypeface.Height : FallbackLineHeightEm;

        // 横・縦のどちらでもはみ出さない文字サイズ
        double emSize = Math.Min(available / totalAdvanceEm, available / lineHeightEm);
        if (emSize <= 0 || double.IsNaN(emSize) || double.IsInfinity(emSize)) return null;

        // 実寸へ直してから中央寄せ（y はベースライン位置なので上端 + Baseline）
        double textWidth  = totalAdvanceEm * emSize;
        double textHeight = lineHeightEm   * emSize;
        double left       = (boxSize - textWidth)  / 2.0;
        double top        = (boxSize - textHeight) / 2.0;
        var    origin     = new Point(left, top + glyphTypeface.Baseline * emSize);

        var advances = new List<double>(glyphIndices.Count);
        foreach (var index in glyphIndices) advances.Add(glyphTypeface.AdvanceWidths[index] * emSize);

        return new GlyphRun(
            glyphTypeface:   glyphTypeface,
            bidiLevel:       0,
            isSideways:      false,
            renderingEmSize: emSize,
            pixelsPerDip:    PixelsPerDip,
            glyphIndices:    new List<ushort>(glyphIndices),
            baselineOrigin:  origin,
            advanceWidths:   advances,
            glyphOffsets:    null,
            characters:      null,
            deviceFontName:  null,
            clusterMap:      null,
            caretStops:      null,
            language:        null);
    }

    /// <summary>Freeze 済みの単色ブラシを作る（生成のたびに新しいブラシを作らないため）。</summary>
    /// <param name="color">塗り色。</param>
    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
