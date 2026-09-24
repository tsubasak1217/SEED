// ============================================================
//  AndroidAbi.cs — 対応する Android の ABI の表と、端末に合う ABI の選び方
//
//  【役割】
//  ビルドできる ABI（arm64-v8a = 実機 / x86_64 = PC のエミュレータ）と、それぞれの Rust のターゲットを
//  1 か所に持つ（runtime/android/app/build.gradle.kts の defaultSeedAbis と docs/android.md §2 と同じ）。
//  ABI を指定されなかったときは、端末の ro.product.cpu.abilist（端末が実行できる ABI を優先順に並べた値）の
//  先頭から、この表にあるものを選ぶ。
//
//  WPF に依存しない（純粋な処理。単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Android;

/// <summary>Android の ABI（Android の名前と Rust のターゲット）。</summary>
/// <param name="Name">Android の ABI 名（jniLibs のフォルダ名・Gradle の abiFilters）。</param>
/// <param name="RustTarget">cargo のターゲット（cargo ndk の -t には Android の名前を渡すので記録用）。</param>
public sealed record AndroidAbi(string Name, string RustTarget)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>対応する ABI の表と選び方。</summary>
public static class AndroidAbis
{
    /// <summary>ABI の並びを文字列にするときの区切り（-Pseed.abis=a,b・引数 --abi a,b）。</summary>
    public const char ListSeparator = ',';

    /// <summary>64 bit ARM（現行の Android 実機のほぼすべて。配布はこれだけの想定）。</summary>
    public static readonly AndroidAbi Arm64 = new("arm64-v8a", "aarch64-linux-android");

    /// <summary>64 bit x86（PC のエミュレータで開発するためだけに作る）。</summary>
    public static readonly AndroidAbi X86_64 = new("x86_64", "x86_64-linux-android");

    /// <summary>ビルドできる ABI（並びは「両方」を指定したときのビルド順）。</summary>
    public static readonly IReadOnlyList<AndroidAbi> Supported = new[] { Arm64, X86_64 };

    /// <summary>ABI を指定されず、端末からも決められないときに作る ABI（両方。従来の build_and_run.ps1 の既定と同じ）。</summary>
    public static IReadOnlyList<AndroidAbi> Default => Supported;

    /// <summary>名前から ABI を引く（大文字小文字・前後の空白は無視）。無ければ null。</summary>
    /// <param name="name">ABI 名。</param>
    /// <returns>ABI。</returns>
    public static AndroidAbi? Find(string? name)
    {
        var trimmed = name?.Trim();
        return Supported.FirstOrDefault(abi => string.Equals(abi.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 「a,b」形式の並びを解釈する（重複は 1 つにまとめ、表の順に並べ直す）。
    /// </summary>
    /// <param name="text">カンマ区切りの ABI 名。</param>
    /// <param name="error">解釈できなかった理由（成功時は null）。</param>
    /// <returns>ABI の並び（失敗時は空）。</returns>
    public static IReadOnlyList<AndroidAbi> ParseList(string? text, out string? error)
    {
        error = null;
        var names = (text ?? string.Empty)
            .Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0)
        {
            error = $"ABI が空です（使える値: {Describe(Supported)}）";
            return Array.Empty<AndroidAbi>();
        }
        var chosen = new HashSet<AndroidAbi>();
        foreach (var name in names)
        {
            var abi = Find(name);
            if (abi is null)
            {
                error = $"知らない ABI です: {name}（使える値: {Describe(Supported)}）";
                return Array.Empty<AndroidAbi>();
            }
            chosen.Add(abi);
        }
        return Supported.Where(chosen.Contains).ToArray();
    }

    /// <summary>
    /// 端末の ro.product.cpu.abilist（例 "x86_64,arm64-v8a"）から、ビルドできる ABI のうち端末が最も優先するものを選ぶ。
    /// エミュレータ（x86_64）は ARM の変換も持つので arm64-v8a も並ぶが、先頭の x86_64 を選ぶ（変換無しで速い）。
    /// </summary>
    /// <param name="abiList">端末の ABI の並び（カンマ区切り。優先順）。</param>
    /// <returns>選んだ ABI。どれも合わなければ null。</returns>
    public static AndroidAbi? ChooseForDevice(string? abiList)
    {
        foreach (var name in (abiList ?? string.Empty).Split(ListSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var abi = Find(name);
            if (abi is not null) return abi;
        }
        return null;
    }

    /// <summary>ABI の並びを「a,b」の文字列にする（Gradle の -Pseed.abis・ログ用）。</summary>
    /// <param name="abis">ABI の並び。</param>
    /// <returns>カンマ区切りの名前。</returns>
    public static string Describe(IEnumerable<AndroidAbi> abis) =>
        string.Join(ListSeparator, abis.Select(abi => abi.Name));
}
