// ============================================================
//  EmulatorHost.cs — 端末の用意（AndroidDeviceProvisioner）が触る外の世界（adb・emulator）の窓口
//
//  AndroidDeviceProvisioner（どの端末を使うか・エミュレータの起動と待ち合わせの段取り）は、adb と emulator を
//  この窓口越しに呼ぶ。本番は AdbClient と EmulatorLauncher をそのまま呼び、単体テストは偽物に差し替えて
//  「新しく現れたエミュレータの見分け・起動の完了待ち・時間切れ・起動直後の終了」を端末なしで確かめる。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Processes;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Android.Emulator;

/// <summary>端末の用意が使う adb・emulator の窓口。</summary>
public interface IEmulatorHost
{
    /// <summary>端末の一覧（adb devices -l）。</summary>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>端末の一覧。</returns>
    Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken);

    /// <summary>エミュレータの AVD 名（adb emu avd name。コンソールにつながらなければ null）。</summary>
    /// <param name="serial">エミュレータのシリアル。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>AVD 名。</returns>
    Task<string?> GetAvdNameAsync(string serial, CancellationToken cancellationToken);

    /// <summary>起動が終わったか（sys.boot_completed が 1。問い合わせに失敗したら false）。</summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>終わっていれば true。</returns>
    Task<bool> IsBootCompletedAsync(string serial, CancellationToken cancellationToken);

    /// <summary>AVD の一覧（emulator -list-avds）。</summary>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>AVD の名前。</returns>
    Task<IReadOnlyList<string>> ListAvdsAsync(CancellationToken cancellationToken);

    /// <summary>エミュレータを切り離して起動する。</summary>
    /// <param name="avdName">AVD の名前。</param>
    /// <returns>起動した emulator.exe。</returns>
    IDetachedProcess StartEmulator(string avdName);
}

/// <summary>本番の窓口（AdbClient と EmulatorLauncher をそのまま呼ぶ）。</summary>
public sealed class EmulatorHost : IEmulatorHost
{
    /// <summary>adb。</summary>
    private readonly AdbClient _adb;

    /// <summary>道具の場所（emulator.exe はエミュレータを起動するときだけ探す。実機で実行するなら要らない）。</summary>
    private readonly AndroidToolchain _toolchain;

    /// <summary>adb と道具の場所を指定して作る。</summary>
    /// <param name="adb">adb。</param>
    /// <param name="toolchain">道具の場所。</param>
    public EmulatorHost(AdbClient adb, AndroidToolchain toolchain)
    {
        _adb = adb;
        _toolchain = toolchain;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken) =>
        _adb.ListDevicesAsync(cancellationToken);

    /// <inheritdoc />
    public Task<string?> GetAvdNameAsync(string serial, CancellationToken cancellationToken) =>
        _adb.GetEmulatorAvdNameAsync(serial, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsBootCompletedAsync(string serial, CancellationToken cancellationToken) =>
        _adb.IsBootCompletedAsync(serial, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ListAvdsAsync(CancellationToken cancellationToken) =>
        new EmulatorLauncher(_toolchain.RequireEmulator()).ListAvdsAsync(cancellationToken);

    /// <inheritdoc />
    public IDetachedProcess StartEmulator(string avdName) =>
        new EmulatorLauncher(_toolchain.RequireEmulator()).Start(avdName);
}
