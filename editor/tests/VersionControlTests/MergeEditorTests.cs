// ============================================================
//  MergeEditorTests.cs — マージエディタの純粋ロジックの単体テスト
//
//  【ここで固定していること】
//   1. 印つきテキストの分解（|||||||（元）の有無・複数ブロック・壊れた印）
//   2. 行差分（共通 / 追加 / 削除）と、巨大ブロックの退避
//   3. 合成（取り込み元だけ / 現在だけ / 両方 / どちらも選ばない）と、
//      **両方採ったときの並び順が出どころで入れ替わること**
//   4. 「両方を取り込む」の可否（**両側とも元へ挿入だけ** のときだけ可）と、
//      元を骨格にした union 合成（元の行が二重にならないこと）
//   5. 検査（印の残存 / JSON の構文 / 同一オブジェクト内のキー重複）
//   6. CRLF・BOM・末尾改行なし の保持
//   7. プロバイダ経路（印が残っていたら Lore を呼ばない）
//
//  【なぜここが重要か】
//  この層は **アセットそのものを上書きする**。間違えると
//  「エディタでは開けるがゲームが起動しない .scene」が静かに出来上がる。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Lore;
using SEEDEditor.VersionControl.Lore.Backend;
using SEEDEditor.VersionControl.Merge;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Scheduling;
using ProjectSystemTests;   // TempDir（一時フォルダ。ProjectSystemTests と共用）
using SpriteRigTests;

namespace SEEDEditor.Tests.VersionControl;

/// <summary>
/// マージエディタの純粋ロジックのテスト。
/// </summary>
public static class MergeEditorTests
{
    /// <summary>テストで使う競合ファイルのパス（拡張子で JSON 検査が効く）。</summary>
    private const string SCENE_PATH = "assets/scenes/test.scene";

    /// <summary>JSON ではない拡張子（検査が構文まで見ないことの確認用）。</summary>
    private const string TEXT_PATH = "docs/note.txt";

    /// <summary>全テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        // ── 1. 分解 ──────────────────────────────────────────
        harness.Add("マージ: 元(|||||||)つきの印を 3 つの側へ分解できる", ParsesThreeWayBlock);
        harness.Add("マージ: 元の節が無い印は元を空として分解する", ParsesAddOnlyBlock);
        harness.Add("マージ: 共通部分と複数ブロックが順番どおりに並ぶ", ParsesMultipleBlocks);
        harness.Add("マージ: 閉じていない印は明示的なエラーになる", RejectsUnterminatedBlock);
        harness.Add("マージ: 入れ子の印は明示的なエラーになる", RejectsNestedBlock);
        harness.Add("マージ: 対応する始まりが無い印はエラーになる", RejectsOrphanMarker);

        // ── 2. 行差分 ────────────────────────────────────────
        harness.Add("マージ: 行差分が共通/追加/削除を正しく色分けする", DiffsLines);
        harness.Add("マージ: 元が空なら全行が追加になる", DiffsAgainstEmptyBase);
        harness.Add("マージ: 巨大ブロックは全行変更扱いへ退避する", DiffsFallsBackOnHugeBlock);

        // ── 2b. 上段 2 面の行揃え ────────────────────────────
        harness.Add("マージ: 上段 2 面の行数が必ず一致する", AlignsBothSidesToSameRowCount);
        harness.Add("マージ: 短い側に詰め物が入る", PadsShorterSide);
        harness.Add("マージ: 共通部分は両側に同じ行が入る", KeepsCommonRowsOnBothSides);

        // ── 3. 合成 ──────────────────────────────────────────
        harness.Add("マージ: 取り込み元だけを採ると現在側が消える", ComposesIncomingOnly);
        harness.Add("マージ: 現在だけを採ると取り込み元が消える", ComposesCurrentOnly);
        harness.Add("マージ: sync の両方採用は 取り込み元 → 現在 の順", ComposesBothForSync);
        harness.Add("マージ: ブランチのマージの両方採用は 現在 → 取り込み元 の順",
                    ComposesBothForBranchMerge);
        harness.Add("マージ: どちらも選ばないと元へ戻る", ComposesNeitherAsBase);
        harness.Add("マージ: 元が空でどちらも選ばないと何も残らない", ComposesNeitherWithEmptyBase);
        harness.Add("マージ: 未選択のブロック数を数えられる", CountsUnselectedBlocks);

        // ── 4. 「両方を取り込む」の可否と union 合成 ──────────
        harness.Add("マージ: 元が空なら両方を取り込める", AllowsTakeBothForAddOnly);
        harness.Add("マージ: 同じ箇所を両方が変更したら両方を取り込めない", BlocksTakeBothForEdits);
        harness.Add("マージ: 印が無いファイルは両方を取り込めない", BlocksTakeBothWithoutMarkers);
        harness.Add("マージ: 元が空でなくても両側とも挿入だけなら両方を取り込める",
                    AllowsTakeBothWhenBothSidesOnlyInsert);
        harness.Add("マージ: 片側が元の行を書き換えていたら両方を取り込めない",
                    BlocksTakeBothWhenBaseLineChanged);
        harness.Add("マージ: 挿入位置が違っても元を骨格に両方が入る（union）",
                    UnionsInsertionsAtDifferentPositions);
        harness.Add("マージ: 実機のシーン競合を union で両方取り込む（アクター 4 体）",
                    UnionsRealSceneConflict);
        harness.Add("マージ: union の並び順もブランチのマージで入れ替わる",
                    UnionsRealSceneConflictForBranchMerge);

        // ── 5. 検査 ──────────────────────────────────────────
        harness.Add("マージ: 印が残っていたら検査で落ちる", ValidationCatchesRemainingMarkers);
        harness.Add("マージ: 壊れた JSON は検査で落ちる", ValidationCatchesBrokenJson);
        harness.Add("マージ: 同じオブジェクトのキー重複は検査で落ちる", ValidationCatchesDuplicateKey);
        harness.Add("マージ: 別のオブジェクトの同名キーは通る", ValidationAllowsSameKeyInDifferentObjects);
        harness.Add("マージ: JSON でない拡張子は構文を見ない", ValidationSkipsNonJson);
        harness.Add("マージ: 両方採用でキーが重複する .scene を落とす", ValidationCatchesTakeBothDuplicate);
        harness.Add("マージ: 実機の .scene の競合を両方採用して正しい JSON になる",
                    ComposesRealWorldSceneConflict);

        // ── 6. 改行と BOM ────────────────────────────────────
        harness.Add("マージ: CRLF のファイルは CRLF のまま戻る", PreservesCrlf);
        harness.Add("マージ: BOM の有無を覚えている", RemembersBom);
        harness.Add("マージ: 末尾に改行が無いファイルは無いまま戻る", PreservesMissingFinalNewLine);
        harness.Add("マージ: 触っていない共通部分は 1 バイトも変わらない", RoundTripsUntouchedCommonLines);

        // ── 7. プロバイダ経路 ────────────────────────────────
        harness.Add("マージ: 印が残ったテキストでは Lore を呼ばない", ProviderRejectsMarkedContent);
        harness.Add("マージ: 合成結果を書き込んで解決しコミットまで進む", ProviderResolvesWithContent);
        harness.Add("マージ: 解決できていなければ成功と言わない", ProviderFailsWhenStillConflicted);
        harness.Add("マージ: もう競合していないファイルは上書きしない",
                    ProviderRefusesAlreadyResolvedFile);
        harness.Add("マージ: 両方を取り込むが 1 件でも不可なら何も書かない",
                    ProviderTakeBothIsAllOrNothing);
        harness.Add("マージ: 両方を取り込むと書き込み・解決・コミットまで進む（実測の status の形）",
                    ProviderTakeBothResolvesAndCommits);
        harness.Add("マージ: 出どころの印に取り込み元ブランチ名を残せる", StoresMergeSourceBranch);
        harness.Add("マージ: 1 行だけの古い印も読める", ReadsLegacySingleLineOrigin);
    }

    // ============================================================
    //  1. 分解
    // ============================================================

    /// <summary>元（|||||||）つきの印が 3 つの側へ分かれること。</summary>
    private static void ParsesThreeWayBlock()
    {
        var document = ConflictMarkerDocument.Parse(Text(
            "head",
            "<<<<<<< ours",
            "mine-1",
            "||||||| original",
            "base-1",
            "=======",
            "theirs-1",
            ">>>>>>> theirs",
            "tail"));

        Check.Equal(3, document.Segments.Count, "区画の数");
        Check.Equal(1, document.ConflictCount, "競合ブロックの数");

        var block = document.Conflicts[0];
        Check.Equal("mine-1",   Single(block.CurrentLines),  "現在側の行");
        Check.Equal("base-1",   Single(block.BaseLines),     "元の行");
        Check.Equal("theirs-1", Single(block.IncomingLines), "取り込み元の行");
        Check.True(!block.HasEmptyBase, "元の節がある");
        Check.True(!MergeTakeBothRule.IsUnionable(block),
                   "元の行が書き換わっているので「挿入だけ」ではない");
    }

    /// <summary>元の節が無い印（Lore が実際に書く形）を扱えること。</summary>
    private static void ParsesAddOnlyBlock()
    {
        var document = ConflictMarkerDocument.Parse(Text(
            "<<<<<<< ours",
            "added-by-b",
            "||||||| original",
            "=======",
            "added-by-a",
            ">>>>>>> theirs"));

        var block = document.Conflicts[0];
        Check.Equal(0, block.BaseLines.Count, "元の行数");
        Check.True(block.HasEmptyBase, "元の節が空");
        Check.True(MergeTakeBothRule.IsUnionable(block), "追加どうしの競合と判定される");
        Check.Equal("added-by-b", Single(block.CurrentLines),  "現在側");
        Check.Equal("added-by-a", Single(block.IncomingLines), "取り込み元側");
    }

    /// <summary>共通部分と複数ブロックが順序どおりに並ぶこと。</summary>
    private static void ParsesMultipleBlocks()
    {
        var document = ConflictMarkerDocument.Parse(Text(
            "a",
            "<<<<<<< ours", "c1", "=======", "t1", ">>>>>>> theirs",
            "b",
            "<<<<<<< ours", "c2", "=======", "t2", ">>>>>>> theirs"));

        Check.Equal(4, document.Segments.Count, "区画の数（共通 a / 競合 / 共通 b / 競合）");
        Check.Equal(2, document.ConflictCount, "競合ブロックの数");
        Check.Equal(0, document.Conflicts[0].ConflictIndex, "1 つ目の通し番号");
        Check.Equal(1, document.Conflicts[1].ConflictIndex, "2 つ目の通し番号");
        Check.Equal(MergeSegmentKind.Common, document.Segments[0].Kind, "先頭は共通部分");
    }

    /// <summary>閉じていない印が例外になること。</summary>
    private static void RejectsUnterminatedBlock()
    {
        var threw = false;
        try
        {
            ConflictMarkerDocument.Parse(Text("<<<<<<< ours", "x", "=======", "y"));
        }
        catch (MergeParseException)
        {
            threw = true;
        }
        Check.True(threw, "閉じていない印で MergeParseException が飛ぶ");
    }

    /// <summary>入れ子の印が例外になること。</summary>
    private static void RejectsNestedBlock()
    {
        var ok = ConflictMarkerDocument.TryParse(
            Text("<<<<<<< ours", "<<<<<<< ours", "x", "=======", "y", ">>>>>>> theirs"),
            out _, out var error);

        Check.True(!ok, "入れ子は解析できない");
        Check.True(error.Length > 0, "理由が返る");
    }

    /// <summary>ブロック外の印が例外になること。</summary>
    private static void RejectsOrphanMarker()
    {
        var ok = ConflictMarkerDocument.TryParse(
            Text("a", ">>>>>>> theirs", "b"), out _, out var error);

        Check.True(!ok, "対応する始まりが無い印は解析できない");
        Check.True(error.Length > 0, "理由が返る");
    }

    // ============================================================
    //  2. 行差分
    // ============================================================

    /// <summary>共通・追加・削除が正しく付くこと。</summary>
    private static void DiffsLines()
    {
        var diff = MergeLineDiff.Diff(
            new[] { "a", "b", "c" },
            new[] { "a", "x", "c" });

        var kinds = diff.Select(d => d.Kind).ToArray();
        Check.Equal(MergeDiffKind.Common,  kinds[0], "1 行目は共通");
        Check.Equal(MergeDiffKind.Removed, kinds[1], "2 行目は削除（b）");
        Check.Equal(MergeDiffKind.Added,   kinds[2], "3 行目は追加（x）");
        Check.Equal(MergeDiffKind.Common,  kinds[3], "4 行目は共通");
        Check.Equal("b", diff[1].Text, "消えた行の中身");
        Check.Equal("x", diff[2].Text, "足された行の中身");
    }

    /// <summary>元が空なら全部追加になること。</summary>
    private static void DiffsAgainstEmptyBase()
    {
        var diff = MergeLineDiff.Diff(Array.Empty<string>(), new[] { "a", "b" });

        Check.Equal(2, diff.Count, "行数");
        Check.True(diff.All(d => d.Kind == MergeDiffKind.Added), "全部が追加になる");
    }

    /// <summary>大きすぎるブロックで LCS を諦め、全行を変更扱いにすること。</summary>
    private static void DiffsFallsBackOnHugeBlock()
    {
        // 上限（マス数）を確実に超える大きさにする。
        var side = (int)Math.Sqrt(MergeLineDiff.MAX_TABLE_CELLS) + 2;
        var a    = Enumerable.Range(0, side).Select(i => $"line-{i}").ToArray();
        var b    = Enumerable.Range(0, side).Select(i => $"line-{i}").ToArray();

        var diff = MergeLineDiff.Diff(a, b);

        Check.Equal(a.Length + b.Length, diff.Count, "退避時の行数（元 + 側）");
        Check.True(diff.All(d => d.Kind != MergeDiffKind.Common),
                   "退避したら共通行は作らない（全行を変更扱い）");
    }

    // ============================================================
    //  2b. 上段 2 面の行揃え
    // ============================================================

    /// <summary>
    /// 左右の表示行数が必ず一致すること
    /// （一致していないと同期スクロールが意味を失い、違う場所どうしを見比べさせる）。
    /// </summary>
    private static void AlignsBothSidesToSameRowCount()
    {
        var view = MergeAlignedView.Build(ConflictMarkerDocument.Parse(Text(
            "head",
            "<<<<<<< ours", "m1", "m2", "m3", "||||||| original", "=======", "t1",
            ">>>>>>> theirs",
            "tail")));

        Check.Equal(view.IncomingRows.Count, view.CurrentRows.Count, "左右の表示行数");
        Check.Equal(1, view.Blocks.Count, "ブロックの数");
        Check.Equal(3, view.Blocks[0].RowCount, "ブロックが占める行数（長い側に揃う）");
    }

    /// <summary>短い側に詰め物が入ること。</summary>
    private static void PadsShorterSide()
    {
        var view = MergeAlignedView.Build(ConflictMarkerDocument.Parse(Text(
            "<<<<<<< ours", "m1", "m2", "m3", "||||||| original", "=======", "t1",
            ">>>>>>> theirs")));

        var padded = view.IncomingRows.Count(r => r.Kind == MergeRowKind.Padding);
        Check.Equal(2, padded, "取り込み元側に入る詰め物の行数");
        Check.Equal(0, view.CurrentRows.Count(r => r.Kind == MergeRowKind.Padding),
                    "長い側には詰め物を入れない");
    }

    /// <summary>共通部分が両側に同じ行として入ること。</summary>
    private static void KeepsCommonRowsOnBothSides()
    {
        var view = MergeAlignedView.Build(ConflictMarkerDocument.Parse(Text(
            "head",
            "<<<<<<< ours", "m", "||||||| original", "=======", "t", ">>>>>>> theirs",
            "tail")));

        Check.Equal("head", view.IncomingRows[0].Text, "取り込み元側の 1 行目");
        Check.Equal("head", view.CurrentRows[0].Text,  "現在側の 1 行目");
        Check.Equal(-1, view.IncomingRows[0].ConflictIndex, "共通部分はブロックに属さない");
        Check.Equal(0,  view.IncomingRows[1].ConflictIndex, "2 行目は 1 つ目のブロック");
    }

    // ============================================================
    //  3. 合成
    // ============================================================

    /// <summary>取り込み元だけを採ったときの結果。</summary>
    private static void ComposesIncomingOnly()
    {
        var document = ParseSample();
        var text = MergeComposer.Compose(
            document, new[] { MergeBlockChoice.IncomingOnly }, MergeOrigin.Sync);

        Check.Equal(Text("head", "theirs-1", "tail"), text, "取り込み元だけが残る");
    }

    /// <summary>現在だけを採ったときの結果。</summary>
    private static void ComposesCurrentOnly()
    {
        var document = ParseSample();
        var text = MergeComposer.Compose(
            document, new[] { MergeBlockChoice.CurrentOnly }, MergeOrigin.Sync);

        Check.Equal(Text("head", "mine-1", "tail"), text, "現在だけが残る");
    }

    /// <summary>sync では 取り込み元 → 現在 の順に並ぶこと。</summary>
    private static void ComposesBothForSync()
    {
        var document = ParseSample();
        var text = MergeComposer.Compose(
            document, new[] { MergeBlockChoice.Both }, MergeOrigin.Sync);

        Check.Equal(Text("head", "theirs-1", "mine-1", "tail"), text,
                    "sync は「既に共有されていた側（取り込み元）」が先");
    }

    /// <summary>ブランチのマージでは 現在 → 取り込み元 の順に並ぶこと。</summary>
    private static void ComposesBothForBranchMerge()
    {
        var document = ParseSample();
        var text = MergeComposer.Compose(
            document, new[] { MergeBlockChoice.Both }, MergeOrigin.BranchMerge);

        Check.Equal(Text("head", "mine-1", "theirs-1", "tail"), text,
                    "ブランチのマージは「既に共有されていた側（現在）」が先");
    }

    /// <summary>どちらも選ばないと元へ戻ること。</summary>
    private static void ComposesNeitherAsBase()
    {
        var document = ConflictMarkerDocument.Parse(Text(
            "head",
            "<<<<<<< ours", "mine", "||||||| original", "base", "=======", "theirs",
            ">>>>>>> theirs",
            "tail"));

        var text = MergeComposer.Compose(
            document, new[] { MergeBlockChoice.Neither }, MergeOrigin.Sync);

        Check.Equal(Text("head", "base", "tail"), text, "元の内容が入る");
    }

    /// <summary>元が空でどちらも選ばないと何も残らないこと。</summary>
    private static void ComposesNeitherWithEmptyBase()
    {
        var document = ParseSample();
        var text = MergeComposer.Compose(
            document, new[] { MergeBlockChoice.Neither }, MergeOrigin.Sync);

        Check.Equal(Text("head", "tail"), text, "どちらの追加も入らない");
    }

    /// <summary>未選択のブロック数を数えられること。</summary>
    private static void CountsUnselectedBlocks()
    {
        var choices = new[]
        {
            MergeBlockChoice.Neither, MergeBlockChoice.Both, MergeBlockChoice.Neither,
        };
        Check.Equal(2, MergeComposer.CountUnselected(choices), "未選択の数");
    }

    // ============================================================
    //  4. 「両方を取り込む」の可否
    // ============================================================

    /// <summary>元の節が空なら両方を取り込めること（従来からの特別な場合）。</summary>
    private static void AllowsTakeBothForAddOnly()
    {
        var verdict = MergeTakeBothRule.Evaluate(Text(
            "<<<<<<< ours", "b", "||||||| original", "=======", "a", ">>>>>>> theirs"));

        Check.True(verdict.Allowed, "元が空なら可能");
    }

    /// <summary>同じ箇所を両方が変更したら両方を取り込めないこと。</summary>
    private static void BlocksTakeBothForEdits()
    {
        var verdict = MergeTakeBothRule.Evaluate(Text(
            "<<<<<<< ours", "b", "||||||| original", "orig", "=======", "a", ">>>>>>> theirs"));

        Check.True(!verdict.Allowed, "元の行が書き換わっていれば不可");
        Check.Equal(VersionControlMessages.MERGE_TAKE_BOTH_NOT_ADD_ONLY, verdict.Reason, "理由");
    }

    /// <summary>
    /// ★本命: 元の節が空でなくても、**両側とも挿入だけ**なら両方を取り込めること。
    ///
    /// <para>
    /// 実際のシーンで 2 人が配列末尾へアクターを足すと、diff3 は直前のアクターの
    /// 閉じ行を巻き込むので元の節が空にならない。「元が空」を条件にしていた頃は、
    /// 利用者の主用途でこの機能が **一度も使えなかった**。
    /// </para>
    /// </summary>
    private static void AllowsTakeBothWhenBothSidesOnlyInsert()
    {
        var document = ConflictMarkerDocument.Parse(RealSceneConflict());
        var block    = document.Conflicts[0];

        Check.True(!block.HasEmptyBase, "元の節は空ではない（実機で採取した形）");
        Check.Equal(2, block.BaseLines.Count, "元の行数");
        Check.True(MergeTakeBothRule.IsUnionable(block), "両側とも挿入だけなので可能");
        Check.True(MergeTakeBothRule.Evaluate(document).Allowed, "ファイル全体としても可能");
    }

    /// <summary>片側が元の行を書き換えていたら両方を取り込めないこと。</summary>
    private static void BlocksTakeBothWhenBaseLineChanged()
    {
        var verdict = MergeTakeBothRule.Evaluate(Text(
            "<<<<<<< ours", "b1", "CHANGED",
            "||||||| original", "b1", "b2",
            "=======", "b1", "b2", "y",
            ">>>>>>> theirs"));

        Check.True(!verdict.Allowed, "元の b2 が消えている側があるので不可");
        Check.Equal(VersionControlMessages.MERGE_TAKE_BOTH_NOT_ADD_ONLY, verdict.Reason, "理由");
    }

    /// <summary>
    /// 挿入位置が違うとき、元を骨格に **両方が元の位置へ** 差し込まれること。
    /// 単純な連結だと元の行が 2 回出てしまう。
    /// </summary>
    private static void UnionsInsertionsAtDifferentPositions()
    {
        // 現在は先頭へ x、取り込み元は末尾へ y を挿す。
        var document = ConflictMarkerDocument.Parse(Text(
            "<<<<<<< ours", "x", "b1", "b2",
            "||||||| original", "b1", "b2",
            "=======", "b1", "b2", "y",
            ">>>>>>> theirs"));

        Check.True(MergeTakeBothRule.IsUnionable(document.Conflicts[0]), "両側とも挿入だけ");

        var text = MergeComposer.Compose(
            document, MergeComposer.Fill(1, MergeBlockChoice.Both), MergeOrigin.Sync);

        Check.Equal(Text("x", "b1", "b2", "y"), text, "元の行は 1 回だけ、挿入は元の位置へ");
    }

    /// <summary>
    /// ★本命: 実機で採取した競合を「両方を取り込む」と、
    /// 元の行が二重にならず、正しい JSON（アクター 4 体）になること。
    /// </summary>
    private static void UnionsRealSceneConflict()
    {
        var document = ConflictMarkerDocument.Parse(RealSceneConflict());

        var text = MergeComposer.Compose(
            document, MergeComposer.Fill(1, MergeBlockChoice.Both), MergeOrigin.Sync);

        Check.Equal(MergeValidation.NO_MARKER_LINE, MergeValidation.FindMarkerLine(text),
                    "印が 1 つも残らない");

        var validation = MergeValidation.Validate(SCENE_PATH, text);
        Check.True(validation.IsValid, $"JSON として妥当でキーも重複しない（{validation.Message}）");

        Check.Equal(
            "Player,Camera,AddedByOwner,AddedByMe",
            ActorNames(text),
            "アクターは 4 体。sync なので取り込み元（Owner）が先");

        // 元の骨格が二重にならないこと（連結だと閉じ行 `    }` が 1 つ余る）。
        Check.Equal(
            Text(
                "      \"components\": []",
                "    },",
                "    {",
                "      \"name\": \"AddedByOwner\",",
                "      \"transform\": {",
                "        \"position\": [0.0, 5.0, 0.0]",
                "      },",
                "      \"components\": []",
                "    },",
                "    {",
                "      \"name\": \"AddedByMe\",",
                "      \"transform\": {",
                "        \"position\": [0.0, 7.0, 0.0]",
                "      },",
                "      \"components\": []",
                "    }"),
            BlockRegion(text),
            "ブロックの合成結果（元の 2 行を骨格に両側の挿入が入る）");
    }

    /// <summary>
    /// ブランチのマージでは union の並び順も入れ替わること（現在が先）。
    /// </summary>
    private static void UnionsRealSceneConflictForBranchMerge()
    {
        var document = ConflictMarkerDocument.Parse(RealSceneConflict());

        var text = MergeComposer.Compose(
            document, MergeComposer.Fill(1, MergeBlockChoice.Both), MergeOrigin.BranchMerge);

        Check.True(MergeValidation.Validate(SCENE_PATH, text).IsValid, "JSON として妥当");
        Check.Equal(
            "Player,Camera,AddedByMe,AddedByOwner",
            ActorNames(text),
            "ブランチのマージでは現在（Me）が先");
    }

    /// <summary>印が無いファイルでは両方を取り込めないこと。</summary>
    private static void BlocksTakeBothWithoutMarkers()
    {
        var verdict = MergeTakeBothRule.Evaluate(Text("just", "plain", "text"));

        Check.True(!verdict.Allowed, "印が無ければ不可");
        Check.Equal(VersionControlMessages.MERGE_TAKE_BOTH_NO_MARKERS, verdict.Reason, "理由");
    }

    // ============================================================
    //  5. 検査
    // ============================================================

    /// <summary>印が残っていたら落ちること。</summary>
    private static void ValidationCatchesRemainingMarkers()
    {
        var result = MergeValidation.Validate(TEXT_PATH, Text("a", "=======", "b"));

        Check.True(!result.IsValid, "印が残っていたら不合格");
        Check.Equal(2, MergeValidation.FindMarkerLine(Text("a", "=======", "b")), "印の行番号");
    }

    /// <summary>壊れた JSON が落ちること。</summary>
    private static void ValidationCatchesBrokenJson()
    {
        var result = MergeValidation.Validate(SCENE_PATH, "{ \"a\": 1, }");

        Check.True(!result.IsValid, "末尾カンマは不合格");
    }

    /// <summary>同一オブジェクト内のキー重複が落ちること。</summary>
    private static void ValidationCatchesDuplicateKey()
    {
        var result = MergeValidation.Validate(SCENE_PATH, "{ \"name\": \"a\", \"name\": \"b\" }");

        Check.True(!result.IsValid, "キー重複は不合格");
        Check.True(result.Message.Contains("name", StringComparison.Ordinal),
                   "どのキーが重複したかを伝える");
    }

    /// <summary>別のオブジェクトなら同じ名前でも通ること。</summary>
    private static void ValidationAllowsSameKeyInDifferentObjects()
    {
        var json = "{ \"actors\": [ { \"name\": \"a\" }, { \"name\": \"b\" } ] }";
        var result = MergeValidation.Validate(SCENE_PATH, json);

        Check.True(result.IsValid, "別々のオブジェクトの同名キーは正常");
    }

    /// <summary>JSON でない拡張子は構文を見ないこと。</summary>
    private static void ValidationSkipsNonJson()
    {
        var result = MergeValidation.Validate(TEXT_PATH, "{ これは JSON ではない");

        Check.True(result.IsValid, ".txt は構文を見ない");
        Check.True(!MergeValidation.IsJson(TEXT_PATH), ".txt は JSON 扱いしない");
        Check.True(MergeValidation.IsJson(SCENE_PATH), ".scene は JSON 扱いする");
    }

    /// <summary>
    /// 両方採用でキーが重複する .scene を落とすこと
    /// （「両方を取り込む」で最も起きやすい事故の再現）。
    /// </summary>
    private static void ValidationCatchesTakeBothDuplicate()
    {
        var document = ConflictMarkerDocument.Parse(Text(
            "{",
            "<<<<<<< ours",
            "  \"name\": \"B\"",
            "||||||| original",
            "=======",
            "  \"name\": \"A\",",
            ">>>>>>> theirs",
            "}"));

        var text = MergeComposer.Compose(
            document, MergeComposer.Fill(1, MergeBlockChoice.Both), MergeOrigin.Sync);
        var result = MergeValidation.Validate(SCENE_PATH, text);

        Check.True(!result.IsValid, "同じキーが 2 つ並ぶので不合格");
    }

    /// <summary>
    /// 実機で採取した `.scene` の競合（2 人が配列末尾へ別のアクタを足した）を
    /// 「両方を取り込む」で解決すると、印が消えて正しい JSON になること。
    ///
    /// <para>
    /// この形がマージエディタを作った動機そのもの。2 択ではどちらかのアクタが必ず消える。
    /// </para>
    /// </summary>
    private static void ComposesRealWorldSceneConflict()
    {
        var source = Text(
            "{",
            "  \"format_version\": 2,",
            "  \"actors\": [",
            "    {",
            "      \"name\": \"Player\",",
            "      \"x\": 0.0,",
            "      \"y\": 6.0",
            "<<<<<<< ours",
            "    },",
            "    {",
            "      \"name\": \"AddedByB\",",
            "      \"x\": 9.0",
            "||||||| original",
            "=======",
            "    },",
            "    {",
            "      \"name\": \"AddedByA\",",
            "      \"x\": 0.0",
            ">>>>>>> theirs",
            "    }",
            "  ]",
            "}");

        var document = ConflictMarkerDocument.Parse(source);
        Check.Equal(1, document.ConflictCount, "競合ブロックの数");
        Check.True(MergeTakeBothRule.Evaluate(document).Allowed, "両方を取り込める形である");

        var text = MergeComposer.Compose(
            document, MergeComposer.Fill(1, MergeBlockChoice.Both), MergeOrigin.Sync);

        Check.Equal(MergeValidation.NO_MARKER_LINE, MergeValidation.FindMarkerLine(text),
                    "印が 1 つも残らない");
        Check.True(MergeValidation.Validate(SCENE_PATH, text).IsValid,
                   "JSON として正しく、キーも重複しない");
        Check.True(text.Contains("AddedByA", StringComparison.Ordinal), "A のアクタが残る");
        Check.True(text.Contains("AddedByB", StringComparison.Ordinal), "B のアクタが残る");

        // sync なので「取り込み元（= サーバに既にある A）」が先に来る。
        Check.True(
            text.IndexOf("AddedByA", StringComparison.Ordinal)
            < text.IndexOf("AddedByB", StringComparison.Ordinal),
            "sync では取り込み元が先に並ぶ");
    }

    // ============================================================
    //  6. 改行と BOM
    // ============================================================

    /// <summary>CRLF のファイルが CRLF のまま戻ること。</summary>
    private static void PreservesCrlf()
    {
        var source = "head\r\n<<<<<<< ours\r\nmine\r\n=======\r\ntheirs\r\n>>>>>>> theirs\r\ntail\r\n";
        var document = ConflictMarkerDocument.Parse(source);

        Check.Equal(MergeTextLines.CRLF, document.NewLine, "主たる改行は CRLF");

        var text = MergeComposer.Compose(
            document, new[] { MergeBlockChoice.CurrentOnly }, MergeOrigin.Sync);

        Check.Equal("head\r\nmine\r\ntail\r\n", text, "CRLF のまま組み立てる");
        Check.True(!text.Contains("\n\n", StringComparison.Ordinal), "LF が混ざらない");
    }

    /// <summary>BOM の有無を覚えていること。</summary>
    private static void RemembersBom()
    {
        var withBom = MergeTextLines.BOM_CHAR + Text("a");
        Check.True(ConflictMarkerDocument.Parse(withBom).HasUtf8Bom, "BOM つきを覚える");
        Check.True(!ConflictMarkerDocument.Parse(Text("a")).HasUtf8Bom, "BOM なしを覚える");

        // 実ファイルでも BOM を保って往復できること。
        var dir  = new TempDir();
        var path = Path.Combine(dir.Path, "bom.json");
        try
        {
            File.WriteAllText(path, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Check.True(MergeFileText.HasUtf8Bom(path), "書いた BOM を検出できる");

            var read = MergeFileText.Read(path);
            Check.True(read.Succeeded, "読み込める");
            Check.True(read.HasUtf8Bom, "読み込み結果も BOM つき");
            Check.Equal("{}", read.Text, "BOM は本文に混ざらない");

            MergeFileText.Write(path, "{ }", withUtf8Bom: true);
            Check.True(MergeFileText.HasUtf8Bom(path), "書き戻しても BOM が残る");
        }
        finally
        {
            dir.Dispose();
        }
    }

    /// <summary>末尾に改行が無いファイルが、無いまま戻ること。</summary>
    private static void PreservesMissingFinalNewLine()
    {
        var source = "head\n<<<<<<< ours\nmine\n=======\ntheirs\n>>>>>>> theirs\ntail";
        var document = ConflictMarkerDocument.Parse(source);

        var text = MergeComposer.Compose(
            document, new[] { MergeBlockChoice.CurrentOnly }, MergeOrigin.Sync);

        Check.Equal("head\nmine\ntail", text, "末尾に改行を足さない");
    }

    /// <summary>触っていない共通部分が 1 バイトも変わらないこと（混在改行）。</summary>
    private static void RoundTripsUntouchedCommonLines()
    {
        // CRLF と LF が混ざったファイル（外部ツールが作りがち）。
        var source = "a\r\nb\n<<<<<<< ours\r\nmine\r\n=======\r\ntheirs\r\n>>>>>>> theirs\r\nc\r\nd\n";
        var document = ConflictMarkerDocument.Parse(source);

        var text = MergeComposer.Compose(
            document, new[] { MergeBlockChoice.CurrentOnly }, MergeOrigin.Sync);

        Check.Equal("a\r\nb\nmine\r\nc\r\nd\n", text, "共通部分の改行は行ごとに保たれる");
    }

    // ============================================================
    //  7. プロバイダ経路
    // ============================================================

    /// <summary>印が残っているテキストでは Lore を呼ばないこと。</summary>
    private static void ProviderRejectsMarkedContent()
    {
        var backend  = new FakeLoreBackend();
        using var provider = NewProvider(backend);

        var result = provider
            .ResolveConflictsWithContentAsync(SCENE_PATH, Text("a", "<<<<<<< ours", "b"))
            .GetAwaiter().GetResult();

        Check.True(!result.IsSuccess, "印が残っていたら失敗");
        Check.Equal(0, backend.MergeResolveAsIsCallCount, "Lore を呼んでいない");
        Check.Equal(0, backend.CommitCallCount, "コミットもしていない");
    }

    /// <summary>合成結果を書き込み、解決してコミットまで進むこと。</summary>
    private static void ProviderResolvesWithContent()
    {
        var dir = new TempDir();
        try
        {
            var relative = "a.scene";
            var absolute = Path.Combine(dir.Path, relative);
            File.WriteAllText(absolute, "conflicted");

            var backend = new FakeLoreBackend { WorkingCopyRoot = dir.Path };
            // 1 回目: 書き込む前の確認（まだ競合している）。
            backend.StatusResults.Add(FakeRows.Status(new[]
            {
                FakeRows.File(relative, conflict: true, conflictUnresolved: true),
            }));
            // 2 回目以降: 解決後の status。★実サーバの形に合わせる（2026-09-19 実測）:
            //   resolve <path> の直後は flagConflict=true のまま、unresolved / mine / theirs は偽。
            //   「競合なし（空の一覧）」で書くと、この形を未解決へ倒す誤りを検出できない。
            backend.StatusResults.Add(FakeRows.Status(new[]
            {
                FakeRows.File(relative, staged: true, dirty: false, conflict: true),
            }));

            using var provider = NewProvider(backend);

            var result = provider
                .ResolveConflictsWithContentAsync(relative, "{ \"ok\": true }")
                .GetAwaiter().GetResult();

            Check.True(result.IsSuccess, $"解決できる（{result.Message}）");
            Check.Equal("{ \"ok\": true }", File.ReadAllText(absolute), "結果が書き込まれる");
            Check.Equal(1, backend.MergeResolveAsIsCallCount, "Lore へ 1 回だけ頼む");
            Check.Equal(relative, backend.LastResolveAsIsPaths[0], "頼んだパス");
            Check.Equal(1, backend.CommitCallCount, "残りが無いのでマージをコミットする");
        }
        finally
        {
            dir.Dispose();
        }
    }

    /// <summary>
    /// Lore が成功を返しても、status でまだ競合が残っていれば失敗にすること
    /// （印が残ったまま頼んだときの Lore の嘘を検出する経路）。
    /// </summary>
    private static void ProviderFailsWhenStillConflicted()
    {
        var dir = new TempDir();
        try
        {
            var relative = "a.scene";
            File.WriteAllText(Path.Combine(dir.Path, relative), "conflicted");

            var backend = new FakeLoreBackend { WorkingCopyRoot = dir.Path };
            backend.StatusResults.Add(FakeRows.Status(new[]
            {
                FakeRows.File(relative, conflict: true, conflictUnresolved: true),
            }));

            using var provider = NewProvider(backend);

            var result = provider
                .ResolveConflictsWithContentAsync(relative, "{}")
                .GetAwaiter().GetResult();

            Check.True(!result.IsSuccess, "まだ競合しているなら成功と言わない");
            Check.Equal(0, backend.CommitCallCount, "コミットもしない");
        }
        finally
        {
            dir.Dispose();
        }
    }

    /// <summary>
    /// もう競合していないファイルは書き換えないこと。
    ///
    /// <para>
    /// マージエディタは非モーダルなので、開いたままパネルの 2 択で解決されることがある。
    /// その状態で確定すると、解決済みの中身を古い合成結果で黙って上書きしてしまう。
    /// </para>
    /// </summary>
    private static void ProviderRefusesAlreadyResolvedFile()
    {
        var dir = new TempDir();
        try
        {
            var relative = "a.scene";
            var absolute = Path.Combine(dir.Path, relative);
            File.WriteAllText(absolute, "already-resolved");

            var backend = new FakeLoreBackend { WorkingCopyRoot = dir.Path };
            // status は「もう競合していない」と答える。
            backend.StatusResults.Add(FakeRows.Status(Array.Empty<LoreStatusFileRow>()));

            using var provider = NewProvider(backend);

            var result = provider
                .ResolveConflictsWithContentAsync(relative, "{ \"stale\": true }")
                .GetAwaiter().GetResult();

            Check.True(!result.IsSuccess, "競合していないなら失敗にする");
            Check.Equal("already-resolved", File.ReadAllText(absolute), "ファイルを書き換えない");
            Check.Equal(0, backend.MergeResolveAsIsCallCount, "Lore も呼ばない");
        }
        finally
        {
            dir.Dispose();
        }
    }

    /// <summary>「両方を取り込む」は 1 件でも不可なら何も書かないこと。</summary>
    private static void ProviderTakeBothIsAllOrNothing()
    {
        var dir = new TempDir();
        try
        {
            // 1 件目は追加どうし（可能）、2 件目は同じ箇所の変更（不可）。
            var okPath = "ok.scene";
            var ngPath = "ng.scene";
            var okText = Text("{", "<<<<<<< ours", "\"b\":1,", "||||||| original",
                              "=======", "\"a\":1,", ">>>>>>> theirs", "\"z\":1 }");
            var ngText = Text("<<<<<<< ours", "b", "||||||| original", "orig",
                              "=======", "a", ">>>>>>> theirs");

            File.WriteAllText(Path.Combine(dir.Path, okPath), okText);
            File.WriteAllText(Path.Combine(dir.Path, ngPath), ngText);

            var backend = new FakeLoreBackend { WorkingCopyRoot = dir.Path };
            using var provider = NewProvider(backend);

            var result = provider
                .ResolveConflictsTakingBothAsync(new[] { okPath, ngPath })
                .GetAwaiter().GetResult();

            Check.True(!result.IsSuccess, "1 件でも不可なら失敗");
            Check.Equal(0, backend.MergeResolveAsIsCallCount, "Lore を呼んでいない");
            Check.Equal(okText, File.ReadAllText(Path.Combine(dir.Path, okPath)),
                        "可能だった側も書き換えない");
        }
        finally
        {
            dir.Dispose();
        }
    }

    /// <summary>
    /// 「両方を取り込む」が可能なファイルでは、合成結果を書き込み → Lore へ「中身のまま解決」→
    /// 残りが無ければマージのコミット、まで進むこと。
    ///
    /// <para>
    /// 解決後の status は実サーバの形（<c>flagConflict=true</c> のまま、unresolved / mine / theirs は偽）で
    /// 与える。空の一覧で書くと、その形を「未解決」へ倒す誤りを検出できない（実機で再現した不具合）。
    /// </para>
    /// </summary>
    private static void ProviderTakeBothResolvesAndCommits()
    {
        var dir = new TempDir();
        try
        {
            var relative = "both.scene";
            var absolute = Path.Combine(dir.Path, relative);
            File.WriteAllText(absolute, Text(
                "{", "\"actors\": [",
                "<<<<<<< ours", "\"mine\",", "||||||| original", "=======", "\"theirs\",", ">>>>>>> theirs",
                "\"last\"", "]", "}"));

            var backend = new FakeLoreBackend { WorkingCopyRoot = dir.Path };
            // 解決後の status（実測の形）。
            backend.StatusResults.Add(FakeRows.Status(new[]
            {
                FakeRows.File(relative, staged: true, dirty: false, conflict: true),
            }));

            using var provider = NewProvider(backend);

            var result = provider
                .ResolveConflictsTakingBothAsync(new[] { relative })
                .GetAwaiter().GetResult();

            Check.True(result.IsSuccess, $"解決できる（{result.Message}）");
            var written = File.ReadAllText(absolute);
            Check.True(!written.Contains("<<<<<<<"), "印が消えている");
            // sync 由来（印なし）なので 取り込み元（theirs）→ 現在（ours）の順。
            Check.True(written.IndexOf("\"theirs\"", StringComparison.Ordinal)
                       < written.IndexOf("\"mine\"", StringComparison.Ordinal),
                       "sync では取り込み元が先");
            Check.Equal(1, backend.MergeResolveAsIsCallCount, "Lore へ 1 回だけ頼む");
            Check.Equal(1, backend.CommitCallCount, "残りが無いのでマージをコミットする");
        }
        finally
        {
            dir.Dispose();
        }
    }

    /// <summary>出どころの印に取り込み元のブランチ名が残ること。</summary>
    private static void StoresMergeSourceBranch()
    {
        var dir = new TempDir();
        try
        {
            // `.lore/` が無いと印を書かない（見当違いの場所に cache/ を作らないため）。
            Directory.CreateDirectory(
                Path.Combine(dir.Path, VersionControlSettings.LORE_METADATA_DIR_NAME));

            var store = new LoreMergeOriginStore(dir.Path);
            store.MarkBranchMerge("feature/fishing");

            var context = store.ReadContext();
            Check.Equal(MergeOrigin.BranchMerge, context.Origin, "出どころ");
            Check.Equal("feature/fishing", context.SourceBranch, "取り込み元のブランチ名");

            store.Clear();
            Check.Equal(MergeOrigin.Sync, store.Read(), "消したら sync へ戻る");
        }
        finally
        {
            dir.Dispose();
        }
    }

    /// <summary>ブランチ名を持たない古い印（1 行だけ）も読めること。</summary>
    private static void ReadsLegacySingleLineOrigin()
    {
        var dir = new TempDir();
        try
        {
            var stateDir = Path.Combine(
                new[] { dir.Path }
                    .Concat(VersionControlSettings.EDITOR_VCS_STATE_DIR_SEGMENTS).ToArray());
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(
                Path.Combine(stateDir, LoreMergeOriginStore.FILE_NAME), "branch-merge");

            var context = new LoreMergeOriginStore(dir.Path).ReadContext();

            Check.Equal(MergeOrigin.BranchMerge, context.Origin, "1 行だけでも出どころが読める");
            Check.Equal(string.Empty, context.SourceBranch, "ブランチ名は空");
        }
        finally
        {
            dir.Dispose();
        }
    }

    // ============================================================
    //  共通ヘルパー
    // ============================================================

    /// <summary>行を LF で繋いだテキストを作る（末尾にも改行を付ける）。</summary>
    /// <param name="lines">行の中身。</param>
    private static string Text(params string[] lines)
        => string.Concat(lines.Select(l => l + MergeTextLines.LF));

    /// <summary>1 行しか無いはずの列から、その行の中身を取り出す。</summary>
    /// <param name="lines">行の列。</param>
    private static string Single(IReadOnlyList<MergeLine> lines)
    {
        Check.Equal(1, lines.Count, "行数");
        return lines[0].Text;
    }

    /// <summary>
    /// 実機で採取した `.scene` の競合（アクターが複数行あるファイルで、
    /// 2 人が配列末尾へアクターを足した）。
    ///
    /// <para>
    /// ★diff3 は直前のアクターの閉じ行を巻き込むので、**元の節が空にならない**。
    /// これが利用者の主用途の実際の形で、「元が空」を可否の条件にしていた頃は
    /// 「両方を取り込む」が常に押せなかった。
    /// </para>
    /// </summary>
    private static string RealSceneConflict() => Text(
        "{",
        "  \"format_version\": 2,",
        "  \"actors\": [",
        "    {",
        "      \"name\": \"Player\",",
        "      \"transform\": {",
        "        \"position\": [0.0, 0.0, 0.0]",
        "      },",
        "      \"components\": []",
        "    },",
        "    {",
        "      \"name\": \"Camera\",",
        "      \"transform\": {",
        "        \"position\": [0.0, 3.0, 0.0]",
        "      },",
        "<<<<<<< ours",
        "      \"components\": []",
        "    },",
        "    {",
        "      \"name\": \"AddedByMe\",",
        "      \"transform\": {",
        "        \"position\": [0.0, 7.0, 0.0]",
        "      },",
        "      \"components\": []",
        "    }",
        "||||||| original",
        "      \"components\": []",
        "    }",
        "=======",
        "      \"components\": []",
        "    },",
        "    {",
        "      \"name\": \"AddedByOwner\",",
        "      \"transform\": {",
        "        \"position\": [0.0, 5.0, 0.0]",
        "      },",
        "      \"components\": []",
        "    }",
        ">>>>>>> theirs",
        "  ]",
        "}");

    /// <summary>
    /// 合成結果から、競合ブロックがあった範囲（共通の前後を除いた部分）を取り出す。
    /// <see cref="RealSceneConflict"/> の共通部分は前が 15 行・後ろが 2 行。
    /// </summary>
    /// <param name="composed">合成結果。</param>
    private static string BlockRegion(string composed)
    {
        const int CommonLinesBefore = 15;
        const int CommonLinesAfter  = 2;

        var lines = MergeTextLines.Split(composed);
        var body  = lines.Skip(CommonLinesBefore)
                         .Take(lines.Count - CommonLinesBefore - CommonLinesAfter)
                         .ToArray();
        return MergeTextLines.Join(body, MergeTextLines.LF);
    }

    /// <summary>
    /// 合成結果の JSON からアクター名を順に取り出し、カンマ区切りで返す
    /// （実際に解析して確かめる。テストの表明は配列を要素ごとに比べられないので文字列にする）。
    /// </summary>
    /// <param name="composed">合成結果。</param>
    private static string ActorNames(string composed)
    {
        using var document = System.Text.Json.JsonDocument.Parse(composed);
        var names = document.RootElement
                            .GetProperty("actors")
                            .EnumerateArray()
                            .Select(a => a.GetProperty("name").GetString() ?? string.Empty);
        return string.Join(",", names);
    }

    /// <summary>「元が空・1 ブロック」の見本を解析する。</summary>
    private static ConflictMarkerDocument ParseSample()
        => ConflictMarkerDocument.Parse(Text(
            "head",
            "<<<<<<< ours",
            "mine-1",
            "||||||| original",
            "=======",
            "theirs-1",
            ">>>>>>> theirs",
            "tail"));

    /// <summary>テスト用のプロバイダ（同じスレッドで動くスケジューラを使う）。</summary>
    /// <param name="backend">差し替えたバックエンド。</param>
    private static LoreProvider NewProvider(FakeLoreBackend backend)
        => new(backend, new InlineScheduler(), VersionControlSettings.Default);
}
