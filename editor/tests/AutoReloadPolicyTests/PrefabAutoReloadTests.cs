using System;
using System.Collections.Generic;
using System.Linq;
using SEEDEditor.Reload;
using SpriteRigTests; // テストランナー（TestHarness / Check）を共有する

namespace AutoReloadPolicyTests;

/// <summary>
/// プレハブ（.actor / .actor2d）の外部変更の取り込みのテスト（docs/editor_auto_reload.md §7.1）。
///
/// <para>固定するもの:</para>
/// <list type="number">
///   <item>判定表（<see cref="AutoReloadPolicy.Decide"/> の Prefab と <see cref="AutoReloadPolicy.DecidePrefabExternalChange"/>）の全組み合わせ</item>
///   <item>監視の対象のパス（<see cref="PrefabWatchPaths.IsPrefabFile"/>）</item>
///   <item>デバウンス・自己書き込みの除外・内容の比較・一括の書き換えの抑止（<see cref="PrefabExternalChangeTracker"/>）</item>
/// </list>
/// <para>
/// 直したかったこと: エディタの Ctrl+S は Edit 以外では動かないため、Play 中のプレハブの当て直し
/// （PREFAB_LIVE_PATCH_PATH）が書き戻しの続きと IPC からしか届かなかった。外部の書き換えを拾って配線する。
/// 自分の保存（SAVE_ACTOR）・書き戻し（PREFAB_WRITE_BACK）を拾って二重に当てないことが要。
/// </para>
/// </summary>
public static class PrefabAutoReloadTests
{
    /// <summary>テストのアセットルート。</summary>
    private const string Root = @"C:\proj\assets";

    /// <summary>テストのプレハブ。</summary>
    private const string CardPath = @"C:\proj\assets\ui\Card.actor";

    /// <summary>同じファイルを区切りと大文字小文字だけ変えた書き方。</summary>
    private const string CardPathOtherSpelling = "c:/PROJ/assets/ui/card.actor";

    /// <summary>別のプレハブ。</summary>
    private const string ScreenPath = @"C:\proj\assets\ui\Screen.actor2d";

    /// <summary>基準の時刻。</summary>
    private static readonly DateTime T0 = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>デバウンスの直前（1 ミリ秒手前）。</summary>
    private static readonly TimeSpan JustBeforeDebounce = PrefabExternalChangeTracker.Debounce - TimeSpan.FromMilliseconds(1);

    /// <summary>
    /// 内容のハッシュを手で決められる偽のファイル（キー＝区切りと大文字小文字を揃えたパス）。
    /// null を入れると「読めない」。
    /// </summary>
    private sealed class FakeFiles
    {
        private readonly Dictionary<string, string?> _hashes = new(StringComparer.OrdinalIgnoreCase);
        public void Set(string path, string? hash) => _hashes[Norm(path)] = hash;
        public string? Read(string path) => _hashes.TryGetValue(Norm(path), out var h) ? h : null;
        private static string Norm(string path) => path.Replace('\\', '/');
    }

    /// <summary>判定した件のうち、指定パス（綴りの違いを無視）の判定を返す。無ければ null。</summary>
    private static PrefabChangeVerdict? VerdictOf(IReadOnlyList<PrefabChangeOutcome> outcomes, string path)
    {
        var key = path.Replace('\\', '/');
        foreach (var o in outcomes)
            if (string.Equals(o.Path.Replace('\\', '/'), key, StringComparison.OrdinalIgnoreCase)) return o.Verdict;
        return null;
    }

    public static void Register(TestHarness h)
    {
        RegisterPolicy(h);
        RegisterPaths(h);
        RegisterTracker(h);
    }

    // ── 1. 判定表 ───────────────────────────────────────────────

    private static void RegisterPolicy(TestHarness h)
    {
        var states = new[] { PlaybackState.Edit, PlaybackState.Play, PlaybackState.Pause };

        // Decide（種別ごとの大枠）: オフは Drop、オンは状態によらず ApplyNow（Play 中も保留しない）
        foreach (var state in states)
        {
            foreach (var applyScripts in new[] { false, true })
            {
                var s = state; var a = applyScripts;
                h.Add($"Decide Prefab: 設定オフ/{s}/即時反映={a} は Drop", () =>
                    Check.Equal(AutoReloadDecision.Drop,
                        AutoReloadPolicy.Decide(AutoReloadKind.Prefab, s, autoReloadEnabled: false, applyScriptsDuringPlay: a),
                        "オフでは保留もしない"));
                h.Add($"Decide Prefab: 設定オン/{s}/即時反映={a} は ApplyNow", () =>
                    Check.Equal(AutoReloadDecision.ApplyNow,
                        AutoReloadPolicy.Decide(AutoReloadKind.Prefab, s, autoReloadEnabled: true, applyScriptsDuringPlay: a),
                        "状態を保つ当て直しなので Play 中も保留しない（スクリプトの設定は波及しない）"));
            }
        }

        // DecidePrefabExternalChange: 状態 × 自動再読込 × 保存時の自動反映 の 12 通り
        foreach (var state in states)
        {
            foreach (var autoPropagate in new[] { false, true })
            {
                var s = state; var p = autoPropagate;
                h.Add($"外部変更: 自動再読込オフ/{s}/自動反映={p} は None", () =>
                    Check.Equal(PrefabExternalChangeAction.None,
                        AutoReloadPolicy.DecidePrefabExternalChange(s, autoReloadEnabled: false, autoPropagate: p),
                        "オフでは何も送らない"));
            }
        }
        h.Add("外部変更: Edit・自動反映オンでも版ずれのお知らせだけ（2026-10-03 レビュー #5: 同時に変わった .scene を古い内容で上書きしないため）", () =>
            Check.Equal(PrefabExternalChangeAction.StatusOnly,
                AutoReloadPolicy.DecidePrefabExternalChange(PlaybackState.Edit, autoReloadEnabled: true, autoPropagate: true),
                "PREFAB_STATUS（シーンに触れない。更新はバナーの［更新する］から）"));
        h.Add("外部変更: Edit・自動反映オフは版ずれのお知らせだけ", () =>
            Check.Equal(PrefabExternalChangeAction.StatusOnly,
                AutoReloadPolicy.DecidePrefabExternalChange(PlaybackState.Edit, autoReloadEnabled: true, autoPropagate: false),
                "PREFAB_STATUS（シーンに触れない）"));
        foreach (var state in new[] { PlaybackState.Play, PlaybackState.Pause })
        {
            foreach (var autoPropagate in new[] { false, true })
            {
                var s = state; var p = autoPropagate;
                h.Add($"外部変更: {s}・自動反映={p} は当て直して覚える", () =>
                    Check.Equal(PrefabExternalChangeAction.LivePatchAndRemember,
                        AutoReloadPolicy.DecidePrefabExternalChange(s, autoReloadEnabled: true, autoPropagate: p),
                        "Play の表示だけを変える当て直しは設定に関わらず行う（Edit への反映は停止時に設定を見る）"));
            }
        }
    }

    // ── 2. 監視の対象のパス ─────────────────────────────────────

    private static void RegisterPaths(TestHarness h)
    {
        h.Add("パス: .actor を拾う", () =>
            Check.True(PrefabWatchPaths.IsPrefabFile(CardPath, Root), ".actor"));
        h.Add("パス: .actor2d を拾う", () =>
            Check.True(PrefabWatchPaths.IsPrefabFile(ScreenPath, Root), ".actor2d"));
        h.Add("パス: 拡張子の大文字小文字を問わない", () =>
            Check.True(PrefabWatchPaths.IsPrefabFile(@"C:\proj\assets\ui\CARD.ACTOR", Root), ".ACTOR"));
        h.Add("パス: 原子的な保存の一時ファイル（.actor.tmp）は拾わない", () =>
            Check.True(!PrefabWatchPaths.IsPrefabFile(@"C:\proj\assets\ui\Card.actor.tmp", Root), ".tmp は名前の変更で本名になってから拾う"));
        h.Add("パス: 世代バックアップ（.backup/）の中は拾わない", () =>
            Check.True(!PrefabWatchPaths.IsPrefabFile(@"C:\proj\assets\.backup\ui\Card.20261003-000000.actor", Root),
                "保存のたびに旧版が外部変更として届かないように"));
        h.Add("パス: obj / .git の中は拾わない", () =>
        {
            Check.True(!PrefabWatchPaths.IsPrefabFile(@"C:\proj\assets\scripts\obj\Card.actor", Root), "obj");
            Check.True(!PrefabWatchPaths.IsPrefabFile(@"C:\proj\assets\.git\Card.actor", Root), ".git");
        });
        h.Add("パス: アセットルート自体の名前に bin が入っていても落とさない", () =>
            Check.True(PrefabWatchPaths.IsPrefabFile(@"D:\bin\assets\ui\Card.actor", @"D:\bin\assets"), "ルートより下の段だけを見る"));
        h.Add("パス: 別の拡張子・空は拾わない", () =>
        {
            Check.True(!PrefabWatchPaths.IsPrefabFile(@"C:\proj\assets\ui\Card.scene", Root), ".scene");
            Check.True(!PrefabWatchPaths.IsPrefabFile(null, Root), "null");
            Check.True(!PrefabWatchPaths.IsPrefabFile("", Root), "空");
        });
    }

    // ── 3. デバウンス・自己書き込みの除外 ───────────────────────

    private static void RegisterTracker(TestHarness h)
    {
        h.Add("追跡: デバウンスが満了するまで判定しない", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.OnFileEvent(CardPath, T0);
            Check.Equal(0, t.TakeDue(T0 + JustBeforeDebounce).Count, "書き込みの途中で読まない");
            Check.Equal(T0 + PrefabExternalChangeTracker.Debounce, t.NextDueUtc, "次の満了の時刻");
            var due = t.TakeDue(T0 + PrefabExternalChangeTracker.Debounce);
            // まだ内容を知らない（監視の開始時の覚え込みが済む前・覚え込みに入らなかった）ファイルの最初の書き込みは、
            // 内容に関わらず外部変更（docs §7.1。覚え込みの後の規則は PrefabHashSeedTests.cs。2 回目のレビュー #15）
            Check.Equal(PrefabChangeVerdict.Changed, VerdictOf(due, CardPath), "静まったら外部変更（まだ内容を知らないファイル）");
            Check.Equal(0, t.PendingCount, "取り出したら待ちは空");
        });

        h.Add("追跡: 続けて届いたイベントはデバウンスを延ばし 1 件にまとめる", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.OnFileEvent(CardPath, T0);
            t.OnFileEvent(CardPathOtherSpelling, T0 + JustBeforeDebounce);   // 同じファイル（綴り違い）
            Check.Equal(1, t.PendingCount, "綴りの違いは同じファイル");
            Check.Equal(0, t.TakeDue(T0 + PrefabExternalChangeTracker.Debounce).Count, "最後のイベントから数え直す");
            var due = t.TakeDue(T0 + JustBeforeDebounce + PrefabExternalChangeTracker.Debounce);
            Check.Equal(1, due.Count, "1 件だけ");
        });

        h.Add("追跡: 内容が同じなら Unchanged（touch・重複イベント）", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.OnFileEvent(CardPath, T0);
            t.TakeDue(T0 + PrefabExternalChangeTracker.Debounce);              // A を覚える
            var later = T0 + PrefabExternalChangeTracker.Debounce * 2;
            t.OnFileEvent(CardPath, later);
            var due = t.TakeDue(later + PrefabExternalChangeTracker.Debounce);
            Check.Equal(PrefabChangeVerdict.Unchanged, VerdictOf(due, CardPath), "内容が変わっていない");
        });

        h.Add("追跡: 自分の保存（開始〜終了）の書き込みは SelfWrite", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.NotifySelfWriteStarted(CardPath, T0);                            // SAVE_ACTOR を送った
            files.Set(CardPath, "B");
            t.OnFileEvent(CardPath, T0);                                       // ランタイムが書いた
            var due = t.TakeDue(T0 + PrefabExternalChangeTracker.Debounce);    // SAVE_OK はまだ（遅い応答）
            Check.Equal(PrefabChangeVerdict.SelfWrite, VerdictOf(due, CardPath), "応答待ちの間は自分の書き込み");
        });

        h.Add("追跡: 自分の書き込みの終了の後も余韻の間は SelfWrite、その後の同じ内容は Unchanged", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            files.Set(CardPath, "B");
            t.OnFileEvent(CardPath, T0);                                       // 書き込みのイベントが先に届く
            t.NotifySelfWriteFinished(CardPath, T0);                           // EXPORT_ACTOR_OK（開始の知らせ無し）
            var due = t.TakeDue(T0 + PrefabExternalChangeTracker.Debounce);
            Check.True(VerdictOf(due, CardPath) is PrefabChangeVerdict.SelfWrite or PrefabChangeVerdict.Unchanged,
                "終了時に覚えた内容と同じなので当て直さない");
            // 余韻が切れた後に遅れて届いた同じ書き込みのイベント
            var late = T0 + PrefabExternalChangeTracker.SelfWriteTail + PrefabExternalChangeTracker.Debounce;
            t.OnFileEvent(CardPath, late);
            Check.Equal(PrefabChangeVerdict.Unchanged,
                VerdictOf(t.TakeDue(late + PrefabExternalChangeTracker.Debounce), CardPath), "覚えた内容と同じ");
        });

        h.Add("追跡: 余韻の後の別の内容は外部変更", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.NotifySelfWriteStarted(CardPath, T0);
            t.NotifySelfWriteFinished(CardPath, T0);                           // A を覚える
            var after = T0 + PrefabExternalChangeTracker.SelfWriteTail;
            files.Set(CardPath, "C");                                          // AI が書き換えた
            t.OnFileEvent(CardPath, after);
            Check.Equal(PrefabChangeVerdict.Changed,
                VerdictOf(t.TakeDue(after + PrefabExternalChangeTracker.Debounce), CardPath), "余韻が切れたら外部変更");
        });

        h.Add("追跡: 終了の知らせが来ないまま上限を過ぎたら外部変更として扱う", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.NotifySelfWriteStarted(CardPath, T0);                            // 応答が失われた
            var after = T0 + PrefabExternalChangeTracker.SelfWriteMaxWait;
            files.Set(CardPath, "B");
            t.OnFileEvent(CardPath, after);
            Check.Equal(PrefabChangeVerdict.Changed,
                VerdictOf(t.TakeDue(after + PrefabExternalChangeTracker.Debounce), CardPath), "監視が永久に死なない");
        });

        h.Add("追跡: 自己書き込みの窓はそのパスだけ（別のプレハブは外部変更）", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A"); files.Set(ScreenPath, "S1");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.NotifySelfWriteStarted(CardPath, T0);
            t.OnFileEvent(CardPath, T0);
            t.OnFileEvent(ScreenPath, T0);
            var due = t.TakeDue(T0 + PrefabExternalChangeTracker.Debounce);
            Check.Equal(PrefabChangeVerdict.SelfWrite, VerdictOf(due, CardPathOtherSpelling), "綴りの違いも同じ窓");
            Check.Equal(PrefabChangeVerdict.Changed, VerdictOf(due, ScreenPath), "別のファイルは外部変更");
        });

        h.Add("追跡: 読めない間は再試行し、上限を超えたら Unreadable", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, null);           // ロック中
            var t = new PrefabExternalChangeTracker(files.Read);
            t.OnFileEvent(CardPath, T0);
            var now = T0;
            for (int i = 0; i < PrefabExternalChangeTracker.HashRetryLimit; i++)
            {
                now += PrefabExternalChangeTracker.Debounce;
                Check.Equal(0, t.TakeDue(now).Count, $"{i + 1} 回目は再試行（待ちに残る）");
                Check.Equal(1, t.PendingCount, "待ちに残っている");
            }
            now += PrefabExternalChangeTracker.Debounce;
            Check.Equal(PrefabChangeVerdict.Unreadable, VerdictOf(t.TakeDue(now), CardPath), "上限を超えたら捨てる");
            Check.Equal(0, t.PendingCount, "捨てたら待ちは空");
        });

        h.Add("追跡: 読めるようになったら判定する（書き込みの途中のロックが解けた）", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, null);
            var t = new PrefabExternalChangeTracker(files.Read);
            t.OnFileEvent(CardPath, T0);
            var first = T0 + PrefabExternalChangeTracker.Debounce;
            Check.Equal(0, t.TakeDue(first).Count, "ロック中は待つ");
            files.Set(CardPath, "A");
            Check.Equal(PrefabChangeVerdict.Changed,
                VerdictOf(t.TakeDue(first + PrefabExternalChangeTracker.Debounce), CardPath), "読めたら外部変更");
        });

        h.Add("追跡: 一括の書き換えの最中と余韻の間は Suppressed、その後は外部変更", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.BeginSuppression();
            t.OnFileEvent(CardPath, T0);
            Check.Equal(PrefabChangeVerdict.Suppressed,
                VerdictOf(t.TakeDue(T0 + PrefabExternalChangeTracker.Debounce), CardPath), "最中");
            var end = T0 + PrefabExternalChangeTracker.Debounce;
            t.EndSuppression(end);
            files.Set(CardPath, "B");
            t.OnFileEvent(CardPath, end);
            Check.Equal(PrefabChangeVerdict.Suppressed,
                VerdictOf(t.TakeDue(end + PrefabExternalChangeTracker.Debounce), CardPath), "余韻の間（遅れて届いたイベント）");
            var after = end + PrefabExternalChangeTracker.SelfWriteTail;
            files.Set(CardPath, "C");
            t.OnFileEvent(CardPath, after);
            var due = t.TakeDue(after + PrefabExternalChangeTracker.Debounce);
            Check.Equal(PrefabChangeVerdict.Changed, VerdictOf(due, CardPath), "余韻の後は外部変更");
        });

        h.Add("追跡: 抑止は入れ子にでき、いちばん外側を閉じるまで続く", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.BeginSuppression();
            t.BeginSuppression();
            t.EndSuppression(T0);
            t.OnFileEvent(CardPath, T0 + PrefabExternalChangeTracker.SelfWriteTail);
            var due = t.TakeDue(T0 + PrefabExternalChangeTracker.SelfWriteTail + PrefabExternalChangeTracker.Debounce);
            Check.Equal(PrefabChangeVerdict.Suppressed, VerdictOf(due, CardPath), "内側を閉じただけでは続く");
        });

        h.Add("追跡: 判定した順は最初にイベントが来た順", () =>
        {
            var files = new FakeFiles(); files.Set(CardPath, "A"); files.Set(ScreenPath, "S");
            var t = new PrefabExternalChangeTracker(files.Read);
            t.OnFileEvent(ScreenPath, T0);
            t.OnFileEvent(CardPath, T0);
            var due = t.TakeDue(T0 + PrefabExternalChangeTracker.Debounce);
            Check.Equal(2, due.Count, "2 件");
            Check.Equal(ScreenPath, due.First().Path, "先に来た方が先");
        });
    }
}
