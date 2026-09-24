// ============================================================
//  RunAsTarArchive.cs — run-as で端末へ送る tar のストリームを作る
//
//  【なぜ tar か】
//  実機（Android 11 以降）では adb push が外部アプリ専用フォルダに作ったフォルダは shell の所有になり、
//  アプリから読めない。そこでデバッグ版 APK の run-as でアプリの権限になり、ホストで作った tar のストリームを
//  アプリの内部データフォルダへ展開する（docs/android.md §4.5・§17.7）。以前は Windows の tar.exe の出力を
//  pwsh のパイプで adb へ渡していた（pwsh 7.4 未満はパイプがバイト列を壊すので 7.4 以上が必須だった）。
//  ここでは .NET の TarWriter でストリームを直接 adb の標準入力へ書く（外部の tar は要らない）。
//
//  【形式】
//  GNU 形式（長いパスは GNU の LongLink）。端末の toybox tar が読める。
//  権限はフォルダ 0700・ファイル 0600（展開の後にも chmod で絞る。他のユーザーから読めないように）。
//  所有者は付けない（run-as のアプリの権限で展開するので、端末の tar は所有者を変えない）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SEEDEditor.Android.Adb;

/// <summary>run-as で送る tar のストリームを作る。</summary>
public static class RunAsTarArchive
{
    /// <summary>フォルダの権限（所有者だけ読み書き実行）。</summary>
    private const UnixFileMode DirectoryPermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>ファイルの権限（所有者だけ読み書き）。</summary>
    private const UnixFileMode FilePermissions = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>tar の中のパスの区切り。</summary>
    private const char TarPathSeparator = '/';

    /// <summary>
    /// フォルダの中身をすべて（サブフォルダごと）tar にしてストリームへ書く。エントリ名はフォルダからの相対パス。
    /// </summary>
    /// <param name="sourceDirectory">送るフォルダ。</param>
    /// <param name="output">書き込み先（adb の標準入力）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>書いたファイルの数と合計バイト数。</returns>
    public static async Task<(int Files, long Bytes)> WriteDirectoryAsync(
        string sourceDirectory, Stream output, CancellationToken cancellationToken)
    {
        var root = new DirectoryInfo(sourceDirectory);
        var entries = new List<(FileSystemInfo Item, string Name)>();
        CollectEntries(root, root, entries);
        return await WriteEntriesAsync(entries, output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 指定のファイルだけを tar にしてストリームへ書く（エントリ名はファイル名。フォルダ構造は持たない）。
    /// </summary>
    /// <param name="files">送るファイル。</param>
    /// <param name="output">書き込み先（adb の標準入力）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>書いたファイルの数と合計バイト数。</returns>
    public static Task<(int Files, long Bytes)> WriteFilesAsync(
        IEnumerable<FileInfo> files, Stream output, CancellationToken cancellationToken) =>
        WriteEntriesAsync(files.Select(file => ((FileSystemInfo)file, file.Name)).ToList(), output, cancellationToken);

    /// <summary>フォルダを辿ってエントリを集める（名前順。フォルダはその中身より前）。</summary>
    /// <param name="root">送るフォルダ（相対パスの基準）。</param>
    /// <param name="directory">今辿っているフォルダ。</param>
    /// <param name="entries">集めたエントリ。</param>
    private static void CollectEntries(DirectoryInfo root, DirectoryInfo directory, List<(FileSystemInfo Item, string Name)> entries)
    {
        foreach (var item in directory.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var name = Path.GetRelativePath(root.FullName, item.FullName).Replace(Path.DirectorySeparatorChar, TarPathSeparator);
            entries.Add((item, name));
            if (item is DirectoryInfo sub) CollectEntries(root, sub, entries);
        }
    }

    /// <summary>エントリを tar として書く（書き終えたら終端のブロックも書く。ストリームは閉じない）。</summary>
    /// <param name="entries">書くもの（ファイルかフォルダと、tar の中の名前）。</param>
    /// <param name="output">書き込み先。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>書いたファイルの数と合計バイト数。</returns>
    private static async Task<(int Files, long Bytes)> WriteEntriesAsync(
        IReadOnlyList<(FileSystemInfo Item, string Name)> entries, Stream output, CancellationToken cancellationToken)
    {
        var fileCount = 0;
        var byteCount = 0L;
        await using (var writer = new TarWriter(output, TarEntryFormat.Gnu, leaveOpen: true))
        {
            foreach (var (item, name) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item is DirectoryInfo)
                {
                    var directoryEntry = new GnuTarEntry(TarEntryType.Directory, name + TarPathSeparator)
                    {
                        Mode             = DirectoryPermissions,
                        ModificationTime = item.LastWriteTimeUtc,
                    };
                    await writer.WriteEntryAsync(directoryEntry, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var file = (FileInfo)item;
                await using var content = new FileStream(
                    file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1, useAsync: true);
                var fileEntry = new GnuTarEntry(TarEntryType.RegularFile, name)
                {
                    Mode             = FilePermissions,
                    ModificationTime = file.LastWriteTimeUtc,
                    DataStream       = content,
                };
                await writer.WriteEntryAsync(fileEntry, cancellationToken).ConfigureAwait(false);
                fileCount++;
                byteCount += file.Length;
            }
        }
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return (fileCount, byteCount);
    }
}
