using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SEED;
using SEED.UI;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace UiComponentsTests;

/// <summary>
/// テーマ（W2-9）の純粋な計算のテスト（docs/ui_theme.md）:
/// 読み込み（UiThemeSource）・知らない名前と型の誤りの警告・継承（UiThemeResolver）と一部だけの上書き・明暗の対応と重ねる順（UiThemeDefinition）・
/// 明暗の選び方（UiBrightnessRules）・色の補間（UiThemeBlend・UiThemeTransition）・種の色（UiSeedColors）・見やすさ・見本のテーマ・
/// トークンの表の完全性（UiTokenCatalog と定数〈反射〉と既定のテーマ）と docs の表。
/// </summary>
public static class ThemeTests
{
    /// <summary>浮動小数の比較の許容量。</summary>
    private const double Eps = 1e-4;
    /// <summary>文字と面の最小のコントラスト比（WCAG 2 の AA）。</summary>
    private const float BodyContrast = 4.5f;
    /// <summary>主の色の上の文字・主の色と面の最小のコントラスト比（WCAG 2 の大きな文字・図形）。</summary>
    private const float LargeContrast = 3.0f;

    /// <summary>組み込みの既定のテーマ（出力へ写した default_theme.json を組み込みと同じ出どころで読む）。</summary>
    private static UiThemeSource BuiltInSource()
        => UiThemeSource.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "default_theme.json")), UiThemePaths.BuiltInOrigin);

    /// <summary>組み込みの既定のテーマの定義。</summary>
    private static UiThemeDefinition BuiltIn(UiThemeSource source) => UiThemeResolver.Build(source, source, _ => null);

    /// <summary>手元の表のファイルからテーマを作る。</summary>
    private static UiThemeDefinition Build(string origin, Dictionary<string, string> files, UiThemeSource builtIn)
        => UiThemeResolver.Build(UiThemeSource.Parse(files[origin], origin), builtIn, path => files.TryGetValue(path, out var json) ? json : null);

    /// <summary>16 進で比べる（sRGB の 8 bit に丸めた値）。</summary>
    private static void SameHex(string expected, Color actual, string what) => Check.Equal(expected, UiColorMath.ToHex(actual), what);

    /// <summary>公開の文字列の定数（トークンの名前）を反射で集める。</summary>
    private static IEnumerable<string> ConstTokens(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    public static void Register(TestHarness h)
    {
        var builtInSource = BuiltInSource();
        var builtIn = BuiltIn(builtInSource);
        var dark = builtIn.Resolve(UiBrightness.Dark);
        var light = builtIn.Resolve(UiBrightness.Light);

        // ── トークンの表 ───────────────────────────────────────
        h.Add("W2-9 表: 定数（反射）はすべて表にあり、表のすべてのトークンが既定のテーマに型どおりある・既定のテーマは警告なし", () =>
        {
            Check.Equal(0, builtInSource.Warnings.Count, "既定のテーマの読み込みの警告 " + string.Join(" / ", builtInSource.Warnings));
            var names = UiTokenCatalog.All.Select(i => i.Name).ToList();
            Check.Equal(names.Count, names.Distinct().Count(), "表に同じ名前が 2 度無い");
            foreach (var type in new[] { typeof(UiTokens), typeof(NavTokens), typeof(ChartTokens), typeof(TextFieldTokens) })
                foreach (var token in ConstTokens(type))
                    Check.True(UiTokenCatalog.TryGet(token, out _), $"{type.Name} の {token} が表にある");
            foreach (var token in UiTokens.All.Concat(NavTokens.All).Concat(ChartTokens.All).Concat(TextFieldTokens.All))
                Check.True(UiTokenCatalog.Match(token, out _) == UiTokenMatch.Known, $"部品の読む {token} が表で引ける");
            foreach (var info in UiTokenCatalog.All)
            {
                Check.True(info.UsedBy.Length > 0, $"{info.Name} の使う部品が書いてある");
                foreach (var table in new[] { dark, light })
                    foreach (var leaf in UiTokenCatalog.LeafTokens(info))
                    {
                        bool has = info.Kind switch
                        {
                            UiTokenKind.Color => table.TryColor(leaf, out _),
                            UiTokenKind.Text => table.TryText(leaf, out _),
                            _ => table.TryNumber(leaf, out _),
                        };
                        Check.True(has, $"既定のテーマ（{table.Brightness}）に {leaf} が {info.Kind} である");
                    }
            }
            // 逆向き: 既定のテーマのトークンはすべて表にある（表に無い値を持たない）
            foreach (var token in dark.ColorTokens.Concat(dark.NumberTokens).Concat(dark.TextTokens))
                Check.True(UiTokenCatalog.Match(token, out _) == UiTokenMatch.Known, $"既定のテーマの {token} が表にある");
            Check.True(UiTokenCatalog.All.Count > 100, $"表の行 {UiTokenCatalog.All.Count}");
        });

        h.Add("W2-9 表: docs/ui_theme.md の表が、表（UiTokenCatalog）と既定のテーマから作ったものと一致する", () =>
        {
            string docs = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ui_theme.md"));
            string? inDocs = ThemeTokenTable.Extract(docs);
            Check.True(inDocs is not null, "docs に表の印がある");
            string expected = ThemeTokenTable.Build(dark, light);
            if (inDocs != expected)
            {
                var a = inDocs!.Split('\n');
                var b = expected.Split('\n');
                int i = 0;
                while (i < Math.Min(a.Length, b.Length) && a[i] == b[i]) i++;
                Check.True(false, $"docs の表が古い（{i + 1} 行目から違う。docs: {(i < a.Length ? a[i] : "(無い)")} / 期待: {(i < b.Length ? b[i] : "(無い)")}）。--update-docs で作り直す");
            }
        });

        // ── 読み込み ───────────────────────────────────────────
        h.Add("W2-9 読み込み: グループ・最上位の直書き・曲線・書体・app・説明の鍵・コメントと末尾のカンマ", () =>
        {
            var src = UiThemeSource.Parse(@"{
  // 手で書くファイルなのでコメントを許す
  ""_about"": ""x"",
  ""name"": ""t"",
  ""color"": { ""primary"": ""#FFFFFF"", ""_note"": ""#000000"" },
  ""color.surface"": ""#80808080"",
  ""radius"": { ""button"": 12 },
  ""motion"": { ""push_curve"": { ""x1"": 0.1, ""y1"": 0, ""x2"": 0.3, ""y2"": 1 } },
  ""font"": { ""family"": ""assets://fonts/a.ttf"" },
  ""app"": { ""kakugo"": { ""danger"": ""#FF0000"", ""size"": 3, ""label"": ""覚悟"" } },
  ""app.flag"": 1,
}", "t.json");
            Check.True(src.IsValid, "読める: " + src.Error);
            Check.Equal(0, src.Warnings.Count, "警告なし " + string.Join(" / ", src.Warnings));
            Check.Equal("t", src.Name, "名前");
            SameHex("#FFFFFF", src.Base[UiTokens.ColorPrimary].Color, "グループの色");
            Check.Close(0.21586, src.Base[UiTokens.ColorSurface].Color.r, 1e-4, "最上位の直書き・sRGB の中間の灰色は線形で約 0.216");
            Check.Close(128.0 / 255.0, src.Base[UiTokens.ColorSurface].Color.a, 1e-4, "アルファは線形へ直さない");
            Check.True(!src.Base.ContainsKey("color._note"), "_ で始まる鍵は読まない");
            Check.Close(12, src.Base[UiTokens.RadiusButton].Number, Eps, "数");
            Check.Close(0.1, src.Base[NavTokens.MotionPushCurve + UiCurve.SuffixX1].Number, Eps, "曲線の成分");
            Check.Equal("assets://fonts/a.ttf", src.Base[UiTokens.FontFamily].Text, "書体（文字列）");
            Check.Equal(UiTokenKind.Color, src.Base["app.kakugo.danger"].Kind, "app の #… は色");
            Check.Equal(UiTokenKind.Number, src.Base["app.kakugo.size"].Kind, "app の数は数");
            Check.Equal("覚悟", src.Base["app.kakugo.label"].Text, "app のほかの文字列は文字列");
            Check.Close(1, src.Base["app.flag"].Number, Eps, "app の直書き");
            var broken = UiThemeSource.Parse("{ not json", "b.json");
            Check.True(!broken.IsValid && broken.Error.Length > 0, "壊れた JSON は理由を返す（例外にしない）");
            Check.True(!UiThemeSource.Parse("[1,2]", "a.json").IsValid, "最上位が配列なら読めない");
        });

        h.Add("W2-9 読み込み: 知らない名前・知らないグループ・型の誤りは警告して読まず、既定の値が残る", () =>
        {
            const string json = @"{
 ""color"": { ""primery"": ""#000000"", ""primary"": 12, ""surface"": ""red"", ""outline"": ""#12345"" },
 ""radius"": { ""button"": ""12"" },
 ""colour"": { ""primary"": ""#000000"" },
 ""font"": { ""family"": 3 },
 ""motion"": { ""push_curve"": 0.5 },
 ""size"": ""big"",
 ""brightness"": ""dim"",
 ""extends"": 5,
 ""seed_color"": ""green"",
 ""light"": [],
 ""dark"": { ""name"": ""x"", ""color"": { ""on_surface"": ""#101010"" } },
 ""app"": { ""list"": [1, 2] }
}";
            var src = UiThemeSource.Parse(json, "w.json");
            Check.True(src.IsValid, "JSON としては読める");
            string all = string.Join("\n", src.Warnings);
            Check.Equal(15, src.Warnings.Count, "警告の数\n" + all);
            foreach (var token in new[] { "color.primery", "color.primary", "color.surface", "color.outline", "radius.button", "colour.primary",
                         "font.family", "motion.push_curve", "size", "brightness", "extends", "seed_color", "light", "dark.name", "app.list" })
                Check.True(src.Warnings.Any(w => w.StartsWith($"w.json: {token}: ", StringComparison.Ordinal)), $"{token} の警告がある\n{all}");
            Check.True(src.Warnings.Single(w => w.StartsWith("w.json: color.primery: ", StringComparison.Ordinal)).Contains("打ち間違い"), "表に無い名前は打ち間違いと言う");
            Check.True(src.Warnings.Single(w => w.StartsWith("w.json: colour.primary: ", StringComparison.Ordinal)).Contains("知らないグループ"), "知らないグループ");
            var theme = UiThemeResolver.Build(src, builtInSource, _ => null);
            var table = theme.Resolve(UiBrightness.Dark);
            SameHex(UiColorMath.ToHex(dark.Color(UiTokens.ColorPrimary)), table.Color(UiTokens.ColorPrimary), "型の誤りの色は既定");
            SameHex(UiColorMath.ToHex(dark.Color(UiTokens.ColorSurface)), table.Color(UiTokens.ColorSurface), "名前の色は読まない（既定）");
            SameHex(UiColorMath.ToHex(dark.Color(UiTokens.ColorOutline)), table.Color(UiTokens.ColorOutline), "桁の足りない色は既定");
            Check.Close(dark.Number(UiTokens.RadiusButton), table.Number(UiTokens.RadiusButton), Eps, "文字列の数は既定");
            Check.Equal(string.Empty, table.Text(UiTokens.FontFamily, "?"), "数の書体は既定（空）");
            Check.Equal(UiCurve.Standard, UiCurve.FromTheme(table, NavTokens.MotionPushCurve, UiCurve.Linear), "数の曲線は既定の曲線");
            Check.True(!table.Has("color.primery") && !table.Has("colour.primary"), "知らない名前は表に入らない");
            Check.Equal(UiBrightness.Dark, theme.Brightness, "読めない明暗は基のまま");
            SameHex("#101010", table.Color(UiTokens.ColorOnSurface), "節の中の正しい値は読む");
        });

        // ── 継承 ───────────────────────────────────────────────
        h.Add("W2-9 継承: 一部だけの上書き → 基のテーマ → 既定の順に落ちる（相対・絶対・.. のパス・default）", () =>
        {
            var files = new Dictionary<string, string>
            {
                ["assets://ui/themes/base.json"] = "{\"name\":\"base\",\"color\":{\"primary\":\"#112233\",\"surface\":\"#223344\"},\"radius\":{\"button\":20}}",
                ["assets://ui/themes/child.json"] = "{\"name\":\"child\",\"extends\":\"base.json\",\"color\":{\"primary\":\"#445566\"}}",
                ["assets://common/abs.json"] = "{\"extends\":\"assets://ui/themes/child.json\",\"radius\":{\"chip\":30}}",
                ["assets://ui/themes/sub/up.json"] = "{\"extends\":\"../base.json\",\"text\":{\"body\":18}}",
                ["assets://x/def.json"] = "{\"extends\":\"default\",\"radius\":{\"card\":1}}",
            };
            var abs = Build("assets://common/abs.json", files, builtInSource);
            Check.Equal(0, abs.Warnings.Count, "警告なし " + string.Join(" / ", abs.Warnings));
            Check.Equal("builtin:default|assets://ui/themes/base.json|assets://ui/themes/child.json|assets://common/abs.json",
                string.Join("|", abs.ChainOrigins), "根 → 葉の鎖");
            Check.Equal("abs", abs.Name, "name が無ければファイル名");
            var t = abs.Resolve(UiBrightness.Dark);
            SameHex("#445566", t.Color(UiTokens.ColorPrimary), "近い方（child）の上書き");
            SameHex("#223344", t.Color(UiTokens.ColorSurface), "child に無ければ base");
            Check.Close(20, t.Number(UiTokens.RadiusButton), Eps, "base の数");
            Check.Close(30, t.Number(UiTokens.RadiusChip), Eps, "葉の数");
            Check.Close(dark.Number(UiTokens.RadiusCard), t.Number(UiTokens.RadiusCard), Eps, "どこにも無ければ既定");
            SameHex(UiColorMath.ToHex(dark.Color(UiTokens.ColorOnSurface)), t.Color(UiTokens.ColorOnSurface), "色も既定へ落ちる");
            var up = Build("assets://ui/themes/sub/up.json", files, builtInSource).Resolve(UiBrightness.Dark);
            SameHex("#112233", up.Color(UiTokens.ColorPrimary), ".. で上のフォルダの基");
            Check.Close(18, up.Number(UiTokens.TextBody), Eps, "葉の値");
            var def = Build("assets://x/def.json", files, builtInSource);
            Check.Equal(2, def.ChainOrigins.Count, "extends: default は既定のテーマへ直接");
            Check.Equal(1, BuiltIn(builtInSource).ChainOrigins.Count, "既定のテーマそのものは鎖 1 つ");
            Check.True(BuiltIn(builtInSource).IsBuiltIn, "既定のテーマの印");
        });

        h.Add("W2-9 継承: 読めない基・壊れた基・輪・深すぎは警告して既定へつなぐ（例外で止めない）", () =>
        {
            var files = new Dictionary<string, string>
            {
                ["assets://t/missing.json"] = "{\"extends\":\"nope.json\",\"radius\":{\"button\":5}}",
                ["assets://t/badparent.json"] = "{\"extends\":\"bad.json\"}",
                ["assets://t/bad.json"] = "{ bad",
                ["assets://t/a.json"] = "{\"extends\":\"b.json\",\"radius\":{\"button\":7}}",
                ["assets://t/b.json"] = "{\"extends\":\"a.json\",\"radius\":{\"chip\":8}}",
                ["assets://t/self.json"] = "{\"extends\":\"self.json\"}",
            };
            for (int i = 0; i < UiThemeResolver.MaxDepth + 4; i++)
                files[$"assets://d/{i}.json"] = $"{{\"extends\":\"{i + 1}.json\",\"space\":{{\"xs\":{i}}}}}";
            var missing = Build("assets://t/missing.json", files, builtInSource);
            Check.Equal(1, missing.Warnings.Count, "読めない基の警告 " + string.Join(" / ", missing.Warnings));
            Check.True(missing.Warnings[0].Contains("読めません"), "読めない");
            Check.Close(5, missing.Resolve(UiBrightness.Dark).Number(UiTokens.RadiusButton), Eps, "葉の値は使う");
            Check.Equal(2, missing.ChainOrigins.Count, "既定のテーマへつなぐ");
            var bad = Build("assets://t/badparent.json", files, builtInSource);
            Check.True(bad.Warnings.Count == 1 && bad.Warnings[0].Contains("壊れて"), "壊れた基の警告");
            var cycle = Build("assets://t/a.json", files, builtInSource);
            Check.True(cycle.Warnings.Count == 1 && cycle.Warnings[0].Contains("輪"), "輪の警告");
            Check.Equal("builtin:default|assets://t/b.json|assets://t/a.json", string.Join("|", cycle.ChainOrigins), "輪の手前まで");
            Check.Close(7, cycle.Resolve(UiBrightness.Dark).Number(UiTokens.RadiusButton), Eps, "葉");
            Check.Close(8, cycle.Resolve(UiBrightness.Dark).Number(UiTokens.RadiusChip), Eps, "輪の手前の基");
            var self = Build("assets://t/self.json", files, builtInSource);
            Check.True(self.Warnings.Count == 1 && self.Warnings[0].Contains("輪"), "自分を基にした輪");
            var deep = Build("assets://d/0.json", files, builtInSource);
            Check.True(deep.Warnings.Count == 1 && deep.Warnings[0].Contains("深すぎ"), "深すぎの警告");
            Check.Equal(UiThemeResolver.MaxDepth + 1, deep.ChainOrigins.Count, "葉から MaxDepth 段＋既定のテーマ");
            Check.Close(0, deep.Resolve(UiBrightness.Dark).Number(UiTokens.SpaceXs), Eps, "葉の値");
        });

        // ── 明暗 ───────────────────────────────────────────────
        h.Add("W2-9 明暗: 対応の決まり方（既定は両方・brightness を書くとその明暗だけ・節で足す・書かない上書きは基を受け継ぐ）", () =>
        {
            Check.True(builtIn.SupportsDark && builtIn.SupportsLight, "既定のテーマは両方");
            Check.Equal(UiBrightness.Dark, builtIn.Brightness, "既定のテーマは暗い方");
            var files = new Dictionary<string, string>
            {
                ["assets://m/dark_only.json"] = "{\"brightness\":\"dark\",\"color\":{\"surface\":\"#101010\"}}",
                ["assets://m/light_only.json"] = "{\"brightness\":\"light\"}",
                ["assets://m/tweak.json"] = "{\"radius\":{\"button\":30}}",
                ["assets://m/both.json"] = "{\"brightness\":\"dark\",\"light\":{\"color\":{\"primary\":\"#FF0000\"}}}",
                ["assets://m/light_child.json"] = "{\"extends\":\"light_only.json\",\"radius\":{\"chip\":3}}",
                ["assets://m/light_child_dark.json"] = "{\"extends\":\"light_only.json\",\"dark\":{}}",
            };
            var darkOnly = Build("assets://m/dark_only.json", files, builtInSource);
            Check.True(darkOnly.SupportsDark && !darkOnly.SupportsLight, "brightness: dark はその明暗だけ");
            var lightOnly = Build("assets://m/light_only.json", files, builtInSource);
            Check.True(lightOnly.SupportsLight && !lightOnly.SupportsDark && lightOnly.Brightness == UiBrightness.Light, "brightness: light");
            SameHex("#FFFFFF", lightOnly.Resolve(UiBrightness.Light).Color(UiTokens.ColorSurface), "明るい方は既定の light の節の面");
            Check.Equal(UiBrightness.Light, lightOnly.Resolve(UiBrightness.Dark).Brightness, "対応していない明暗はテーマの明暗で解く");
            var tweak = Build("assets://m/tweak.json", files, builtInSource);
            Check.True(tweak.SupportsDark && tweak.SupportsLight, "brightness を書かない上書きは基（両方）を受け継ぐ");
            SameHex("#FFFFFF", tweak.Resolve(UiBrightness.Light).Color(UiTokens.ColorSurface), "上書きのテーマの明るい方");
            Check.Close(30, tweak.Resolve(UiBrightness.Light).Number(UiTokens.RadiusButton), Eps, "上書きは両方の明暗に効く");
            Check.Close(30, tweak.Resolve(UiBrightness.Dark).Number(UiTokens.RadiusButton), Eps, "上書き（暗い方）");
            var both = Build("assets://m/both.json", files, builtInSource);
            Check.True(both.SupportsDark && both.SupportsLight, "brightness ＋ もう一方の節で両方");
            var lightChild = Build("assets://m/light_child.json", files, builtInSource);
            Check.True(lightChild.SupportsLight && !lightChild.SupportsDark && lightChild.Brightness == UiBrightness.Light, "明るいだけの基を受け継ぐ");
            var lightChildDark = Build("assets://m/light_child_dark.json", files, builtInSource);
            Check.True(lightChildDark.SupportsLight && lightChildDark.SupportsDark && lightChildDark.Brightness == UiBrightness.Light, "空の dark の節でも暗い方に対応");
        });

        h.Add("W2-9 明暗: 重ねる順（子の最上位は親の明暗の節に勝つ・子の節は子の最上位に勝つ）", () =>
        {
            var files = new Dictionary<string, string>
            {
                ["assets://o/base_only.json"] = "{\"color\":{\"primary\":\"#00FF00\"}}",
                ["assets://o/with_section.json"] = "{\"color\":{\"primary\":\"#00FF00\"},\"light\":{\"color\":{\"primary\":\"#0000FF\"}}}",
            };
            var baseOnly = Build("assets://o/base_only.json", files, builtInSource);
            SameHex("#00FF00", baseOnly.Resolve(UiBrightness.Light).Color(UiTokens.ColorPrimary), "子の最上位 > 既定の light の節");
            SameHex(UiColorMath.ToHex(light.Color(UiTokens.ColorSurface)), baseOnly.Resolve(UiBrightness.Light).Color(UiTokens.ColorSurface), "ほかは既定の明るい方");
            var withSection = Build("assets://o/with_section.json", files, builtInSource);
            SameHex("#0000FF", withSection.Resolve(UiBrightness.Light).Color(UiTokens.ColorPrimary), "子の light の節 > 子の最上位");
            SameHex("#00FF00", withSection.Resolve(UiBrightness.Dark).Color(UiTokens.ColorPrimary), "暗い方は子の最上位");
            Check.True(ReferenceEquals(withSection.Resolve(UiBrightness.Light), withSection.Resolve(UiBrightness.Light)), "明暗ごとに 1 度だけ作る");
            SameHex("#6C4BFF", light.Color(UiTokens.ColorPrimary), "既定のテーマの明るい方の主の色");
            SameHex("#7C5CFF", dark.Color(UiTokens.ColorPrimary), "既定のテーマの暗い方の主の色（W2-4 のまま）");
            Check.Close(0.1, light.Number(UiTokens.OpacityPressed), Eps, "明るい方の押下の濃さ");
            Check.Close(0.16, dark.Number(UiTokens.OpacityPressed), Eps, "暗い方の押下の濃さ（W2-4 のまま）");
        });

        h.Add("W2-9 明暗: 選び方の規則（Theme・System〈不明ならテーマ〉・強制・対応していなければテーマの明暗）", () =>
        {
            const UiBrightness D = UiBrightness.Dark, L = UiBrightness.Light;
            UiBrightness R(UiBrightnessMode m, UiBrightness own, bool sl, bool sd, UiBrightness? sys) => UiBrightnessRules.Resolve(m, own, sl, sd, sys);
            // テーマのまま: 端末・強制に関係なくテーマの明暗
            foreach (UiBrightness? sys in new UiBrightness?[] { null, D, L })
            {
                Check.Equal(D, R(UiBrightnessMode.Theme, D, true, true, sys), "Theme（暗いテーマ）");
                Check.Equal(L, R(UiBrightnessMode.Theme, L, true, true, sys), "Theme（明るいテーマ）");
            }
            // 端末に従う
            Check.Equal(L, R(UiBrightnessMode.System, D, true, true, L), "System: 端末が明るい → 明るい");
            Check.Equal(D, R(UiBrightnessMode.System, L, true, true, D), "System: 端末が暗い → 暗い");
            Check.Equal(D, R(UiBrightnessMode.System, D, true, true, null), "System: 不明 → テーマの明暗（暗）");
            Check.Equal(L, R(UiBrightnessMode.System, L, true, true, null), "System: 不明 → テーマの明暗（明）");
            Check.Equal(D, R(UiBrightnessMode.System, D, false, true, L), "System: テーマが明るい方に対応していなければテーマのまま");
            // 強制
            Check.Equal(L, R(UiBrightnessMode.Light, D, true, true, D), "Light の強制（端末は無視）");
            Check.Equal(D, R(UiBrightnessMode.Dark, L, true, true, L), "Dark の強制");
            Check.Equal(D, R(UiBrightnessMode.Light, D, false, true, null), "Light を強制しても対応していなければテーマのまま");
            Check.Equal(L, R(UiBrightnessMode.Dark, L, true, false, null), "Dark を強制しても対応していなければテーマのまま");
            Check.Equal(L, UiBrightnessRules.Desired(UiBrightnessMode.Light, D, null), "望む明暗は対応を見ない");
            Check.True(UiBrightnessRules.TryParse("light", out var b) && b == L, "語 light");
            Check.True(!UiBrightnessRules.TryParse("Light", out _), "語は小文字だけ");
        });

        // ── 色の補間 ───────────────────────────────────────────
        h.Add("W2-9 補間: 端・中間（sRGB の成分ごと）・数と文字列は行き先・片側だけの色", () =>
        {
            var to = UiThemeData.Parse("{\"brightness\":\"light\",\"radius\":{\"button\":40},\"app\":{\"only_to\":\"#123456\"}}", light, out _);
            var from = UiThemeData.Parse("{\"app\":{\"only_from\":\"#654321\"}}", dark, out _);
            var start = UiThemeBlend.Blend(from, to, 0f);
            SameHex(UiColorMath.ToHex(dark.Color(UiTokens.ColorSurface)), start.Color(UiTokens.ColorSurface), "0 は元の色");
            Check.Close(40, start.Number(UiTokens.RadiusButton), Eps, "数は最初から行き先");
            Check.Equal(UiBrightness.Light, start.Brightness, "明暗は行き先");
            SameHex("#123456", start.Color("app.only_to"), "行き先にだけある色は行き先");
            Check.True(!start.Has("app.only_from"), "元にだけある色は捨てる");
            Check.True(ReferenceEquals(to, UiThemeBlend.Blend(from, to, 1f)), "1 は行き先そのもの");
            var mid = UiThemeBlend.Blend(from, to, 0.5f).Color(UiTokens.ColorSurface);
            var a = dark.Color(UiTokens.ColorSurface);
            var z = to.Color(UiTokens.ColorSurface);
            Check.Close((UiColorMath.LinearToSrgb(a.r) + UiColorMath.LinearToSrgb(z.r)) / 2, UiColorMath.LinearToSrgb(mid.r), 1e-4, "半分は sRGB の中間（赤）");
            Check.Close((UiColorMath.LinearToSrgb(a.b) + UiColorMath.LinearToSrgb(z.b)) / 2, UiColorMath.LinearToSrgb(mid.b), 1e-4, "半分は sRGB の中間（青）");
            Check.True(mid.r < (a.r + z.r) / 2, $"線形の中間より暗い（sRGB で混ぜる。{mid.r:0.000} < {(a.r + z.r) / 2:0.000}）");
            var alpha = UiColorMath.LerpSrgb(new Color(1f, 1f, 1f, 0f), new Color(1f, 1f, 1f, 1f), 0.25f);
            Check.Close(0.25, alpha.a, Eps, "アルファはそのまま補間");
        });

        h.Add("W2-9 補間: 時間の進み・曲線・0 秒はすぐ・負と NaN の dt・終わりは行き先そのもの", () =>
        {
            var tr = new UiThemeTransition(dark, light, 0.3f, UiCurve.Linear);
            Check.True(!tr.IsDone, "始まったばかり");
            var half = tr.Step(0.15f);
            Check.Close(0.5, tr.Progress, 1e-3, "半分の時間で半分（直線）");
            Check.Close((UiColorMath.LinearToSrgb(dark.Color(UiTokens.ColorPrimary).g) + UiColorMath.LinearToSrgb(light.Color(UiTokens.ColorPrimary).g)) / 2,
                UiColorMath.LinearToSrgb(half.Color(UiTokens.ColorPrimary).g), 1e-3, "途中の表の色");
            tr.Step(-1f);
            tr.Step(float.NaN);
            Check.Close(0.15, tr.Elapsed, Eps, "負・NaN の dt は進めない");
            var end = tr.Step(10f);
            Check.True(tr.IsDone && ReferenceEquals(end, light), "終わりは行き先そのもの");
            Check.Close(0.3, tr.Elapsed, Eps, "時間は越えない");
            var instant = new UiThemeTransition(dark, light, 0f, UiCurve.Linear);
            Check.True(instant.IsDone && ReferenceEquals(instant.Current, light), "0 秒はすぐ行き先");
            var eased = new UiThemeTransition(dark, light, 1f, UiCurve.FastOutSlowIn);
            eased.Step(0.5f);
            Check.True(eased.Progress > 0.5f, $"fastOutSlowIn は前半が速い（{eased.Progress}）");
            Check.Close(0.3, dark.Number(UiTokens.MotionTheme), Eps, "既定の補間の時間は 0.3 秒");
            Check.Equal(UiCurve.FastOutSlowIn, UiCurve.FromTheme(dark, UiTokens.MotionThemeCurve, UiCurve.Linear), "既定の補間の曲線");
        });

        // ── 種の色 ─────────────────────────────────────────────
        h.Add("W2-9 種の色: 6 つのトークン・コントラストの約束（midnight・forest・sunrise・暗すぎる種）", () =>
        {
            foreach (var (seedHex, brightness, surfaceHex) in new[]
                     {
                         ("#6C4BFF", UiBrightness.Dark, "#1E1B26"), ("#2E7D32", UiBrightness.Dark, "#17221A"),
                         ("#FF7043", UiBrightness.Light, "#FFFFFF"), ("#101010", UiBrightness.Dark, "#1E1B26"),
                         ("#FFFF00", UiBrightness.Light, "#FFFFFF"),
                     })
            {
                UiColorMath.TryParseHex(seedHex, out var seed);
                UiColorMath.TryParseHex(surfaceHex, out var surface);
                var d = UiSeedColors.Derive(seed, brightness, surface);
                Check.Equal(UiSeedColors.DerivedTokens.Length, d.Count, $"{seedHex}: 作るトークンの数");
                foreach (var token in UiSeedColors.DerivedTokens) Check.True(d.ContainsKey(token), $"{seedHex}: {token}");
                var primary = d[UiTokens.ColorPrimary].Color;
                Check.True(UiColorMath.Contrast(primary, surface) >= UiSeedColors.MinPrimaryContrast - 1e-3f,
                    $"{seedHex}: 主の色と面 {UiColorMath.Contrast(primary, surface):0.00}");
                Check.True(UiColorMath.Contrast(d[UiTokens.ColorOnPrimary].Color, primary) >= BodyContrast,
                    $"{seedHex}: 主の色の上の文字 {UiColorMath.Contrast(d[UiTokens.ColorOnPrimary].Color, primary):0.00}");
                Check.True(UiColorMath.Contrast(d[UiTokens.ColorOnSelected].Color, d[UiTokens.ColorSelected].Color) >= BodyContrast,
                    $"{seedHex}: 選択の文字");
                SameHex(UiColorMath.ToHex(primary), d[ChartTokens.ColorSeries1].Color, $"{seedHex}: 系列 1 は主の色");
                Check.Close(0.12, d[ChartTokens.ColorHighlight].Color.a, Eps, $"{seedHex}: 強調は 12%");
            }
            UiColorMath.TryParseHex("#6C4BFF", out var midnight);
            UiColorMath.TryParseHex("#1E1B26", out var darkSurface);
            SameHex("#6C4BFF", UiSeedColors.Derive(midnight, UiBrightness.Dark, darkSurface)[UiTokens.ColorPrimary].Color, "midnight は種の色のまま（3:1 に届く）");
            SameHex("#FFFFFF", UiSeedColors.Derive(midnight, UiBrightness.Dark, darkSurface)[UiTokens.ColorOnPrimary].Color, "midnight の文字は白");
            UiColorMath.TryParseHex("#FF7043", out var sunrise);
            var sun = UiSeedColors.Derive(sunrise, UiBrightness.Light, new Color(1f, 1f, 1f, 1f));
            Check.True(UiColorMath.ToHex(sun[UiTokens.ColorPrimary].Color) != "#FF7043", "sunrise は白い面で 3:1 に足りないので黒へ寄せる: " + UiColorMath.ToHex(sun[UiTokens.ColorPrimary].Color));
            // 明示が勝つ・面はそのテーマの面に合わせる
            var files = new Dictionary<string, string>
            {
                ["assets://s/explicit.json"] = "{\"brightness\":\"dark\",\"seed_color\":\"#FF0000\",\"color\":{\"primary\":\"#00FF00\",\"surface\":\"#202020\"}}",
            };
            var ex = Build("assets://s/explicit.json", files, builtInSource).Resolve(UiBrightness.Dark);
            SameHex("#00FF00", ex.Color(UiTokens.ColorPrimary), "明示した主の色が勝つ");
            UiColorMath.TryParseHex("#FF0000", out var red);
            UiColorMath.TryParseHex("#202020", out var s202020);
            var expected = UiSeedColors.Derive(red, UiBrightness.Dark, s202020);
            SameHex(UiColorMath.ToHex(expected[UiTokens.ColorSelected].Color), ex.Color(UiTokens.ColorSelected), "選択の色はこのテーマの面（#202020）で作る");
        });

        // ── 見やすさ・見本 ─────────────────────────────────────
        var sampleFiles = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "themes"), "*.json")
            .ToDictionary(p => "assets://ui/themes/" + Path.GetFileName(p), File.ReadAllText);

        h.Add("W2-9 見やすさ: 既定のテーマと見本のテーマの文字と面のコントラスト（本文 4.5・主の色とエラーの面の上 3）", () =>
        {
            var tables = new List<UiThemeData> { dark, light };
            foreach (var origin in sampleFiles.Keys)
            {
                var def = Build(origin, sampleFiles, builtInSource);
                if (def.SupportsDark) tables.Add(def.Resolve(UiBrightness.Dark));
                if (def.SupportsLight) tables.Add(def.Resolve(UiBrightness.Light));
            }
            foreach (var t in tables)
            {
                string who = $"{t.Name}（{UiBrightnessRules.ToWord(t.Brightness)}）";
                void Pair(string fg, string bg, float min)
                {
                    float c = UiColorMath.Contrast(t.Color(fg), t.Color(bg));
                    Check.True(c >= min, $"{who}: {fg} / {bg} = {c:0.00} ≥ {min}");
                }
                Pair(UiTokens.ColorOnSurface, UiTokens.ColorSurface, BodyContrast);
                Pair(UiTokens.ColorOnSurface, UiTokens.ColorBackground, BodyContrast);
                Pair(UiTokens.ColorOnSurfaceMuted, UiTokens.ColorSurface, BodyContrast);
                Pair(UiTokens.ColorOnSelected, UiTokens.ColorSelected, BodyContrast);
                Pair(NavTokens.ColorOnInverseSurface, NavTokens.ColorInverseSurface, BodyContrast);
                Pair(ChartTokens.ColorLabel, UiTokens.ColorSurface, BodyContrast);
                Pair(UiTokens.ColorOnPrimary, UiTokens.ColorPrimary, LargeContrast);
                // W2 の手直し P2-3: 削除の面の上の「削除」の文字（ボタンの文字と同じく 3:1。暗い方の #FF5252 に白で 3.19）
                Pair(UiTokens.ColorOnError, UiTokens.ColorError, LargeContrast);
            }
        });

        h.Add("W2-9 見本: templates/ui/themes が警告なく読め、継承・明暗の対応・一部だけの上書きが決まりどおり", () =>
        {
            Check.Equal(3, sampleFiles.Count, "見本は 3 つ");
            foreach (var origin in sampleFiles.Keys)
            {
                var def = Build(origin, sampleFiles, builtInSource);
                Check.Equal(0, def.Warnings.Count, $"{origin} の警告 " + string.Join(" / ", def.Warnings));
            }
            var forest = Build("assets://ui/themes/forest.json", sampleFiles, builtInSource);
            Check.True(forest.SupportsDark && !forest.SupportsLight && forest.Brightness == UiBrightness.Dark, "forest は暗い方だけ");
            var sunrise = Build("assets://ui/themes/sunrise.json", sampleFiles, builtInSource);
            Check.True(sunrise.SupportsLight && !sunrise.SupportsDark && sunrise.Brightness == UiBrightness.Light, "sunrise は明るい方だけ");
            SameHex("#FFFFFF", sunrise.Resolve(UiBrightness.Light).Color(UiTokens.ColorSurface), "sunrise の面は既定の明るい方");
            var round = Build("assets://ui/themes/forest_round.json", sampleFiles, builtInSource);
            Check.Equal("builtin:default|assets://ui/themes/forest.json|assets://ui/themes/forest_round.json", string.Join("|", round.ChainOrigins), "forest_round の鎖");
            Check.True(round.SupportsDark && !round.SupportsLight, "forest_round は forest の明暗を受け継ぐ");
            var r = round.Resolve(UiBrightness.Dark);
            var f = forest.Resolve(UiBrightness.Dark);
            SameHex("#00796B", r.Color(UiTokens.ColorPrimary), "上書きした主の色");
            Check.Close(24, r.Number(UiTokens.RadiusButton), Eps, "上書きした角丸");
            Check.Close(0.35, r.Number(UiTokens.FontWeight), Eps, "上書きした太さ");
            SameHex(UiColorMath.ToHex(f.Color(UiTokens.ColorSurface)), r.Color(UiTokens.ColorSurface), "面は forest");
            SameHex(UiColorMath.ToHex(f.Color(UiTokens.ColorSelected)), r.Color(UiTokens.ColorSelected), "選択の色は forest の種の色から");
            Check.Close(dark.Number(UiTokens.RadiusProgress), r.Number(UiTokens.RadiusProgress), Eps, "forest にも無ければ既定");
            Check.Close(dark.Number(NavTokens.MotionPush), r.Number(NavTokens.MotionPush), Eps, "動きも既定");
        });

        // ── パスと色の計算 ─────────────────────────────────────
        h.Add("W2-9 パス: extends の書き方 → 読むファイル", () =>
        {
            const string o = "assets://ui/themes/a.json";
            Check.Equal(UiThemePaths.BuiltInOrigin, UiThemePaths.Resolve("default", o), "default");
            Check.Equal(UiThemePaths.BuiltInOrigin, UiThemePaths.Resolve("builtin:default", o), "builtin:default");
            Check.Equal("assets://ui/themes/b.json", UiThemePaths.Resolve("b.json", o), "同じフォルダ");
            Check.Equal("assets://ui/themes/sub/c.json", UiThemePaths.Resolve("./sub/c.json", o), "./");
            Check.Equal("assets://ui/d.json", UiThemePaths.Resolve("../d.json", o), "..");
            Check.Equal("assets://e.json", UiThemePaths.Resolve("../../../../e.json", o), "根より上へは出ない");
            Check.Equal("assets://x/y.json", UiThemePaths.Resolve("assets://x//z/../y.json", o), "絶対のパスも整える");
            Check.Equal("b.json", UiThemePaths.Resolve("b.json", "(json)"), "アセットでない出どころはそのまま");
        });

        h.Add("W2-9 色の計算: sRGB ↔ 線形の往復・16 進の往復・コントラスト比", () =>
        {
            for (int i = 0; i <= 255; i++)
            {
                float s = i / 255f;
                Check.Close(s, UiColorMath.LinearToSrgb(UiColorMath.SrgbToLinear(s)), 1e-4, $"往復 {i}");
            }
            foreach (var hex in new[] { "#000000", "#FFFFFF", "#7C5CFF", "#12345678", "#6C4BFF1F" })
            {
                Check.True(UiColorMath.TryParseHex(hex, out var c), hex);
                Check.Equal(hex, UiColorMath.ToHex(c), $"{hex} の往復");
            }
            Check.Close(21, UiColorMath.Contrast(new Color(0f, 0f, 0f, 1f), new Color(1f, 1f, 1f, 1f)), 1e-3, "白黒は 21");
            Check.Close(1, UiColorMath.Contrast(dark.Color(UiTokens.ColorPrimary), dark.Color(UiTokens.ColorPrimary)), 1e-6, "同じ色は 1");
            UiColorMath.TryParseHex("#777777", out var gray);
            Check.Close(4.48, UiColorMath.Contrast(gray, new Color(1f, 1f, 1f, 1f)), 0.01, "#777 と白は 4.48（WCAG の例）");
        });
    }
}
