// ============================================================
//  SignerCertificateParser.cs — 配布物の署名の確かめの出力を読む（純粋な処理。段階D）
//
//    APK … apksigner verify --print-certs -v（build-tools）
//            Verifies / DOES NOT VERIFY、Signer #1 certificate DN: …、Signer #1 certificate SHA-256 digest: …（16 進の小文字）
//    AAB … keytool -printcert -jarfile（JDK。AAB は JAR 形式の署名。-J-Duser.language=en で英語に固定）
//            Owner: …、SHA256: AA:BB:…（コロン区切りの大文字）。署名が無ければ "Not a signed jar file"
//  SHA-256 は比べやすいようにコロン無しの小文字にそろえる（NormalizeFingerprint）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;

namespace SEEDEditor.Android.Release;

/// <summary>署名の確かめの結果。</summary>
/// <param name="Verified">署名が確かめられたか（AAB は署名があるか）。</param>
/// <param name="Subject">署名の証明書の名前（DN）。</param>
/// <param name="Sha256">証明書の SHA-256（コロン無しの小文字）。</param>
/// <param name="Schemes">確かめられた署名の方式（APK の v2・v3 等。AAB は JAR）。</param>
public sealed record SignerCertificate(bool Verified, string? Subject, string? Sha256, IReadOnlyList<string> Schemes);

/// <summary>署名の確かめの出力の読み取り。</summary>
public static class SignerCertificateParser
{
    /// <summary>apksigner の「確かめられた」の行。</summary>
    private const string ApksignerVerifiedLine = "Verifies";

    /// <summary>apksigner の証明書の名前の行の見出し。</summary>
    private const string ApksignerDnMarker = "certificate DN:";

    /// <summary>apksigner の SHA-256 の行の見出し。</summary>
    private const string ApksignerSha256Marker = "certificate SHA-256 digest:";

    /// <summary>apksigner の方式の行の頭（Verified using v2 scheme (…): true）。</summary>
    private const string ApksignerSchemePrefix = "Verified using ";

    /// <summary>方式の行の「確かめられた」。</summary>
    private const string SchemeTrueSuffix = ": true";

    /// <summary>keytool の持ち主の行の見出し。</summary>
    private const string KeytoolOwnerLabel = "Owner:";

    /// <summary>keytool の SHA-256 の行の見出し。</summary>
    private const string KeytoolSha256Label = "SHA256:";

    /// <summary>keytool の「署名されていない」の文。</summary>
    private const string KeytoolUnsignedMarker = "Not a signed jar file";

    /// <summary>AAB の署名の方式の表示。</summary>
    public const string JarSchemeName = "JAR";

    /// <summary>
    /// apksigner verify --print-certs -v の出力を読む。
    /// </summary>
    /// <param name="lines">出力の行。</param>
    /// <param name="exitCode">終了コード（0 以外は確かめられなかった）。</param>
    /// <returns>結果。</returns>
    public static SignerCertificate ParseApksigner(IEnumerable<string> lines, int exitCode)
    {
        var list = lines.Select(line => line.Trim()).ToList();
        var verified = exitCode == 0 && list.Contains(ApksignerVerifiedLine, StringComparer.Ordinal);
        var subject = ValueAfterMarker(list, ApksignerDnMarker);
        var sha256 = ValueAfterMarker(list, ApksignerSha256Marker);
        var schemes = list
            .Where(line => line.StartsWith(ApksignerSchemePrefix, StringComparison.Ordinal) && line.EndsWith(SchemeTrueSuffix, StringComparison.Ordinal))
            .Select(line => line[ApksignerSchemePrefix.Length..].Split(' ', 2)[0])
            .ToList();
        return new SignerCertificate(verified, subject, NormalizeFingerprint(sha256), schemes);
    }

    /// <summary>
    /// keytool -printcert -jarfile（英語）の出力を読む（最初の証明書）。
    /// </summary>
    /// <param name="lines">出力の行。</param>
    /// <param name="exitCode">終了コード。</param>
    /// <returns>結果。</returns>
    public static SignerCertificate ParseKeytoolPrintCert(IEnumerable<string> lines, int exitCode)
    {
        var list = lines.Select(line => line.Trim()).ToList();
        var unsigned = list.Any(line => line.Contains(KeytoolUnsignedMarker, StringComparison.OrdinalIgnoreCase));
        var subject = list.FirstOrDefault(line => line.StartsWith(KeytoolOwnerLabel, StringComparison.Ordinal))?[KeytoolOwnerLabel.Length..].Trim();
        var sha256 = list.FirstOrDefault(line => line.StartsWith(KeytoolSha256Label, StringComparison.Ordinal))?[KeytoolSha256Label.Length..].Trim();
        var verified = exitCode == 0 && !unsigned && subject is not null;
        return new SignerCertificate(verified, subject, NormalizeFingerprint(sha256), verified ? new[] { JarSchemeName } : Array.Empty<string>());
    }

    /// <summary>
    /// 証明書の指紋をコロン無しの小文字にそろえる（null・空は null）。
    /// </summary>
    /// <param name="fingerprint">指紋（AA:BB:… か aabb…）。</param>
    /// <returns>そろえた指紋。</returns>
    public static string? NormalizeFingerprint(string? fingerprint) =>
        string.IsNullOrWhiteSpace(fingerprint) ? null : fingerprint.Replace(":", string.Empty).Trim().ToLowerInvariant();

    /// <summary>見出しを含む最初の行の、見出しの後ろ。</summary>
    private static string? ValueAfterMarker(IEnumerable<string> lines, string marker)
    {
        foreach (var line in lines)
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index >= 0) return line[(index + marker.Length)..].Trim();
        }
        return null;
    }
}
