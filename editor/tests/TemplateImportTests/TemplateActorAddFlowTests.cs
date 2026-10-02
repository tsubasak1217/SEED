// ============================================================
//  TemplateActorAddFlowTests.cs — テンプレートアクタを 1 件追加する一連の手順（TemplateActorAddFlow）の単体テスト
//
//  窓（TemplateActorPickerWindow）と MCP の seed_template_actor が共通に通る手順の分岐を、
//  外部の機能一式（TemplateActorPickerContext）を偽物に差し替えて確かめる:
//    編集できない → 何もしない / 追加先を見失う → 送らない / 2D・3D の規則 → 送らない /
//    準備の失敗 → 送らない / 送る直前に見失う → 送らない（コピーは知らせる）/ 成功 → ADD_TEMPLATE_ACTOR を 1 回だけ送る
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SEEDEditor.Templates.Actors;
using SpriteRigTests;

namespace SEEDEditor.Tests.TemplateImport;

/// <summary>TemplateActorAddFlow のテスト。</summary>
public static class TemplateActorAddFlowTests
{
    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("追加の手順: 成功なら ADD_TEMPLATE_ACTOR を 1 回だけ送り、引き直した追加先を返す", SendsOnceOnSuccess);
        h.Add("追加の手順: 編集できない・見失った・2D/3D の規則では送らない（準備もしない）", RefusesBeforePreparing);
        h.Add("追加の手順: 準備の失敗・送る直前の見失いでは送らない", RefusesAfterPreparing);
    }

    /// <summary>偽物の外部機能（呼ばれ方を記録する）。</summary>
    private sealed class FakeHost
    {
        /// <summary>送った命令。</summary>
        public List<string> Sent { get; } = [];

        /// <summary>依存ファイルのコピーを知らされた回数。</summary>
        public int FilesCopiedCount { get; private set; }

        /// <summary>引き直しが呼ばれた回数。</summary>
        public int RefreshCount { get; private set; }

        /// <summary>編集できない理由（null なら編集できる）。</summary>
        public string? ReadOnly { get; init; }

        /// <summary>何回目の引き直しで見失うか（0 なら見失わない）。</summary>
        public int LoseOnRefresh { get; init; }

        /// <summary>引き直しで返す追加先（null なら渡されたもの）。</summary>
        public TemplateActorTarget? RefreshedTarget { get; init; }

        /// <summary>手順へ渡す外部の機能一式を作る。</summary>
        /// <param name="libraryRoot">ライブラリの場所。</param>
        /// <param name="assetsRoot">アセットの場所。</param>
        public TemplateActorPickerContext Context(string libraryRoot, string assetsRoot) => new()
        {
            LibraryRoot    = libraryRoot,
            AssetsRoot     = () => assetsRoot,
            ReadOnlyReason = () => ReadOnly,
            RefreshTarget  = t =>
            {
                RefreshCount++;
                return RefreshCount == LoseOnRefresh
                    ? new TemplateActorTargetRefresh(null, "見失った")
                    : new TemplateActorTargetRefresh(RefreshedTarget ?? t, null);
            },
            SendToRuntime  = Sent.Add,
            FilesCopied    = _ => FilesCopiedCount++,
        };
    }

    private static void SendsOnceOnSuccess()
    {
        using var fx = new TemplateActorFixture();
        var entry = TemplateActorCatalog.Load(fx.LibraryRoot).Entries.First(e => e.TemplateRelPath == TemplateActorFixture.ThingPath);
        var fake  = new FakeHost { RefreshedTarget = new TemplateActorTarget { WorldLine = 0, ParentDfs = 9, ParentName = "World" } };
        var refreshed = new List<TemplateActorTarget>();
        bool preparing = false;

        var outcome = TemplateActorAddFlow.RunAsync(
            fake.Context(fx.LibraryRoot, fx.AssetsRoot), new TemplateActorTarget { ParentDfs = 4, ParentName = "World" }, entry,
            targetRefreshed: refreshed.Add, preparing: () => preparing = true, stagingDirectory: fx.StagingRoot)
            .GetAwaiter().GetResult();

        Check.True(outcome.IsSent, $"送れなかった: {outcome.Status} {outcome.Message}");
        Check.Equal(1, fake.Sent.Count, "送った回数");
        Check.True(fake.Sent[0].StartsWith(TemplateActorIpc.AddCommandPrefix + "0,9,", StringComparison.Ordinal),
                   $"引き直した親（DFS 9）へ送る: {fake.Sent[0]}");
        Check.Equal(outcome.SentCommand, fake.Sent[0], "結果に送った行が入る");
        Check.Equal(2, fake.RefreshCount, "引き直しは準備の前と送る直前の 2 回");
        Check.Equal(2, refreshed.Count, "引き直しのたびに知らせる");
        Check.True(preparing, "準備に入ることを知らせる");
        Check.Equal(1, fake.FilesCopiedCount, "モデルのコピーを知らせる");
        Check.True(outcome.Result is { Success: true } && File.Exists(outcome.Result.StagedPath), "一時ファイルがある");
    }

    private static void RefusesBeforePreparing()
    {
        using var fx = new TemplateActorFixture();
        var catalog = TemplateActorCatalog.Load(fx.LibraryRoot);
        var thing   = catalog.Entries.First(e => e.TemplateRelPath == TemplateActorFixture.ThingPath);   // 3D

        // 編集できない
        var readOnly = new FakeHost { ReadOnly = "閲覧専用" };
        var r1 = Run(readOnly, fx, thing, new TemplateActorTarget());
        Check.Equal(TemplateActorAddStatus.ReadOnly, r1.Status, "閲覧専用");
        Check.Equal("閲覧専用", r1.Message, "理由はそのまま");
        Check.True(readOnly.Sent.Count == 0 && readOnly.RefreshCount == 0, "引き直しも送信もしない");

        // 最初の引き直しで見失う
        var lost = new FakeHost { LoseOnRefresh = 1 };
        var r2 = Run(lost, fx, thing, new TemplateActorTarget { ParentDfs = 3 });
        Check.Equal(TemplateActorAddStatus.TargetLost, r2.Status, "見失った");
        Check.True(lost.Sent.Count == 0 && r2.Result is null, "準備も送信もしない");

        // 3D を 2D の子へ入れようとした
        var rule = new FakeHost();
        var r3 = Run(rule, fx, thing, new TemplateActorTarget { ParentDfs = 5, ParentIs2D = true });
        Check.Equal(TemplateActorAddStatus.Rejected, r3.Status, "2D の子に 3D");
        Check.True(r3.Message!.Contains(TemplateActorTarget.Reject3DUnder2D), $"既存の追加と同じ文言: {r3.Message}");
        Check.True(rule.Sent.Count == 0 && r3.Result is null && rule.FilesCopiedCount == 0, "準備も送信もしない");
    }

    private static void RefusesAfterPreparing()
    {
        using var fx = new TemplateActorFixture();
        var thing = TemplateActorCatalog.Load(fx.LibraryRoot).Entries.First(e => e.TemplateRelPath == TemplateActorFixture.ThingPath);

        // 準備の失敗（アセットの場所が無い）
        var noAssets = new FakeHost();
        var r1 = TemplateActorAddFlow.RunAsync(
            noAssets.Context(fx.LibraryRoot, Path.Combine(fx.AssetsRoot, "nope")), new TemplateActorTarget(), thing,
            stagingDirectory: fx.StagingRoot).GetAwaiter().GetResult();
        Check.Equal(TemplateActorAddStatus.PrepareFailed, r1.Status, "準備の失敗");
        Check.True(noAssets.Sent.Count == 0 && r1.Result is { Success: false }, "送らず、準備の結果は返す");

        // 送る直前（2 回目の引き直し）に見失う: コピーは済んでいるので知らせるが、送らない
        var lateLost = new FakeHost { LoseOnRefresh = 2 };
        var r2 = Run(lateLost, fx, thing, new TemplateActorTarget());
        Check.Equal(TemplateActorAddStatus.TargetLost, r2.Status, "送る直前に見失った");
        Check.True(lateLost.Sent.Count == 0, "送らない");
        Check.Equal(1, lateLost.FilesCopiedCount, "コピーしたことは知らせる（プロジェクトパネルの読み直し）");
        Check.True(r2.Result is { Success: true }, "準備の結果は返す");
    }

    /// <summary>既定の偽物の外部機能で手順を走らせる。</summary>
    private static TemplateActorAddOutcome Run(FakeHost fake, TemplateActorFixture fx, TemplateActorEntry entry, TemplateActorTarget target) =>
        TemplateActorAddFlow.RunAsync(fake.Context(fx.LibraryRoot, fx.AssetsRoot), target, entry, stagingDirectory: fx.StagingRoot)
            .GetAwaiter().GetResult();
}
