// ============================================================
//  AaptBadgingParser.cs — aapt2 dump badging の出力から、配布物の要点（ID・版・SDK・debuggable・権限・ABI）を読む（純粋な処理。段階D）
//
//  出力の例（build-tools 36.0.0 の aapt2 で実物の APK から確かめた。古い aapt は minSdk を sdkVersion: と書くので両方読む）:
//    package: name='com.example.game' versionCode='3' versionName='1.0.3' ...
//    minSdkVersion:'29'
//    targetSdkVersion:'36'
//    uses-permission: name='android.permission.INTERNET'
//    application-debuggable            … debuggable のときだけ出る行
//    native-code: 'arm64-v8a' 'x86_64'
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SEEDEditor.Android.Release;

/// <summary>aapt2 dump badging から読んだ要点。</summary>
/// <param name="PackageName">アプリ ID。</param>
/// <param name="VersionCode">versionCode。</param>
/// <param name="VersionName">versionName。</param>
/// <param name="MinSdk">minSdk。</param>
/// <param name="TargetSdk">targetSdk。</param>
/// <param name="Debuggable">debuggable か。</param>
/// <param name="Permissions">権限。</param>
/// <param name="NativeCode">native-code の ABI。</param>
public sealed record AaptBadging(
    string? PackageName, int? VersionCode, string? VersionName, int? MinSdk, int? TargetSdk, bool Debuggable,
    IReadOnlyList<string> Permissions, IReadOnlyList<string> NativeCode);

/// <summary>aapt2 dump badging の出力の読み取り。</summary>
public static class AaptBadgingParser
{
    /// <summary>package の行の頭。</summary>
    private const string PackagePrefix = "package:";

    /// <summary>minSdk の行の頭（build-tools 36 の aapt2）。</summary>
    private const string MinSdkPrefix = "minSdkVersion:";

    /// <summary>minSdk の行の頭（古い aapt の書き方）。</summary>
    private const string LegacyMinSdkPrefix = "sdkVersion:";

    /// <summary>targetSdk の行の頭。</summary>
    private const string TargetSdkPrefix = "targetSdkVersion:";

    /// <summary>権限の行の頭。</summary>
    private const string PermissionPrefix = "uses-permission:";

    /// <summary>debuggable の行。</summary>
    private const string DebuggableLine = "application-debuggable";

    /// <summary>ABI の行の頭。</summary>
    private const string NativeCodePrefix = "native-code:";

    /// <summary>name='value' の組。</summary>
    private static readonly Regex AttributePattern = new(@"([A-Za-z][A-Za-z0-9_-]*)='([^']*)'", RegexOptions.CultureInvariant);

    /// <summary>'value' の並び。</summary>
    private static readonly Regex QuotedPattern = new(@"'([^']*)'", RegexOptions.CultureInvariant);

    /// <summary>
    /// 出力を読む。
    /// </summary>
    /// <param name="lines">aapt2 dump badging の出力の行。</param>
    /// <returns>要点（読めなかった項目は null・空）。</returns>
    public static AaptBadging Parse(IEnumerable<string> lines)
    {
        string? package = null, versionName = null;
        int? versionCode = null, minSdk = null, targetSdk = null;
        var debuggable = false;
        var permissions = new List<string>();
        var nativeCode = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith(PackagePrefix, StringComparison.Ordinal))
            {
                var attributes = Attributes(line);
                package = attributes.GetValueOrDefault("name");
                versionCode = ParseInt(attributes.GetValueOrDefault("versionCode"));
                versionName = attributes.GetValueOrDefault("versionName");
            }
            else if (line.StartsWith(TargetSdkPrefix, StringComparison.Ordinal))
            {
                targetSdk = ParseInt(FirstQuoted(line));
            }
            else if (line.StartsWith(MinSdkPrefix, StringComparison.Ordinal) || line.StartsWith(LegacyMinSdkPrefix, StringComparison.Ordinal))
            {
                minSdk = ParseInt(FirstQuoted(line));
            }
            else if (line.StartsWith(PermissionPrefix, StringComparison.Ordinal))
            {
                if (Attributes(line).GetValueOrDefault("name") is { } name) permissions.Add(name);
            }
            else if (string.Equals(line, DebuggableLine, StringComparison.Ordinal))
            {
                debuggable = true;
            }
            else if (line.StartsWith(NativeCodePrefix, StringComparison.Ordinal))
            {
                nativeCode.AddRange(QuotedPattern.Matches(line).Select(m => m.Groups[1].Value));
            }
        }
        return new AaptBadging(package, versionCode, versionName, minSdk, targetSdk, debuggable, permissions, nativeCode);
    }

    /// <summary>行の name='value' をすべて読む（同じ名前は最初のもの）。</summary>
    private static Dictionary<string, string> Attributes(string line)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in AttributePattern.Matches(line))
        {
            result.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
        }
        return result;
    }

    /// <summary>行の最初の 'value'。</summary>
    private static string? FirstQuoted(string line)
    {
        var match = QuotedPattern.Match(line);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>整数として読む（読めなければ null）。</summary>
    private static int? ParseInt(string? text) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
