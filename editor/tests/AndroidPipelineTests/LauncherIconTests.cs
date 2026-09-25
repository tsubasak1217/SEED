using System.Buffers.Binary;
using System.IO.Compression;
using System.Linq;
using System.Text;
using SEEDEditor.Android.Icons;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.ProjectSettings;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// ランチャーのアイコンの生成（段階D。docs/android.md §24）: PNG の読み書き（全部の色の種類・ビットの深さ・フィルタ・インターレース）、
/// 縮小と拡大、各密度の寸法とアダプティブアイコンの安全域、置き場への書き方（中身が同じなら書かない）、設定の検査。
/// </summary>
public static class LauncherIconTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("PNG: 書いて読むと画素が一致する（行ごとのフィルタの選び方・同じ画像から同じバイト列）", PngRoundTrip);
        harness.Add("PNG: 5 種類のフィルタ（なし・左・上・平均・Paeth）を戻せる", PngFilters);
        harness.Add("PNG: パレット 2 bit ＋ tRNS・灰色 1 bit・灰色 8 bit の透明色・RGBA 16 bit・灰色＋アルファ", PngColorTypes);
        harness.Add("PNG: Adam7 インターレースの 7 回の走査を元の位置へ戻す", PngInterlaced);
        harness.Add("PNG: 壊れたファイル（署名なし・途中で切れた・大きすぎる）は理由付きの PngFormatException", PngRejectsBroken);
        harness.Add("縮小と拡大: 面積平均（市松模様は灰色）・乗算済みアルファ（透明の色が縁に滲まない）・縦横比を保って収める", Resampling);
        harness.Add("アイコンの生成物: 各密度の寸法（従来型 48dp・前景 108dp）・前景は安全域 66dp に収まる・背景色の XML", GeneratedDimensions);
        harness.Add("アイコンの置き場: 中身が同じなら書かない・古い生成物を消す・設定が無ければ置き場ごと消す", StagerWritesOnlyChanges);
        harness.Add("アイコンの設定: 相対パスと assets://・PNG でない・無いファイル・色の書式（#RRGGBB / #AARRGGBB）", IconSettings);
    }

    // ── テスト用の PNG の組み立て（仕様どおりの最小の書き方。PngDecoder を独立に確かめるため自前で書く）──

    /// <summary>チャンクを書く。</summary>
    private static void Chunk(MemoryStream stream, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        stream.Write(typeBytes);
        stream.Write(data);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, PngFormat.Crc(typeBytes, data));
        stream.Write(crc);
    }

    /// <summary>フィルタの 1 バイトを含む行の並び（raw）から PNG を作る。</summary>
    private static byte[] MakePng(int width, int height, byte bitDepth, byte colorType, byte[] raw,
        byte[]? palette = null, byte[]? transparency = null, byte interlace = PngFormat.InterlaceNone)
    {
        using var stream = new MemoryStream();
        stream.Write(PngFormat.Signature);
        var header = new byte[PngFormat.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[PngFormat.HeaderBitDepthOffset] = bitDepth;
        header[PngFormat.HeaderColorTypeOffset] = colorType;
        header[PngFormat.HeaderInterlaceOffset] = interlace;
        Chunk(stream, "IHDR", header);
        if (palette is not null) Chunk(stream, "PLTE", palette);
        if (transparency is not null) Chunk(stream, "tRNS", transparency);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) zlib.Write(raw);
        var bytes = compressed.ToArray();
        // IDAT を 2 つに分けて入れる（複数の IDAT をつなげて読むことも確かめる）
        var half = bytes.Length / 2;
        Chunk(stream, "IDAT", bytes[..half]);
        Chunk(stream, "IDAT", bytes[half..]);
        Chunk(stream, "IEND", System.Array.Empty<byte>());
        return stream.ToArray();
    }

    /// <summary>テスト用の模様の画像（場所ごとに違う色・半透明を含む）。</summary>
    private static RgbaImage Pattern(int width, int height)
    {
        var image = new RgbaImage(width, height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                image.Pixels[i] = (byte)(x * 37 + y * 11);
                image.Pixels[i + 1] = (byte)(x * 5 + y * 71);
                image.Pixels[i + 2] = (byte)((x ^ y) * 13);
                image.Pixels[i + 3] = (byte)(255 - (x * y) % 256);
            }
        }
        return image;
    }

    /// <summary>書いて読む。</summary>
    private static void PngRoundTrip()
    {
        var image = Pattern(37, 23);
        var bytes = PngEncoder.Encode(image);
        var decoded = PngDecoder.Decode(bytes);
        Check.Equal(37, decoded.Width, "幅");
        Check.Equal(23, decoded.Height, "高さ");
        Check.True(decoded.Pixels.SequenceEqual(image.Pixels), "画素が一致");
        Check.True(PngEncoder.Encode(image).SequenceEqual(bytes), "同じ画像から同じバイト列（中身が同じなら書かない前提）");
    }

    /// <summary>フィルタ（テストの中で仕様の式どおりにかけ、デコーダで戻す）。</summary>
    private static void PngFilters()
    {
        const int width = 5, height = 4, bpp = 3;
        var random = new System.Random(1234);
        var pixels = new byte[width * height * bpp];
        random.NextBytes(pixels);
        for (byte filter = 0; filter <= 4; filter++)
        {
            var raw = new List<byte>();
            var previous = new byte[width * bpp];
            for (var y = 0; y < height; y++)
            {
                var row = pixels.AsSpan(y * width * bpp, width * bpp).ToArray();
                raw.Add(filter);
                for (var i = 0; i < row.Length; i++)
                {
                    var left = i >= bpp ? row[i - bpp] : (byte)0;
                    var up = previous[i];
                    var upLeft = i >= bpp ? previous[i - bpp] : (byte)0;
                    raw.Add(filter switch
                    {
                        1 => (byte)(row[i] - left),
                        2 => (byte)(row[i] - up),
                        3 => (byte)(row[i] - ((left + up) >> 1)),
                        4 => (byte)(row[i] - PngFormat.Paeth(left, up, upLeft)),
                        _ => row[i],
                    });
                }
                previous = row;
            }
            var decoded = PngDecoder.Decode(MakePng(width, height, 8, PngFormat.ColorTypeRgb, raw.ToArray()));
            for (var p = 0; p < width * height; p++)
            {
                Check.True(decoded.Pixels[p * 4] == pixels[p * 3] && decoded.Pixels[p * 4 + 1] == pixels[p * 3 + 1]
                           && decoded.Pixels[p * 4 + 2] == pixels[p * 3 + 2] && decoded.Pixels[p * 4 + 3] == 255,
                    $"フィルタ {filter}: 画素 {p} が一致");
            }
        }
    }

    /// <summary>色の種類とビットの深さ。</summary>
    private static void PngColorTypes()
    {
        // パレット 2 bit（3x2）: 0=赤 1=緑 2=青 3=白、tRNS は 3 項目（白は不透明のまま）
        var palette = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255 };
        var trns = new byte[] { 255, 128, 0 };
        var paletted = PngDecoder.Decode(MakePng(3, 2, 2, PngFormat.ColorTypePalette,
            new byte[] { 0, 0b00_01_10_00, 0, 0b11_00_01_00 }, palette, trns));
        Check.Equal(new RgbaColor(0, 255, 0, 128), paletted.GetPixel(1, 0), "パレット 1（緑・半透明）");
        Check.Equal(new RgbaColor(0, 0, 255, 0), paletted.GetPixel(2, 0), "パレット 2（青・透明）");
        Check.Equal(new RgbaColor(255, 255, 255, 255), paletted.GetPixel(0, 1), "tRNS の外は不透明");

        // 灰色 1 bit（10x1）: 1010101010
        var bits = PngDecoder.Decode(MakePng(10, 1, 1, PngFormat.ColorTypeGray, new byte[] { 0, 0xAA, 0x80 }));
        Check.Equal((byte)255, bits.GetPixel(0, 0).R, "1 は白");
        Check.Equal((byte)0, bits.GetPixel(1, 0).R, "0 は黒");
        Check.Equal((byte)255, bits.GetPixel(8, 0).R, "2 バイト目の最上位");
        Check.Equal((byte)0, bits.GetPixel(9, 0).R, "余りのビット");

        // 灰色 8 bit の透明色（tRNS は 16 bit の値）
        var keyed = PngDecoder.Decode(MakePng(2, 1, 8, PngFormat.ColorTypeGray, new byte[] { 0, 0x40, 0x80 }, transparency: new byte[] { 0, 0x80 }));
        Check.True(keyed.GetPixel(0, 0).A == 255 && keyed.GetPixel(1, 0).A == 0, "透明色の画素だけ透明");

        // RGBA 16 bit（上位 8 bit を使う）
        var deep = PngDecoder.Decode(MakePng(1, 1, 16, PngFormat.ColorTypeRgba, new byte[] { 0, 0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xFF, 0xFF }));
        Check.Equal(new RgbaColor(0x12, 0x56, 0x9A, 0xFF), deep.GetPixel(0, 0), "16 bit は上位 8 bit");

        // 灰色＋アルファ 8 bit
        var grayAlpha = PngDecoder.Decode(MakePng(1, 1, 8, PngFormat.ColorTypeGrayAlpha, new byte[] { 0, 0x33, 0x44 }));
        Check.Equal(new RgbaColor(0x33, 0x33, 0x33, 0x44), grayAlpha.GetPixel(0, 0), "灰色＋アルファ");
    }

    /// <summary>インターレース（3x3・灰色 8 bit。各画素の値は 10 × (x + 3y + 1)）。</summary>
    private static void PngInterlaced()
    {
        static byte Value(int x, int y) => (byte)(10 * (x + 3 * y + 1));
        // 3x3 の Adam7: 1 回目 (0,0)／4 回目 (2,0)／5 回目 (0,2)(2,2)／6 回目 (1,0)(1,2)／7 回目 (0,1)(1,1)(2,1)（2・3 回目は空）
        var raw = new List<byte>
        {
            0, Value(0, 0),
            0, Value(2, 0),
            0, Value(0, 2), Value(2, 2),
            0, Value(1, 0),
            0, Value(1, 2),
            0, Value(0, 1), Value(1, 1), Value(2, 1),
        };
        var decoded = PngDecoder.Decode(MakePng(3, 3, 8, PngFormat.ColorTypeGray, raw.ToArray(), interlace: PngFormat.InterlaceAdam7));
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++) Check.Equal(Value(x, y), decoded.GetPixel(x, y).R, $"({x},{y})");
        }
    }

    /// <summary>壊れたファイル。</summary>
    private static void PngRejectsBroken()
    {
        ExpectPng(() => PngDecoder.Decode(Encoding.ASCII.GetBytes("not a png file")), "署名");
        var good = PngEncoder.Encode(Pattern(8, 8));
        ExpectPng(() => PngDecoder.Decode(good[..(good.Length / 2)]), "途中");
        var huge = MakePng(1, 1, 8, PngFormat.ColorTypeGray, new byte[] { 0, 0 });
        BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(PngFormat.Signature.Length + 8), PngDecoder.MaxDimension + 1);
        ExpectPng(() => PngDecoder.Decode(huge), "扱えません");
    }

    /// <summary>PngFormatException を期待する。</summary>
    private static void ExpectPng(System.Action action, string expected)
    {
        try
        {
            action();
        }
        catch (PngFormatException ex)
        {
            Check.True(ex.Message.Contains(expected), $"理由: {ex.Message}");
            return;
        }
        throw new AssertionException($"PngFormatException（{expected}）が投げられませんでした");
    }

    /// <summary>縮小と拡大。</summary>
    private static void Resampling()
    {
        var checker = new RgbaImage(4, 4);
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var v = (x + y) % 2 == 0 ? (byte)255 : (byte)0;
                var i = (y * 4 + x) * 4;
                checker.Pixels[i] = checker.Pixels[i + 1] = checker.Pixels[i + 2] = v;
                checker.Pixels[i + 3] = 255;
            }
        }
        var half = ImageResampler.Resize(checker, 2, 2);
        Check.True(System.Math.Abs(half.GetPixel(1, 1).R - 128) <= 1, $"市松模様の面積平均は灰色: {half.GetPixel(1, 1)}");

        var edge = new RgbaImage(2, 1);
        edge.Pixels[0] = 255; edge.Pixels[3] = 255;   // 不透明の赤と、透明の黒
        var merged = ImageResampler.Resize(edge, 1, 1).GetPixel(0, 0);
        Check.True(merged.R == 255 && System.Math.Abs(merged.A - 128) <= 1, $"透明の黒が混ざって黒ずまない: {merged}");

        var up = ImageResampler.Resize(Pattern(3, 3), 12, 12);
        Check.True(up.Width == 12 && up.Height == 12, "拡大");
        Check.Equal((10, 5), ImageResampler.FitInside(300, 150, 10), "横長は幅に合わせる");
        Check.Equal((5, 10), ImageResampler.FitInside(100, 200, 10), "縦長は高さに合わせる");
        Check.Equal((1, 10), ImageResampler.FitInside(1, 1000, 10), "細くても 1 画素は残す");
    }

    /// <summary>不透明の赤の画像。</summary>
    private static RgbaImage Red(int width, int height) => RgbaImage.Filled(width, height, new RgbaColor(255, 0, 0, 255));

    /// <summary>生成物の寸法。</summary>
    private static void GeneratedDimensions()
    {
        var output = LauncherIconGenerator.Generate(Red(300, 200), new RgbaColor(0, 0, 255, 255));
        Check.Equal(LauncherIconSpec.Densities.Count * 2 + 2, output.Files.Count, "密度ごとに 2 枚＋XML 2 つ");
        foreach (var density in LauncherIconSpec.Densities)
        {
            var folder = LauncherIconSpec.MipmapFolder(density);
            var legacy = PngDecoder.Decode(output.Files[$"{folder}/ic_launcher.png"]);
            var legacySize = (int)System.Math.Round(LauncherIconSpec.LegacyIconDp * density.Scale);
            Check.True(legacy.Width == legacySize && legacy.Height == legacySize, $"{folder}: 従来型は {legacySize} 画素（48dp）");
            Check.Equal(new RgbaColor(0, 0, 255, 255), legacy.GetPixel(0, 0), $"{folder}: 従来型の余りは背景色");
            Check.Equal(new RgbaColor(255, 0, 0, 255), legacy.GetPixel(legacySize / 2, legacySize / 2), $"{folder}: 従来型の中央は元の画像");

            var foreground = PngDecoder.Decode(output.Files[$"{folder}/ic_launcher_foreground.png"]);
            var layer = (int)System.Math.Round(LauncherIconSpec.AdaptiveLayerDp * density.Scale);
            Check.True(foreground.Width == layer && foreground.Height == layer, $"{folder}: 前景は {layer} 画素（108dp）");
            Check.Equal((byte)0, foreground.GetPixel(0, 0).A, $"{folder}: 前景の隅は透明（背景色の層が見える）");
            // 不透明な画素の範囲が安全域（66dp）の正方形の中に収まり、横は安全域いっぱい
            var opaque = Enumerable.Range(0, layer * layer).Where(p => foreground.Pixels[p * 4 + 3] > 0)
                .Select(p => (X: p % layer, Y: p / layer)).ToList();
            var safe = (int)System.Math.Round(LauncherIconSpec.AdaptiveSafeZoneDp * density.Scale);
            var width = opaque.Max(p => p.X) - opaque.Min(p => p.X) + 1;
            var height = opaque.Max(p => p.Y) - opaque.Min(p => p.Y) + 1;
            Check.True(width == safe && height <= safe, $"{folder}: 前景の中身は安全域 {safe} 画素に収まる（{width}x{height}）");
            Check.True(System.Math.Abs(opaque.Min(p => p.X) - (layer - width) / 2) <= 1, $"{folder}: 中央に置く");
        }
        var adaptive = Encoding.UTF8.GetString(output.Files[LauncherIconSpec.AdaptiveIconRelativePath]);
        Check.True(adaptive.Contains("@color/ic_launcher_background") && adaptive.Contains("@mipmap/ic_launcher_foreground"), "アダプティブアイコンの XML");
        var color = Encoding.UTF8.GetString(output.Files[LauncherIconSpec.BackgroundColorRelativePath]);
        Check.True(color.Contains("<color name=\"ic_launcher_background\">#0000FF</color>"), $"背景色の XML: {color}");
        Check.True(output.Warnings.Any(w => w.Contains("正方形ではありません")), "正方形でなければ警告");
        Check.True(output.Warnings.Any(w => w.Contains("小さい")), "小さければ（拡大になる）警告");
        Check.Equal(0, LauncherIconGenerator.Generate(Red(512, 512), RgbaColor.White).Warnings.Count, "512 の正方形なら警告なし");
    }

    /// <summary>置き場。</summary>
    private static void StagerWritesOnlyChanges()
    {
        using var temp = new TempDir();
        var icon = temp.Combine("icon.png");
        File.WriteAllBytes(icon, PngEncoder.Encode(Red(64, 64)));
        var res = temp.Combine("seedIcon/res");
        var source = new LauncherIconSource(icon, RgbaColor.White, "icon.png");

        var first = LauncherIconStager.Stage(res, source);
        Check.True(first.Generated && first.Written == 12 && first.Unchanged == 0, $"最初は全部書く: {first.Describe()}");
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(res, "mipmap-xxxhdpi", "ic_launcher.png"));
        var second = LauncherIconStager.Stage(res, source);
        Check.True(second.Written == 0 && second.Unchanged == 12, $"中身が同じなら書かない: {second.Describe()}");
        Check.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(res, "mipmap-xxxhdpi", "ic_launcher.png")), "更新時刻も変わらない");

        temp.WriteFile("seedIcon/res/mipmap-ldpi/old.png", "x");
        var third = LauncherIconStager.Stage(res, source with { Background = new RgbaColor(1, 2, 3, 255) });
        Check.True(third.Removed == 1 && !Directory.Exists(Path.Combine(res, "mipmap-ldpi")), "古い生成物とフォルダを消す");
        Check.True(third.Written >= 1, "背景色が変われば書き直す");

        var cleared = LauncherIconStager.Stage(res, null);
        Check.True(!cleared.Generated && cleared.Removed == 12 && !Directory.Exists(res), $"設定が無ければ置き場ごと消す: {cleared.Describe()}");

        File.WriteAllText(icon, "壊れた PNG");
        try
        {
            LauncherIconStager.Stage(res, source);
            throw new AssertionException("読めない PNG で止まらない");
        }
        catch (AndroidPipelineException ex)
        {
            Check.True(ex.Kind == AndroidFailureKind.InvalidRequest && ex.Message.Contains(icon), $"読めない PNG は指定の誤り: {ex.Message}");
        }
    }

    /// <summary>設定の検査。</summary>
    private static void IconSettings()
    {
        using var temp = new TempDir();
        var assets = temp.Combine("proj/assets");
        temp.WriteFile("proj/assets/icons/app.png", "x");
        Check.True(LauncherIconSettings.Resolve(null, assets) is null, "設定なしはアイコンを作らない");
        var relative = LauncherIconSettings.Resolve(new AndroidAppSettings { Icon = "icons/app.png" }, assets)!;
        Check.Equal(temp.Combine("proj/assets/icons/app.png"), relative.IconPath, "相対パスはアセットルートから");
        Check.Equal(RgbaColor.White, relative.Background, "背景色の既定は白");
        var scheme = LauncherIconSettings.Resolve(new AndroidAppSettings { Icon = "assets://icons/app.png", IconBackground = "#80112233" }, assets)!;
        Check.Equal(relative.IconPath, scheme.IconPath, "assets:// も読む");
        Check.Equal(new RgbaColor(0x11, 0x22, 0x33, 0x80), scheme.Background, "#AARRGGBB");

        Check.True(LauncherIconSettings.Validate(new AndroidAppSettings { Icon = "icons/app.jpg" }, assets).Single().Contains("PNG"), "PNG でない");
        Check.True(LauncherIconSettings.Validate(new AndroidAppSettings { Icon = "icons/none.png" }, assets).Single().Contains("ありません"), "無いファイル");
        Check.True(LauncherIconSettings.Validate(new AndroidAppSettings { IconBackground = "white" }, assets).Single().Contains("#RRGGBB"), "色の書式");
        Check.True(RgbaColor.TryParse(" #0a0B0c ", out var lower) && lower == new RgbaColor(10, 11, 12, 255), "小文字・前後の空白");
        Check.True(!RgbaColor.TryParse("#12345", out _) && !RgbaColor.TryParse("123456", out _) && !RgbaColor.TryParse("#GGGGGG", out _), "桁・# 無し・16 進でない");
        Check.Equal("#0A0B0C", lower.ToAndroidHex(), "不透明は #RRGGBB");
        try
        {
            LauncherIconSettings.Resolve(new AndroidAppSettings { Icon = "icons/none.png" }, assets);
            throw new AssertionException("無いアイコンで止まらない");
        }
        catch (AndroidPipelineException ex)
        {
            Check.True(ex.Kind == AndroidFailureKind.InvalidRequest, "ビルドでは指定の誤り");
        }
    }
}
