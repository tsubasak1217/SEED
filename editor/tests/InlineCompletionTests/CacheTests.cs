using System;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;
using SpriteRigTests;

namespace InlineCompletionTests;

/// <summary>
/// 選択結果の使い回し（<see cref="ApiReferenceSelectionCache"/>・<see cref="ContextualApiReference.Select"/>）のテスト。
/// </summary>
public static class CacheTests
{
    /// <summary>あふれを確かめるときの覚える数。</summary>
    private const int SmallCapacity = 2;

    /// <summary>選ぶときの予算（見本の全切れ端より大きい）。</summary>
    private const int Budget = 100_000;

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("キャッシュ: 同じ内容・位置・予算は同じ結果を使い回す", ReusesSameKey);
        harness.Add("キャッシュ: 内容・予算・位置のどれかが違えば選び直す", MissesOnDifferentKey);
        harness.Add("キャッシュ: あふれたら最も古いものから捨てる", EvictsLeastRecentlyUsed);
        harness.Add("キャッシュ: 鍵の内容のハッシュは 1 文字の違いで変わる", KeyHashIsContentSensitive);
    }

    /// <summary>同じ鍵。</summary>
    private static void ReusesSameKey()
    {
        var reference = ContextualApiReference.FromMarkdown(Fixture.Markdown, ApiReferenceSettings.BuiltIn());
        var (text, caret) = Fixture.Split(Fixture.ScreenStackFile);
        var first = reference.Select(text, caret, Budget);
        var second = reference.Select(new string(text.ToCharArray()), caret, Budget);   // 別の文字列の実体でも内容が同じなら当たる
        Check.True(ReferenceEquals(first, second), "同じ結果の実体を返す");
        Check.Equal(1, reference.Cache.Hits, "使い回し 1 回");
        Check.Equal(1, reference.Cache.Misses, "選び直し 1 回");
    }

    /// <summary>違う鍵。</summary>
    private static void MissesOnDifferentKey()
    {
        var reference = ContextualApiReference.FromMarkdown(Fixture.Markdown, ApiReferenceSettings.BuiltIn());
        var (text, caret) = Fixture.Split(Fixture.ScreenStackFile);
        var baseline = reference.Select(text, caret, Budget);
        var otherBudget = reference.Select(text, caret, Budget - 1);
        var otherText = reference.Select(text + "\n", caret, Budget);
        var otherCaret = reference.Select(text, caret - 1, Budget);
        Check.True(!ReferenceEquals(baseline, otherBudget), "予算が違えば選び直す");
        Check.True(!ReferenceEquals(baseline, otherText), "内容が違えば選び直す");
        Check.True(!ReferenceEquals(baseline, otherCaret), "カーソルの位置が違えば選び直す（近くの語の重みが変わるため）");
        Check.Equal(0, reference.Cache.Hits, "使い回しなし");
        Check.Equal(4, reference.Cache.Misses, "選び直し 4 回");
    }

    /// <summary>LRU のあふれ。</summary>
    private static void EvictsLeastRecentlyUsed()
    {
        var cache = new ApiReferenceSelectionCache(SmallCapacity);
        var a = ApiReferenceSelectionCache.MakeKey("a", 0, Budget);
        var b = ApiReferenceSelectionCache.MakeKey("b", 0, Budget);
        var c = ApiReferenceSelectionCache.MakeKey("c", 0, Budget);
        cache.Add(a, ApiReferenceSelection.Empty);
        cache.Add(b, ApiReferenceSelection.Empty);
        Check.True(cache.TryGet(a, out _), "a を使う（a が最新になる）");
        cache.Add(c, ApiReferenceSelection.Empty);          // 最も古い b があふれる
        Check.Equal(SmallCapacity, cache.Count, "覚える数は上限まで");
        Check.True(cache.TryGet(a, out _), "最近使った a は残る");
        Check.True(!cache.TryGet(b, out _), "最も古い b は捨てられる");
        Check.True(cache.TryGet(c, out _), "新しい c は残る");
        cache.Clear();
        Check.Equal(0, cache.Count, "Clear で空");
        Check.Equal(1, new ApiReferenceSelectionCache(0).Capacity, "覚える数は最低 1");
    }

    /// <summary>鍵のハッシュ。</summary>
    private static void KeyHashIsContentSensitive()
    {
        var one = ApiReferenceSelectionCache.MakeKey("var screen = 1;", 3, Budget);
        var same = ApiReferenceSelectionCache.MakeKey("var screen = 1;", 3, Budget);
        var other = ApiReferenceSelectionCache.MakeKey("var screen = 2;", 3, Budget);
        Check.Equal(one, same, "同じ内容は同じ鍵");
        Check.True(one.ContentHash != other.ContentHash, "1 文字違えばハッシュが変わる");
        Check.True(ApiReferenceSelectionCache.MakeKey(string.Empty, 0, 0).ContentHash.Length > 0, "空の内容でも鍵を作れる");
    }
}
