using System;
using System.Collections.Generic;

namespace SpriteRigTests;

/// <summary>
/// 外部テストフレームワークを使わない最小のテストランナー。
///
/// xunit などを足すと NuGet の取得が要るため、
/// オフラインでも `dotnet run --project editor/tests/SpriteRigTests` だけで
/// 回せるように、登録と実行だけの薄い仕組みを自前で持つ。
/// </summary>
public sealed class TestHarness
{
    /// <summary>登録されたテスト（名前と本体）。</summary>
    private readonly List<(string Name, Action Body)> _tests = new();

    /// <summary>失敗したテストの件数。</summary>
    public int FailureCount { get; private set; }

    /// <summary>テストを登録する。</summary>
    /// <param name="name">テスト名（失敗時に表示される）。</param>
    /// <param name="body">テスト本体。</param>
    public void Add(string name, Action body) => _tests.Add((name, body));

    /// <summary>
    /// 登録された全テストを実行し、結果を標準出力へ書く。
    /// </summary>
    /// <returns>プロセスの終了コード（全成功なら 0）。</returns>
    public int Run()
    {
        foreach (var (name, body) in _tests)
        {
            try
            {
                body();
                Console.WriteLine($"  [ OK ] {name}");
            }
            catch (Exception ex)
            {
                FailureCount++;
                Console.WriteLine($"  [FAIL] {name}");
                Console.WriteLine($"         {ex.Message}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{_tests.Count - FailureCount} / {_tests.Count} 件成功");
        return FailureCount == 0 ? 0 : 1;
    }
}

/// <summary>
/// テスト用の表明ヘルパー。失敗時は <see cref="AssertionException"/> を投げる。
/// </summary>
public static class Check
{
    /// <summary>条件が真であることを表明する。</summary>
    /// <param name="condition">検査する条件。</param>
    /// <param name="message">失敗時のメッセージ。</param>
    public static void True(bool condition, string message)
    {
        if (!condition) throw new AssertionException(message);
    }

    /// <summary>2 つの値が等しいことを表明する。</summary>
    /// <param name="expected">期待値。</param>
    /// <param name="actual">実際の値。</param>
    /// <param name="what">対象の説明。</param>
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertionException($"{what}: 期待 {expected} / 実際 {actual}");
    }

    /// <summary>浮動小数が許容誤差内で等しいことを表明する。</summary>
    /// <param name="expected">期待値。</param>
    /// <param name="actual">実際の値。</param>
    /// <param name="tolerance">許容誤差。</param>
    /// <param name="what">対象の説明。</param>
    public static void Close(double expected, double actual, double tolerance, string what)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new AssertionException($"{what}: 期待 {expected} ± {tolerance} / 実際 {actual}");
    }
}

/// <summary>表明が失敗したことを表す例外。</summary>
public sealed class AssertionException : Exception
{
    /// <summary>メッセージを指定して生成する。</summary>
    /// <param name="message">失敗内容。</param>
    public AssertionException(string message) : base(message) { }
}
