// ============================================================
//  ChangeTreeTests.cs — Visual Studio 風パネルの「組み立て」側のテスト
//
//  【ここで固定するもの】
//   1. 変更のパス群 → フォルダー階層のツリー（ChangeTreeBuilder）
//   2. ツリー → 仮想化リストへ流す平坦な行（ChangeTreeFlattener）
//   3. 変更の種類 → 行の右端に出す 1 文字（A / M / D / R / C / !）
//   4. リモートとの前後関係 → 「↑ 未送信 / ↓ 未取得」の 1 行
//   5. 折りたたみ節の見出し・表示可否・既定の開閉・保存キー
//   6. 節の開閉の保存と復元
//
//  【なぜここを固定するのか】
//  どれも「間違えてもビルドが通り、GUI を起動しないと見えない」場所である。
//  特に次の 3 つは、間違えると利用者が事実と違う判断をする:
//   ・見出しの件数と一覧の件数がずれる（どこかに変更が隠れていると思わせる）
//   ・サーバへ問い合わせていないのに「未送信なし」と書く（送り忘れる）
//   ・節の保存キーが変わる（利用者が開いていた状態が黙って失われる）
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ProjectSystemTests;   // TempDir（一時フォルダは ProjectSystemTests のものを共用）
using SEEDEditor.VersionControl;
using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Presentation;
using SpriteRigTests;

namespace SEEDEditor.Tests.VersionControl;

/// <summary>
/// 変更ツリー・上段の 1 行・折りたたみ節のテスト群。
/// </summary>
public static class ChangeTreeTests
{
    /// <summary>テストで使う作業コピーのパス（表示されるだけで実在しなくてよい）。</summary>
    private const string WORKING_COPY = @"D:\SEED_projects\Sample";

    /// <summary>テストをランナーへ登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        // ── ツリーの組み立て ──
        harness.Add("同じフォルダーのファイルは 1 本の枝にまとまる",        SameFolderSharesOneBranch);
        harness.Add("根は作業コピーのパスを名前に持つ",                    RootShowsWorkingCopyPath);
        harness.Add("フォルダーが先・名前順に並ぶ",                        FoldersComeBeforeFiles);
        harness.Add("階層の段数はパスの区切りの数と一致する",              DepthMatchesPathSegments);
        harness.Add("配下のファイル数が集計される",                        FileCountIsAggregated);
        harness.Add("変更が無ければ根だけになる",                          EmptyChangesGivesRootOnly);
        harness.Add("パスが空の変更はツリーに入れない",                    EmptyPathIsDropped);
        harness.Add("区切りはスラッシュでも円記号でも同じ木になる",        BothSeparatorsBuildSameTree);
        harness.Add("大文字小文字が違うだけのフォルダーは 1 つに畳む",      FolderNamesAreCaseInsensitive);

        // ── 平坦化（見えている行）──
        harness.Add("既定ではすべて開いている",                            AllExpandedByDefault);
        harness.Add("畳んだフォルダーの配下は行に出ない",                  CollapsedFolderHidesChildren);
        harness.Add("根を畳むと根の 1 行だけになる",                       CollapsedRootHidesEverything);
        harness.Add("行の深さがインデントの段数になる",                    RowDepthFollowsHierarchy);
        harness.Add("子を持たない行は開いた状態にならない",                LeafIsNeverExpanded);
        harness.Add("畳める節点だけをすべて集められる",                    AllContainerPathsAreCollected);
        harness.Add("ツリーが空なら行も空",                                FlattenNullGivesNoRows);

        // ── 状態の 1 文字 ──
        harness.Add("変更の種類ごとに決まった 1 文字になる",               ChangeLetterPerKind);
        harness.Add("未解決の競合は種類より「!」を優先する",               ConflictLetterWins);
        harness.Add("1 文字は種類ごとに重複しない",                        ChangeLettersAreDistinct);

        // ── 未送信・未取得の 1 行 ──
        harness.Add("オフライン取得では「未確認」と出す",                  SyncOfflineIsUnchecked);
        harness.Add("状態が無ければ「未確認」と出す",                      SyncNullIsUnchecked);
        harness.Add("一致していれば両方「なし」",                          SyncInSyncIsNone);
        harness.Add("ローカルが進んでいれば未送信あり",                    SyncLocalAheadIsUnpushed);
        harness.Add("リモートが進んでいれば未取得あり",                    SyncRemoteAheadIsUnpulled);
        harness.Add("分岐していれば両方あり",                              SyncDivergedIsBoth);
        harness.Add("繋がらなかったときは未確認＋別の説明",                SyncUnavailableExplainsWhy);
        harness.Add("リモートに未作成のブランチは未送信あり",              SyncRemoteMissingIsUnpushed);
        harness.Add("件数が分からないときは数を書かない",                  SyncTextHasNoFakeNumber);

        // ── 折りたたみ節 ──
        harness.Add("節の見出しに件数が入る",                              SectionHeaderHasCount);
        harness.Add("履歴の見出しには件数を出さない",                      HistoryHeaderHasNoCount);
        harness.Add("件数が未取得なら 0 と断定しない",                     UnknownCountIsNotZero);
        harness.Add("競合は 0 件なら節ごと隠す",                           ConflictsSectionHiddenWhenClean);
        harness.Add("競合以外は 0 件でも節を出す",                         OtherSectionsShownWhenEmpty);
        harness.Add("ロックと履歴は既定で閉じている",                      RemoteSectionsStartCollapsed);
        harness.Add("開いたときに取りに行くのはロックと履歴だけ",          OnlyRemoteSectionsFetchOnExpand);
        harness.Add("節と保存キーは 1 対 1 で往復できる",                  SectionKeysRoundTrip);

        // ── 節の開閉の保存 ──
        harness.Add("保存が無ければ既定の開閉で返る",                      StoreFallsBackToDefaults);
        harness.Add("保存した開閉が次回に戻る",                            StoreRoundTripsSections);
        harness.Add("壊れた保存ファイルでも既定で起動する",                StoreSurvivesBrokenFile);
        harness.Add("別プロジェクトの保存は混ざらない",                    StoreSeparatesProjects);
        harness.Add("プロジェクトキーは区切りと大小の揺れを吸収する",      ProjectKeyIsNormalized);
    }

    // ============================================================
    //  ツリーの組み立て
    // ============================================================

    /// <summary>同じフォルダーにある 2 つのファイルは、同じフォルダー節点の下に入る。</summary>
    private static void SameFolderSharesOneBranch()
    {
        var tree = Build("assets/textures/a.png", "assets/textures/b.png");

        Check.Equal(1, tree.Children.Count, "根の子の数");

        var assets = tree.Children[0];
        Check.Equal("assets", assets.Name, "第 1 階層の名前");
        Check.Equal(1, assets.Children.Count, "assets の子の数");

        var textures = assets.Children[0];
        Check.Equal("textures", textures.Name, "第 2 階層の名前");
        Check.Equal(2, textures.Children.Count, "textures の子の数");
    }

    /// <summary>根の行には作業コピーのパスが出る（手本の VS と同じ）。</summary>
    private static void RootShowsWorkingCopyPath()
    {
        var tree = Build("a.txt");

        Check.Equal(ChangeTreeNodeKind.Root, tree.Kind, "根の種類");
        Check.Equal(WORKING_COPY, tree.Name, "根の名前");
        Check.Equal(string.Empty, tree.RelativePath, "根の相対パス");
    }

    /// <summary>フォルダーが先、そのあとファイル。どちらも名前順。</summary>
    private static void FoldersComeBeforeFiles()
    {
        // わざと「ファイルが先・フォルダーは逆順」で渡す。
        var tree = Build("zebra.txt", "alpha.txt", "zzz/inner.txt", "aaa/inner.txt");

        var names = tree.Children.Select(c => c.Name).ToList();

        Check.Equal("aaa",       names[0], "1 番目（フォルダー）");
        Check.Equal("zzz",       names[1], "2 番目（フォルダー）");
        Check.Equal("alpha.txt", names[2], "3 番目（ファイル）");
        Check.Equal("zebra.txt", names[3], "4 番目（ファイル）");
    }

    /// <summary>パスの区切りの数だけ階層が作られる。</summary>
    private static void DepthMatchesPathSegments()
    {
        var tree = Build("a/b/c/d.txt");

        var a = tree.Children[0];
        var b = a.Children[0];
        var c = b.Children[0];
        var d = c.Children[0];

        Check.Equal("a",     a.Name, "第 1 階層");
        Check.Equal("b",     b.Name, "第 2 階層");
        Check.Equal("c",     c.Name, "第 3 階層");
        Check.Equal("d.txt", d.Name, "葉");

        Check.Equal("a/b/c/d.txt", d.RelativePath, "葉の相対パス");
        Check.Equal(ChangeTreeNodeKind.File, d.Kind, "葉の種類");
    }

    /// <summary>配下のファイル数は、根とフォルダーで正しく合算される。</summary>
    private static void FileCountIsAggregated()
    {
        var tree = Build("a/1.txt", "a/2.txt", "b/c/3.txt", "4.txt");

        Check.Equal(4, tree.FileCount, "根の合計");
        Check.Equal(2, tree.Children.First(c => c.Name == "a").FileCount, "a の合計");
        Check.Equal(1, tree.Children.First(c => c.Name == "b").FileCount, "b の合計");
    }

    /// <summary>変更が 1 件も無ければ、根だけで子は無い。</summary>
    private static void EmptyChangesGivesRootOnly()
    {
        var tree = ChangeTreeBuilder.Build(WORKING_COPY, Array.Empty<ChangedFile>());

        Check.Equal(0, tree.Children.Count, "子の数");
        Check.Equal(0, tree.FileCount,      "ファイル数");
        Check.True(!tree.HasChildren,       "子を持たないこと");
    }

    /// <summary>
    /// パスが空の変更（壊れた行）は木に入れない。
    /// 名前の無い行を並べるより、静かに落とす方が画面が壊れない。
    /// </summary>
    private static void EmptyPathIsDropped()
    {
        var tree = ChangeTreeBuilder.Build(WORKING_COPY, new[]
        {
            NewChange(string.Empty, FileChangeKind.Modified),
            NewChange("ok.txt",     FileChangeKind.Modified),
        });

        Check.Equal(1,        tree.Children.Count, "子の数");
        Check.Equal("ok.txt", tree.Children[0].Name, "残った行");
    }

    /// <summary>Lore はスラッシュで返すが、円記号混じりでも同じ木になる。</summary>
    private static void BothSeparatorsBuildSameTree()
    {
        var slash     = Build("a/b/c.txt");
        var backslash = Build(@"a\b\c.txt");

        Check.Equal(
            slash.Children[0].Children[0].Children[0].RelativePath,
            backslash.Children[0].Children[0].Children[0].RelativePath,
            "葉の相対パス");
    }

    /// <summary>
    /// 大文字小文字だけが違うフォルダーは 1 つに畳む。
    /// Windows のファイルシステムでは同じフォルダーなので、
    /// 分けて並べると同じ場所が 2 か所に見える。
    /// </summary>
    private static void FolderNamesAreCaseInsensitive()
    {
        var tree = Build("Assets/a.txt", "assets/b.txt");

        Check.Equal(1, tree.Children.Count, "フォルダーの数");
        Check.Equal(2, tree.Children[0].Children.Count, "その下のファイル数");
    }

    // ============================================================
    //  平坦化（見えている行）
    // ============================================================

    /// <summary>畳み集合が空なら、根も途中のフォルダーも全部開いている。</summary>
    private static void AllExpandedByDefault()
    {
        var rows = ChangeTreeFlattener.Flatten(Build("a/b.txt", "c.txt"));

        // 根 / a / a/b.txt / c.txt の 4 行。
        Check.Equal(4, rows.Count, "行数");
        Check.True(rows[0].IsExpanded, "根が開いていること");
        Check.True(rows[1].IsExpanded, "a が開いていること");
    }

    /// <summary>畳んだフォルダーの配下は 1 行も出ない。</summary>
    private static void CollapsedFolderHidesChildren()
    {
        var tree = Build("a/b.txt", "a/c.txt", "d.txt");
        var rows = ChangeTreeFlattener.Flatten(
            tree, new HashSet<string>(StringComparer.Ordinal) { "a" });

        var paths = rows.Select(r => r.Node.RelativePath).ToList();

        Check.Equal(3, rows.Count, "行数（根 / a / d.txt）");
        Check.True(paths.Contains("a"),      "a の行は残ること");
        Check.True(!paths.Contains("a/b.txt"), "a の配下が隠れること");
        Check.True(!rows[1].IsExpanded,      "a が閉じていること");
    }

    /// <summary>根を畳めば 1 行だけになる。</summary>
    private static void CollapsedRootHidesEverything()
    {
        var rows = ChangeTreeFlattener.Flatten(
            Build("a/b.txt", "c.txt"),
            new HashSet<string>(StringComparer.Ordinal) { string.Empty });

        Check.Equal(1, rows.Count, "行数");
        Check.Equal(ChangeTreeNodeKind.Root, rows[0].Node.Kind, "残る行の種類");
    }

    /// <summary>行の深さは階層と一致する（インデントの段数になる）。</summary>
    private static void RowDepthFollowsHierarchy()
    {
        var rows = ChangeTreeFlattener.Flatten(Build("a/b/c.txt"));

        Check.Equal(0, rows[0].Depth, "根の深さ");
        Check.Equal(1, rows[1].Depth, "a の深さ");
        Check.Equal(2, rows[2].Depth, "b の深さ");
        Check.Equal(3, rows[3].Depth, "c.txt の深さ");
    }

    /// <summary>ファイル行は子を持たないので、開いた状態にはならない。</summary>
    private static void LeafIsNeverExpanded()
    {
        var rows = ChangeTreeFlattener.Flatten(Build("a.txt"));
        var leaf = rows.First(r => r.Node.Kind == ChangeTreeNodeKind.File);

        Check.True(!leaf.IsExpanded, "ファイル行が開いていないこと");
    }

    /// <summary>「すべて折りたたむ」用の集合には、根と全フォルダーが入る。</summary>
    private static void AllContainerPathsAreCollected()
    {
        var paths = ChangeTreeFlattener.AllContainerPaths(Build("a/b/c.txt", "d.txt"));

        Check.True(paths.Contains(string.Empty), "根が入ること");
        Check.True(paths.Contains("a"),          "a が入ること");
        Check.True(paths.Contains("a/b"),        "a/b が入ること");
        Check.True(!paths.Contains("d.txt"),     "ファイルは入らないこと");
        Check.Equal(3, paths.Count,              "畳める節点の数");
    }

    /// <summary>ツリーが無ければ行も無い（null で落ちない）。</summary>
    private static void FlattenNullGivesNoRows()
        => Check.Equal(0, ChangeTreeFlattener.Flatten(null).Count, "行数");

    // ============================================================
    //  状態の 1 文字
    // ============================================================

    /// <summary>種類ごとに決まった 1 文字を返す。</summary>
    private static void ChangeLetterPerKind()
    {
        Check.Equal("A", Letter(FileChangeKind.Added),    "追加");
        Check.Equal("M", Letter(FileChangeKind.Modified), "変更");
        Check.Equal("D", Letter(FileChangeKind.Deleted),  "削除");
        Check.Equal("R", Letter(FileChangeKind.Moved),    "移動");
        Check.Equal("C", Letter(FileChangeKind.Copied),   "複製");
        Check.Equal("?", Letter(FileChangeKind.Unknown),  "不明");
    }

    /// <summary>未解決の競合は、種類が何であれ「!」になる。</summary>
    private static void ConflictLetterWins()
    {
        var letter = VersionControlDisplay.ToChangeLetter(
            FileChangeKind.Modified, FileConflictState.Unresolved);

        Check.Equal("!", letter, "競合の 1 文字");

        // 解決済みの競合は普通の変更として扱う（もう選択は要らない）。
        var resolved = VersionControlDisplay.ToChangeLetter(
            FileChangeKind.Modified, FileConflictState.ResolvedKeepMine);

        Check.Equal("M", resolved, "解決済みの 1 文字");
    }

    /// <summary>1 文字が重複していると、画面上で種類を見分けられなくなる。</summary>
    private static void ChangeLettersAreDistinct()
    {
        var letters = new[]
        {
            Letter(FileChangeKind.Added),
            Letter(FileChangeKind.Modified),
            Letter(FileChangeKind.Deleted),
            Letter(FileChangeKind.Moved),
            Letter(FileChangeKind.Copied),
            VersionControlDisplay.ToChangeLetter(
                FileChangeKind.Modified, FileConflictState.Unresolved),
        };

        Check.Equal(letters.Length, letters.Distinct(StringComparer.Ordinal).Count(), "種類数");
    }

    // ============================================================
    //  未送信・未取得の 1 行
    // ============================================================

    /// <summary>
    /// オフライン取得ではリモートに聞いていない。「なし」と書くと嘘になるので
    /// 「未確認」と出し、どうすれば分かるかを説明する。
    /// </summary>
    private static void SyncOfflineIsUnchecked()
    {
        var summary = VersionControlSyncSummary.From(NewStatus(RemoteComparison.NotChecked));

        Check.Equal(SyncPresence.Unchecked, summary.Unpushed, "未送信");
        Check.Equal(SyncPresence.Unchecked, summary.Unpulled, "未取得");
        Check.True(summary.Tooltip.Length > 0, "説明が付くこと");
    }

    /// <summary>状態そのものが無いときも「未確認」。</summary>
    private static void SyncNullIsUnchecked()
    {
        var summary = VersionControlSyncSummary.From(null);
        Check.Equal(SyncPresence.Unchecked, summary.Unpushed, "未送信");
    }

    /// <summary>一致していれば両方「なし」。</summary>
    private static void SyncInSyncIsNone()
    {
        var summary = VersionControlSyncSummary.From(NewStatus(RemoteComparison.InSync));

        Check.Equal(SyncPresence.None, summary.Unpushed, "未送信");
        Check.Equal(SyncPresence.None, summary.Unpulled, "未取得");
    }

    /// <summary>ローカルが進んでいる＝送るものがある。</summary>
    private static void SyncLocalAheadIsUnpushed()
    {
        var summary = VersionControlSyncSummary.From(NewStatus(RemoteComparison.LocalAhead));

        Check.Equal(SyncPresence.Present, summary.Unpushed, "未送信");
        Check.Equal(SyncPresence.None,    summary.Unpulled, "未取得");
    }

    /// <summary>リモートが進んでいる＝取るものがある。</summary>
    private static void SyncRemoteAheadIsUnpulled()
    {
        var summary = VersionControlSyncSummary.From(NewStatus(RemoteComparison.RemoteAhead));

        Check.Equal(SyncPresence.None,    summary.Unpushed, "未送信");
        Check.Equal(SyncPresence.Present, summary.Unpulled, "未取得");
    }

    /// <summary>分岐していれば両方ある。</summary>
    private static void SyncDivergedIsBoth()
    {
        var summary = VersionControlSyncSummary.From(NewStatus(RemoteComparison.Diverged));

        Check.Equal(SyncPresence.Present, summary.Unpushed, "未送信");
        Check.Equal(SyncPresence.Present, summary.Unpulled, "未取得");
    }

    /// <summary>
    /// 繋がらなかったときも「未確認」だが、理由が違うので説明を分ける
    /// （「更新を押せば分かる」と書いてしまうと、押しても分からず混乱する）。
    /// </summary>
    private static void SyncUnavailableExplainsWhy()
    {
        var unavailable = VersionControlSyncSummary.From(NewStatus(RemoteComparison.Unavailable));
        var notChecked  = VersionControlSyncSummary.From(NewStatus(RemoteComparison.NotChecked));

        Check.Equal(SyncPresence.Unchecked, unavailable.Unpushed, "未送信");
        Check.True(unavailable.Tooltip != notChecked.Tooltip, "説明が別であること");
    }

    /// <summary>リモートに同名ブランチがまだ無い＝手元のものは全部未送信。</summary>
    private static void SyncRemoteMissingIsUnpushed()
    {
        var summary = VersionControlSyncSummary.From(
            NewStatus(RemoteComparison.RemoteBranchMissing));

        Check.Equal(SyncPresence.Present, summary.Unpushed, "未送信");
        Check.Equal(SyncPresence.None,    summary.Unpulled, "未取得");
        Check.True(summary.Tooltip.Length > 0, "初回送信であることの説明");
    }

    /// <summary>
    /// Lore は「何コミット進んでいるか」を返さない。
    /// 数が分からないのに数字を書くと、利用者は嘘の数を信じて判断してしまう。
    /// </summary>
    private static void SyncTextHasNoFakeNumber()
    {
        var summary = VersionControlSyncSummary.From(NewStatus(RemoteComparison.LocalAhead));

        Check.True(summary.UnpushedCount is null, "件数を持たないこと");
        Check.True(!summary.UnpushedText.Any(char.IsDigit), "文言に数字が入らないこと");
    }

    // ============================================================
    //  折りたたみ節
    // ============================================================

    /// <summary>見出しには件数が入る（開かずに規模が分かるように）。</summary>
    private static void SectionHeaderHasCount()
    {
        var header = VersionControlSections.ToHeaderText(VersionControlSection.Changes, 478);

        Check.True(header.Contains("478", StringComparison.Ordinal), "件数が入ること");
        Check.True(header.Contains(VersionControlMessages.PANEL_CHANGE_MODIFIED,
                                   StringComparison.Ordinal),
                   "「変更」の語が入ること");
    }

    /// <summary>
    /// 履歴は「さらに読み込む」で増えるので件数を書かない。
    /// 「履歴 (30)」と出すと、それが全件だと誤解させる。
    /// </summary>
    private static void HistoryHeaderHasNoCount()
    {
        var header = VersionControlSections.ToHeaderText(VersionControlSection.History, 30);

        Check.Equal(VersionControlMessages.PANEL_SECTION_HISTORY, header, "見出し");
    }

    /// <summary>まだ取りに行っていない節を 0 件と断定しない。</summary>
    private static void UnknownCountIsNotZero()
    {
        var header = VersionControlSections.ToHeaderText(VersionControlSection.Locks, null);

        Check.True(header.Contains(VersionControlMessages.PANEL_SECTION_COUNT_UNKNOWN,
                                   StringComparison.Ordinal),
                   "未取得の印が入ること");
        Check.True(!header.Contains("0", StringComparison.Ordinal), "0 と書かないこと");
    }

    /// <summary>競合は例外的な状態。0 件なら節ごと隠して、起きたときに目立たせる。</summary>
    private static void ConflictsSectionHiddenWhenClean()
    {
        Check.True(!VersionControlSections.IsVisible(VersionControlSection.Conflicts, 0),
                   "0 件では隠すこと");
        Check.True(!VersionControlSections.IsVisible(VersionControlSection.Conflicts, null),
                   "未取得でも隠すこと");
        Check.True(VersionControlSections.IsVisible(VersionControlSection.Conflicts, 1),
                   "1 件あれば出すこと");
    }

    /// <summary>競合以外は 0 件でも出す（そこに何があるかを利用者が学べるように）。</summary>
    private static void OtherSectionsShownWhenEmpty()
    {
        Check.True(VersionControlSections.IsVisible(VersionControlSection.Changes, 0), "変更");
        Check.True(VersionControlSections.IsVisible(VersionControlSection.Locks, 0),   "ロック");
        Check.True(VersionControlSections.IsVisible(VersionControlSection.History, null), "履歴");
    }

    /// <summary>
    /// ロックと履歴はサーバ往復が要る。既定で開いていると、
    /// パネルを出しただけで通信が始まってしまう。
    /// </summary>
    private static void RemoteSectionsStartCollapsed()
    {
        Check.True(VersionControlSections.DefaultIsExpanded(VersionControlSection.Conflicts),
                   "競合は開く");
        Check.True(VersionControlSections.DefaultIsExpanded(VersionControlSection.Changes),
                   "変更は開く");
        Check.True(!VersionControlSections.DefaultIsExpanded(VersionControlSection.Locks),
                   "ロックは閉じる");
        Check.True(!VersionControlSections.DefaultIsExpanded(VersionControlSection.History),
                   "履歴は閉じる");
    }

    /// <summary>開いたときに取りに行くのは、サーバ往復が要る 2 つだけ。</summary>
    private static void OnlyRemoteSectionsFetchOnExpand()
    {
        Check.True(!VersionControlSections.NeedsFetchOnExpand(VersionControlSection.Conflicts),
                   "競合");
        Check.True(!VersionControlSections.NeedsFetchOnExpand(VersionControlSection.Changes),
                   "変更");
        Check.True(VersionControlSections.NeedsFetchOnExpand(VersionControlSection.Locks),
                   "ロック");
        Check.True(VersionControlSections.NeedsFetchOnExpand(VersionControlSection.History),
                   "履歴");
    }

    /// <summary>
    /// 保存キーは設定ファイルに書き込む文字列。往復できないと、
    /// 利用者が開いていた節の状態が黙って失われる。
    /// </summary>
    private static void SectionKeysRoundTrip()
    {
        foreach (var section in VersionControlSections.InDisplayOrder)
        {
            var key = VersionControlSections.ToKey(section);
            Check.Equal(section, VersionControlSections.FromKey(key), $"{section} の往復");
        }

        Check.True(VersionControlSections.FromKey("未知のキー") is null, "未知のキー");
    }

    // ============================================================
    //  節の開閉の保存
    // ============================================================

    /// <summary>保存ファイルが無ければ、全部が既定の開閉で返る。</summary>
    private static void StoreFallsBackToDefaults()
    {
        using var temp = new TempDir();

        var store    = VersionControlPanelStateStore.LoadFile(temp.Combine("nothing.json"));
        var sections = store.GetSections("proj");

        foreach (var section in VersionControlSections.InDisplayOrder)
        {
            Check.Equal(
                VersionControlSections.DefaultIsExpanded(section),
                sections[section],
                $"{section} の既定");
        }
    }

    /// <summary>書いた開閉が、読み直したときにそのまま戻る。</summary>
    private static void StoreRoundTripsSections()
    {
        using var temp = new TempDir();
        var path = temp.Combine("state.json");

        // 既定と逆にして保存する（既定値と一致していては往復を検証できない）。
        var written = new Dictionary<VersionControlSection, bool>();
        foreach (var section in VersionControlSections.InDisplayOrder)
        {
            written[section] = !VersionControlSections.DefaultIsExpanded(section);
        }

        var saver = VersionControlPanelStateStore.LoadFile(path);
        saver.SetSections("proj", written);
        Check.True(saver.Save(), "保存できること");

        var loaded = VersionControlPanelStateStore.LoadFile(path).GetSections("proj");
        foreach (var section in VersionControlSections.InDisplayOrder)
        {
            Check.Equal(written[section], loaded[section], $"{section} の往復");
        }
    }

    /// <summary>壊れた JSON を読んでも例外を投げず、既定で起動する（起動を止めない）。</summary>
    private static void StoreSurvivesBrokenFile()
    {
        using var temp = new TempDir();
        var path = temp.Combine("broken.json");
        File.WriteAllText(path, "{ これは JSON ではない");

        var store = VersionControlPanelStateStore.LoadFile(path);

        Check.True(store.Warnings.Count > 0, "警告が残ること");
        Check.Equal(
            VersionControlSections.DefaultIsExpanded(VersionControlSection.Changes),
            store.GetSections("proj")[VersionControlSection.Changes],
            "既定へ倒れること");
    }

    /// <summary>1 つの設定ファイルを複数プロジェクトで共有しても混ざらない。</summary>
    private static void StoreSeparatesProjects()
    {
        using var temp = new TempDir();
        var path = temp.Combine("state.json");

        var store = VersionControlPanelStateStore.LoadFile(path);
        store.SetSections("a", new Dictionary<VersionControlSection, bool>
        {
            [VersionControlSection.History] = true,
        });
        store.SetSections("b", new Dictionary<VersionControlSection, bool>
        {
            [VersionControlSection.History] = false,
        });
        Check.True(store.Save(), "保存できること");

        var loaded = VersionControlPanelStateStore.LoadFile(path);

        Check.True(loaded.GetSections("a")[VersionControlSection.History],  "a の履歴は開いている");
        Check.True(!loaded.GetSections("b")[VersionControlSection.History], "b の履歴は閉じている");
    }

    /// <summary>
    /// 区切り文字・大文字小文字・末尾の区切りが違っても同じプロジェクトとして扱う
    /// （違うキーになると、開き方を変えていないのに設定が戻らなくなる）。
    /// </summary>
    private static void ProjectKeyIsNormalized()
    {
        var a = VersionControlPanelStateStore.MakeProjectKey(@"C:\Work\Game");
        var b = VersionControlPanelStateStore.MakeProjectKey(@"C:\work\game\");
        var c = VersionControlPanelStateStore.MakeProjectKey("C:/Work/Game");

        Check.Equal(a, b, "大小と末尾の区切り");
        Check.Equal(a, c, "区切り文字");
        Check.True(VersionControlPanelStateStore.MakeProjectKey(null) is null, "未確定なら null");
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>パスを並べてツリーを組む（種類はすべて「変更」）。</summary>
    /// <param name="paths">リポジトリ相対パス。</param>
    private static ChangeTreeNode Build(params string[] paths)
        => ChangeTreeBuilder.Build(
            WORKING_COPY,
            paths.Select(p => NewChange(p, FileChangeKind.Modified)).ToList());

    /// <summary>競合していない変更を 1 件作る。</summary>
    /// <param name="path">リポジトリ相対パス。</param>
    /// <param name="kind">変更の種類。</param>
    private static ChangedFile NewChange(string path, FileChangeKind kind)
        => new(path, kind, FileConflictState.None, isStaged: true, isDirty: true);

    /// <summary>競合していない変更の 1 文字を引く。</summary>
    /// <param name="kind">変更の種類。</param>
    private static string Letter(FileChangeKind kind)
        => VersionControlDisplay.ToChangeLetter(kind, FileConflictState.None);

    /// <summary>リモートとの前後関係だけを指定した状態を作る。</summary>
    /// <param name="remote">前後関係。</param>
    private static WorkingCopyStatus NewStatus(RemoteComparison remote)
        => new("main", 1UL, Array.Empty<ChangedFile>(), remote, StatusRefreshMode.ScanOnline);
}
