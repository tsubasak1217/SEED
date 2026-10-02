using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Reload;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace AutoReloadPolicyTests;

/// <summary>
/// プレハブの外部変更の監視の「開始時の覚え込み」のテスト（2026-10-03 の 2 回目のレビュー #15。docs/editor_auto_reload.md §7.1）。
///
/// <para>
/// 起きていたこと: 追跡器が「知っている内容」（ハッシュ）を埋めるのはイベントの判定と自己書き込みの終了だけで、
/// 起動直後（と設定をオフ→オンにした後）は何も知らなかった。まだ一度もイベントの来ていない .actor へ同じ内容の書き込み・
/// コピー・touch が来ると Changed になり、規則 3（内容が前に知っていたものと同じなら Unchanged）と食い違っていた。
/// </para>
/// <para>
/// 直し: 監視の開始時（と設定をオンにしたとき）に既存の .actor / .actor2d のハッシュを背景スレッドで読み
/// （<see cref="PrefabKnownHashSeeder"/>）、UI スレッドで追跡器へ覚えさせる（<see cref="PrefabExternalChangeTracker.SeedKnownHash"/>）。
/// 覚え終わる前に届いたイベントは従来どおり Changed。覚え込みを読んでいる間に書かれたファイルは覚えない（新しい内容を
/// 「知っている」ことにして外部変更を取りこぼさないため）。
/// </para>
/// </summary>
public static class PrefabHashSeedTests
{
    /// <summary>テストのプレハブ。</summary>
    private const string CardPath = @"C:\proj\assets\ui\Card.actor";

    /// <summary>別のプレハブ。</summary>
    private const string ScreenPath = @"C:\proj\assets\ui\Screen.actor2d";

    /// <summary>3 つ目のプレハブ。</summary>
    private const string ListPath = @"C:\proj\assets\ui\List.actor";

    /// <summary>4 つ目のプレハブ。</summary>
    private const string DialogPath = @"C:\proj\assets\ui\Dialog.actor";

    /// <summary>覚え込みを始めた時刻（基準）。</summary>
    private static readonly DateTime SeedStart = new(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>覚え込みの開始より十分前（起動前に保存されたファイルの更新時刻）。</summary>
    private static readonly DateTime LongBefore = SeedStart - TimeSpan.FromHours(1);

    /// <summary>覚え込みの開始の少し後（覚え込みの最中に書かれたファイルの更新時刻・イベントの時刻）。</summary>
    private static readonly DateTime JustAfter = SeedStart + TimeSpan.FromSeconds(1);

    /// <summary>内容のハッシュを手で決められる偽のファイル（キー＝区切りと大文字小文字を揃えたパス）。</summary>
    private sealed class FakeFiles
    {
        private readonly Dictionary<string, string?> _hashes = new(StringComparer.OrdinalIgnoreCase);
        public void Set(string path, string? hash) => _hashes[Norm(path)] = hash;
        public string? Read(string path) => _hashes.TryGetValue(Norm(path), out var h) ? h : null;
        private static string Norm(string path) => path.Replace('\\', '/');
    }

    /// <summary>判定した件のうち、指定パスの判定を返す（無ければ null）。</summary>
    private static PrefabChangeVerdict? VerdictOf(IReadOnlyList<PrefabChangeOutcome> outcomes, string path)
    {
        var key = path.Replace('\\', '/');
        foreach (var o in outcomes)
            if (string.Equals(o.Path.Replace('\\', '/'), key, StringComparison.OrdinalIgnoreCase)) return o.Verdict;
        return null;
    }

    /// <summary>イベントを 1 回流してデバウンスの満了で判定する。</summary>
    private static PrefabChangeVerdict? EventAndTake(PrefabExternalChangeTracker t, string path, DateTime at)
    {
        t.OnFileEvent(path, at);
        return VerdictOf(t.TakeDue(at + PrefabExternalChangeTracker.Debounce), path);
    }

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        // ── 追跡器への覚え込み ──────────────────────────────────
        h.Add("覚え込み: 監視の開始時に覚えた内容と同じ書き込み・touch は Unchanged（規則 3。レビュー #15）", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            Check.True(t.SeedKnownHash(CardPath, "A", SeedStart), "覚え込みを受け付ける");
            Check.Equal(PrefabChangeVerdict.Unchanged, EventAndTake(t, CardPath, JustAfter),
                "起動後に一度もイベントの来ていない .actor でも、同じ内容なら再展開しない");
        });
        h.Add("覚え込み: 覚えた内容と違う書き込みは Changed", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "B");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.SeedKnownHash(CardPath, "A", SeedStart);
            Check.Equal(PrefabChangeVerdict.Changed, EventAndTake(t, CardPath, JustAfter), "外部の変更");
        });
        h.Add("覚え込み: 覚え終わる前に届いて判定待ちのイベントがあれば覚えず、従来どおり Changed", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.OnFileEvent(CardPath, JustAfter);
            Check.True(!t.SeedKnownHash(CardPath, "A", SeedStart), "判定待ちの間に届いた覚え込みは使わない");
            Check.Equal(PrefabChangeVerdict.Changed, VerdictOf(t.TakeDue(JustAfter + PrefabExternalChangeTracker.Debounce), CardPath),
                "覚え終わる前のイベントは従来どおり Changed（docs §7.1）");
        });
        h.Add("覚え込み: 覚え込みの開始より後に判定・自己書き込みで知った内容は上書きしない", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "NEW"); files.Set(ScreenPath, "MINE");
            var t = new PrefabExternalChangeTracker(files.Read);
            // Card: 覚え込みの最中に外部の書き込みが来て判定済み（NEW を知った）。遅れて古い内容の覚え込みが届く
            Check.Equal(PrefabChangeVerdict.Changed, EventAndTake(t, CardPath, JustAfter), "前提: 知らないので Changed");
            Check.True(!t.SeedKnownHash(CardPath, "OLD", SeedStart), "新しく知った内容を古い覚え込みで上書きしない");
            var later = JustAfter + PrefabExternalChangeTracker.Debounce * 2;
            Check.Equal(PrefabChangeVerdict.Unchanged, EventAndTake(t, CardPath, later), "NEW の touch は Unchanged のまま");
            // Screen: 覚え込みの最中に自分の保存が終わった（MINE を知った）
            t.NotifySelfWriteStarted(ScreenPath, JustAfter);
            t.NotifySelfWriteFinished(ScreenPath, JustAfter);
            Check.True(!t.SeedKnownHash(ScreenPath, "OLD", SeedStart), "自分の書き込みで知った内容も上書きしない");
        });
        h.Add("覚え込み: 設定をオフ→オンで覚え直すと、オフの間に古くなった内容を置き換える", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            EventAndTake(t, CardPath, LongBefore);                 // オンの間に A を知った
            files.Set(CardPath, "B");                              // オフの間に B へ変わった（イベントは捨てた）
            Check.True(t.SeedKnownHash(CardPath, "B", SeedStart), "オンにしたときの覚え直しは古い知識を置き換える");
            Check.Equal(PrefabChangeVerdict.Unchanged, EventAndTake(t, CardPath, JustAfter), "B の touch は Unchanged");
        });
        h.Add("覚え込み: 空のパス・空のハッシュは覚えない", () =>
        {
            var t = new PrefabExternalChangeTracker(new FakeFiles().Read);
            Check.True(!t.SeedKnownHash("  ", "A", SeedStart), "空のパス");
            Check.True(!t.SeedKnownHash(CardPath, "", SeedStart), "空のハッシュ");
        });

        // ── 背景スレッドでの読み取り（覚えてよいファイルの選び方）──────────
        h.Add("覚え込みの読み取り: 開始より後に書かれた・読めない・読む間に変わったファイルは覚えない", () =>
        {
            var lastWrite = new Dictionary<string, Queue<DateTime?>>(StringComparer.OrdinalIgnoreCase)
            {
                [CardPath]   = new(new DateTime?[] { LongBefore, LongBefore }),           // 起動前に保存。覚える
                [ScreenPath] = new(new DateTime?[] { JustAfter, JustAfter }),             // 覚え込みの最中に書かれた。覚えない
                [ListPath]   = new(new DateTime?[] { LongBefore, LongBefore }),           // 中身が読めない。覚えない
                [DialogPath] = new(new DateTime?[] { LongBefore, LongBefore + TimeSpan.FromMilliseconds(1) }), // 読む間に変わった
            };
            var hashes = new FakeFiles();
            hashes.Set(CardPath, "A"); hashes.Set(ScreenPath, "B"); hashes.Set(ListPath, null); hashes.Set(DialogPath, "D");

            var seeds = PrefabKnownHashSeeder.Collect(
                new[] { CardPath, ScreenPath, ListPath, DialogPath }, SeedStart,
                p => lastWrite[p].Count > 0 ? lastWrite[p].Dequeue() : null,
                hashes.Read);

            Check.Equal(1, seeds.Count, "覚えるのは Card だけ: " + string.Join(", ", seeds.Select(s => s.Path)));
            Check.Equal(CardPath, seeds[0].Path, "Card");
            Check.Equal("A", seeds[0].Hash, "Card の内容");
        });
        h.Add("覚え込みの読み取り: 更新時刻が読めないファイルは覚えない・取り消しで止まる", () =>
        {
            var hashes = new FakeFiles(); hashes.Set(CardPath, "A");
            var none = PrefabKnownHashSeeder.Collect(new[] { CardPath }, SeedStart, _ => null, hashes.Read);
            Check.Equal(0, none.Count, "更新時刻が読めない（消えた）");

            using var cancel = new System.Threading.CancellationTokenSource();
            cancel.Cancel();
            var cancelled = PrefabKnownHashSeeder.Collect(new[] { CardPath }, SeedStart, _ => LongBefore, hashes.Read, cancel.Token);
            Check.Equal(0, cancelled.Count, "取り消したら読まない（監視の終了・覚え直し）");
        });
        h.Add("覚え込みの読み取り: 本物のフォルダで .actor / .actor2d だけを拾い（.backup・obj は除く）、内容のハッシュを読む", RealFolderSeed);
    }

    /// <summary>一時フォルダに本物のファイルを置いて、列挙・更新時刻・ハッシュの読み取りを通す。</summary>
    private static void RealFolderSeed()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "seed_prefab_seed_" + Guid.NewGuid().ToString("N"));
        try
        {
            void Write(string rel, string text)
            {
                var abs = System.IO.Path.Combine(root, rel);
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs)!);
                System.IO.File.WriteAllText(abs, text);
            }
            Write(@"ui\Card.actor", "{ \"card\": 1 }");
            Write(@"ui\Screen.ACTOR2D", "{ \"screen\": 1 }");
            Write(@".backup\ui\Card.20261003-000000.actor", "{ }");   // 世代バックアップは監視の対象外
            Write(@"scripts\obj\Gen.actor", "{ }");                   // ビルドの出力も対象外
            Write(@"ui\Card.scene", "{ }");                           // 別の拡張子

            var files = PrefabKnownHashSeeder.EnumeratePrefabFiles(root);
            Check.Equal(2, files.Count, "拾うのは 2 本: " + string.Join(", ", files));

            var seeds = PrefabKnownHashSeeder.Collect(
                files, DateTime.UtcNow + TimeSpan.FromSeconds(1), PrefabKnownHashSeeder.TryGetLastWriteUtc, FileContentHash.TryCompute);
            Check.Equal(2, seeds.Count, "書いた後に始めた覚え込みは 2 本とも覚える");
            var card = seeds.First(s => s.Path.EndsWith("Card.actor", StringComparison.OrdinalIgnoreCase));
            Check.Equal(FileContentHash.TryCompute(card.Path), card.Hash, "内容のハッシュは監視の判定と同じ形");

            var tooEarly = PrefabKnownHashSeeder.Collect(
                files, DateTime.UtcNow - TimeSpan.FromHours(1), PrefabKnownHashSeeder.TryGetLastWriteUtc, FileContentHash.TryCompute);
            Check.Equal(0, tooEarly.Count, "覚え込みの開始より後に書かれたファイルは覚えない");
            Check.True(PrefabKnownHashSeeder.TryGetLastWriteUtc(System.IO.Path.Combine(root, "no_such.actor")) is null, "無いファイルの更新時刻は null");
        }
        finally
        {
            try { System.IO.Directory.Delete(root, recursive: true); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
