// ============================================================
//  EditorVersion.cs — エディタ（エンジン）のバージョン文字列
//
//  【役割】
//  「このエディタは何版か」を 1 か所で答える。値は SEEDEditor.csproj の
//  <Version> から生成されるアセンブリ属性で、コードには焼き込まない
//  （版を上げるときに触る場所を csproj 1 か所に閉じるため）。
//
//  【利用箇所】
//  ・新規プロジェクト生成時に .seedproj の engine_version へ記録する
//  ・スタート画面のタイトル表示（"SEED Editor 0.1.0"）
//
//  WPF に一切依存しない（単体テストからそのままリンクして使える）。
// ============================================================

using System;
using System.Reflection;

namespace SEEDEditor.Project;

/// <summary>
/// 実行中のエディタアセンブリのバージョンを取得するユーティリティ。
/// 値は初回アクセス時に 1 度だけ解決してキャッシュする。
/// </summary>
public static class EditorVersion
{
    /// <summary>
    /// バージョンがまったく取得できなかったときに使う表示値。
    /// 空文字を返すと「バージョン欄が消えた」のか「0 なのか」が区別できないため、
    /// 明示的に不明であることが分かる文字列にする。
    /// </summary>
    private const string UNKNOWN_VERSION = "0.0.0";

    /// <summary>
    /// InformationalVersion に付く SourceLink 由来のビルドメタデータ区切り（"0.1.0+abcdef"）。
    /// 表示・記録にはコミットハッシュまでは要らないので、ここで切り落とす。
    /// </summary>
    private const char METADATA_SEPARATOR = '+';

    /// <summary>アセンブリバージョンを "major.minor.build" 形式に整形するときの桁数。</summary>
    private const int VERSION_FIELD_COUNT = 3;

    /// <summary>解決済みのバージョン文字列（初回アクセス時に 1 度だけ計算する）。</summary>
    private static readonly Lazy<string> _current = new(Resolve);

    /// <summary>エディタのバージョン文字列（例 "0.1.0"）。</summary>
    public static string Current => _current.Value;

    /// <summary>
    /// アセンブリ属性からバージョン文字列を組み立てる。
    ///
    /// 優先順:
    ///   1. AssemblyInformationalVersion（csproj の &lt;Version&gt; がそのまま入る）
    ///   2. AssemblyVersion（"0.1.0.0" → "0.1.0" に丸める）
    ///   3. <see cref="UNKNOWN_VERSION"/>
    /// </summary>
    private static string Resolve()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // 1) InformationalVersion（csproj の <Version> がそのまま反映される）
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // "0.1.0+7f3c1d" のようなビルドメタデータは表示に不要なので落とす。
            var plus = informational.IndexOf(METADATA_SEPARATOR);
            return (plus >= 0 ? informational[..plus] : informational).Trim();
        }

        // 2) AssemblyVersion（4 桁で入っているので 3 桁へ丸める）
        var version = assembly.GetName().Version;
        if (version is not null) return version.ToString(VERSION_FIELD_COUNT);

        // 3) どちらも取れない（動的アセンブリなど）
        return UNKNOWN_VERSION;
    }
}
