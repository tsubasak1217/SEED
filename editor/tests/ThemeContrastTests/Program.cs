// ============================================================
//  Program.cs — ボタン共通書式の配色を機械的に検査する
//
//  【何を守るテストか】
//  「ホバーした瞬間だけ文字が読めなくなる」不具合の再発を防ぐ。
//  通常・ホバー・押下・無効・フォーカス、主操作・完了・危険操作・
//  リンク風・アイコン専用・トグル ON の全状態について、
//  背景と文字のコントラスト比が基準（WCAG AA）を満たすことを確かめる。
//
//  【検査対象の出所】
//  SeedColorTable.ContrastCases が状態の一覧を持つ。
//  スタイルを足したらそこへ 1 行足すこと（足さないと検査されない）。
//  XAML は同じ表を {x:Static} 経由で参照しているので、
//  ここが通れば画面の色も同じ値になる。
//
//  実行:  dotnet run --project editor/tests/ThemeContrastTests
// ============================================================

using System.Globalization;
using SEEDEditor.Theme;
using SpriteRigTests;

namespace SEEDEditor.Tests.ThemeContrast;

/// <summary>
/// 配色検査のエントリポイント。
/// </summary>
public static class Program
{
    /// <summary>コントラスト比の表示桁数。</summary>
    private const string RATIO_FORMAT = "F2";

    /// <summary>
    /// 全検査を実行する。
    /// </summary>
    /// <returns>すべて成功なら 0。</returns>
    public static int Main()
    {
        var harness = new TestHarness();

        Console.WriteLine("=== ボタン共通書式の配色検査 ===");
        Console.WriteLine();

        // ── 1. 状態ごとのコントラスト比 ──────────────────────
        foreach (var c in SeedColorTable.ContrastCases)
        {
            // ループ変数をラムダへ閉じ込めるためコピーする
            var testCase = c;
            harness.Add($"コントラスト: {testCase.StateName}", () =>
            {
                var effectiveBg = SeedColorTable.CompositeOver(
                    testCase.OverlayArgbHex, testCase.BackgroundHex);
                var ratio = ContrastMath.Ratio(effectiveBg, testCase.ForegroundHex);

                Check.True(
                    ratio >= testCase.MinimumRatio,
                    $"{testCase.StateName}: 背景 {effectiveBg} × 前景 {testCase.ForegroundHex} の比は "
                    + $"{ratio.ToString(RATIO_FORMAT, CultureInfo.InvariantCulture)} で、"
                    + $"下限 {testCase.MinimumRatio.ToString(RATIO_FORMAT, CultureInfo.InvariantCulture)} を下回る");
            });
        }

        // ── 2. 表そのものの健全性 ────────────────────────────
        harness.Add("検査項目が十分な数ある", () =>
            Check.True(
                SeedColorTable.ContrastCases.Count >= MINIMUM_CASE_COUNT,
                $"検査項目が {SeedColorTable.ContrastCases.Count} 件しかない"
                + $"（スタイルを足して ContrastCases への追加を忘れていないか）"));

        harness.Add("状態名が重複していない", () =>
        {
            var seen = new HashSet<string>();
            foreach (var c in SeedColorTable.ContrastCases)
                Check.True(seen.Add(c.StateName), $"状態名が重複している: {c.StateName}");
        });

        harness.Add("全状態の色が 16 進として解釈できる", () =>
        {
            foreach (var c in SeedColorTable.ContrastCases)
            {
                SeedColorTable.Parse(c.BackgroundHex);
                SeedColorTable.Parse(c.ForegroundHex);
                if (c.OverlayArgbHex != null) SeedColorTable.Parse(c.OverlayArgbHex);
            }
        });

        // ── 3. 「ホバーで文字が消える」不具合そのものの検出 ──
        //     WPF 既定テンプレートのホバー色と、エディタの文字色の組み合わせは
        //     基準を満たさない。これを満たしてしまうようなら、
        //     テストの計算側が壊れている。
        harness.Add("WPF 既定のホバー色 × エディタの文字色は基準を満たさない（検出できることの確認）", () =>
        {
            var ratio = ContrastMath.Ratio(WPF_DEFAULT_HOVER_BG, SeedColorTable.BUTTON_FG);
            Check.True(
                ratio < SeedColorTable.MIN_RATIO_TEXT,
                $"検査が機能していない: WPF 既定ホバー {WPF_DEFAULT_HOVER_BG} × 文字 {SeedColorTable.BUTTON_FG} の比が "
                + $"{ratio.ToString(RATIO_FORMAT, CultureInfo.InvariantCulture)} で基準を満たしてしまっている");
        });

        // ── 4. 合成計算の妥当性 ──────────────────────────────
        harness.Add("半透明の重ね合わせ: 完全不透明なら前面色になる", () =>
            Check.Equal("#FFFFFF",
                        SeedColorTable.CompositeOver("#FFFFFFFF", "#000000"),
                        "不透明な白を黒へ重ねた結果"));

        harness.Add("半透明の重ね合わせ: 完全透明なら下地のままになる", () =>
            Check.Equal("#123456",
                        SeedColorTable.CompositeOver("#00FFFFFF", "#123456"),
                        "透明な白を重ねた結果"));

        harness.Add("コントラスト比: 黒と白は 21 になる", () =>
            Check.Close(BLACK_WHITE_RATIO,
                        ContrastMath.Ratio("#000000", "#FFFFFF"),
                        RATIO_TOLERANCE,
                        "黒と白のコントラスト比"));

        harness.Add("コントラスト比: 同じ色どうしは 1 になる", () =>
            Check.Close(SAME_COLOR_RATIO,
                        ContrastMath.Ratio(SeedColorTable.BUTTON_BG, SeedColorTable.BUTTON_BG),
                        RATIO_TOLERANCE,
                        "同色のコントラスト比"));

        var exitCode = harness.Run();

        // 参考表示: 全状態の実測値（基準を満たしていても値が見えるようにする）
        Console.WriteLine();
        Console.WriteLine("── 実測値 ──");
        foreach (var c in SeedColorTable.ContrastCases)
        {
            var bg = SeedColorTable.CompositeOver(c.OverlayArgbHex, c.BackgroundHex);
            var ratio = ContrastMath.Ratio(bg, c.ForegroundHex);
            Console.WriteLine(
                $"  {c.StateName,-28} 背景 {bg}  文字 {c.ForegroundHex}  "
                + $"比 {ratio.ToString(RATIO_FORMAT, CultureInfo.InvariantCulture),5}"
                + $" (下限 {c.MinimumRatio.ToString(RATIO_FORMAT, CultureInfo.InvariantCulture)})");
        }

        return exitCode;
    }

    /// <summary>検査項目の最低件数（スタイル追加時の登録漏れ検出用）。</summary>
    private const int MINIMUM_CASE_COUNT = 25;

    /// <summary>
    /// WPF 既定の Button テンプレートがホバー時に塗る色（Aero2 テーマ）。
    /// 今回の不具合の原因そのもの。検査が機能していることの確認に使う。
    /// </summary>
    private const string WPF_DEFAULT_HOVER_BG = "#BEE6FD";

    /// <summary>黒と白のコントラスト比（規格上の最大値）。</summary>
    private const double BLACK_WHITE_RATIO = 21.0;

    /// <summary>同じ色どうしのコントラスト比。</summary>
    private const double SAME_COLOR_RATIO = 1.0;

    /// <summary>コントラスト比の比較に使う許容誤差。</summary>
    private const double RATIO_TOLERANCE = 0.01;
}
