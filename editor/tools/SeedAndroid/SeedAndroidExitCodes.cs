// ============================================================
//  SeedAndroidExitCodes.cs — SeedAndroid の終了コード（Usage の説明・build_and_run.ps1 と一致させる）
// ============================================================

using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Tools.SeedAndroid;

/// <summary>終了コード。</summary>
public static class SeedAndroidExitCodes
{
    /// <summary>成功。</summary>
    public const int Success = 0;

    /// <summary>指定の誤り（引数・設定 JSON・プロジェクトの値）。</summary>
    public const int InvalidRequest = 1;

    /// <summary>道具が無い（SDK / NDK / JDK / cargo / dotnet・リポジトリが見つからない）。</summary>
    public const int Toolchain = 2;

    /// <summary>端末が無い・選べない・使えない状態。</summary>
    public const int Device = 3;

    /// <summary>ビルドの工程の失敗。</summary>
    public const int Build = 4;

    /// <summary>端末の操作の失敗（インストール・転送・起動・停止）。</summary>
    public const int DeviceOperation = 5;

    /// <summary>中断（Ctrl+C。シェルの慣習 128 + SIGINT）。</summary>
    public const int Canceled = 130;

    /// <summary>失敗の種類 → 終了コード。</summary>
    /// <param name="kind">失敗の種類。</param>
    /// <returns>終了コード。</returns>
    public static int For(AndroidFailureKind kind) => kind switch
    {
        AndroidFailureKind.InvalidRequest  => InvalidRequest,
        AndroidFailureKind.Toolchain       => Toolchain,
        AndroidFailureKind.Device          => Device,
        AndroidFailureKind.Build           => Build,
        _                                  => DeviceOperation,
    };
}
