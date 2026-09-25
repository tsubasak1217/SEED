// ============================================================
//  AndroidIpcException.cs — Android の IPC（端末のアプリとの TCP の通信路）がつながらない・応答しない（段階D-1）
//
//  WPF に依存しない（SeedAndroid・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.Android.Ipc;

/// <summary>つながらなかった・応答が無かった理由の種類。</summary>
public enum AndroidIpcFailureKind
{
    /// <summary>adb forward を張れなかった（端末が外れた・adb の失敗）。</summary>
    Forward,

    /// <summary>端末のアプリが待ち受けていない（起動していない・古い APK・デバッグ版でない・INTERNET 権限が無い）。</summary>
    NotListening,

    /// <summary>つながったが挨拶が来ない（別の接続＝エディタや SeedAndroid がつながっている・アプリが止まっている）。</summary>
    NoGreeting,

    /// <summary>通信路が切れた（命令を送れない・応答の前に切れた）。</summary>
    Disconnected,

    /// <summary>命令の応答が時間内に来なかった。</summary>
    NoReply,
}

/// <summary>Android の IPC がつながらない・応答しない。</summary>
public sealed class AndroidIpcException : Exception
{
    /// <summary>理由の種類と説明を指定して作る。</summary>
    /// <param name="kind">理由の種類。</param>
    /// <param name="message">利用者へ見せる説明（何を確かめればよいかを含める）。</param>
    /// <param name="inner">元の例外（無ければ null）。</param>
    public AndroidIpcException(AndroidIpcFailureKind kind, string message, Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
    }

    /// <summary>理由の種類。</summary>
    public AndroidIpcFailureKind Kind { get; }
}
