// ============================================================
//  LockRowItem.cs — ロックタブの 1 行
//
//  【役割】
//  <see cref="SEEDEditor.VersionControl.Model.LockInfo"/> を表示用の器へ写す。
//
//  【「不明」を普通の状態として出す】
//  Lore サーバに利用者認証（[server.auth]）を設定しない構成では、
//  ロックの所有者が常に <c>&lt;unknown&gt;</c> になる。これは故障ではなく
//  今の SEED の既定構成そのものなので、赤字や警告にはせず淡々と「不明」と書く。
//  そして **不明なロックには解除ボタンを出さない**
//  （自分のものだと決めつけると、他人の編集権を黙って奪い得る）。
// ============================================================

using SEEDEditor.VersionControl.Model;
using SEEDEditor.VersionControl.Presentation;

namespace SEEDEditor.Panels.VersionControl;

/// <summary>
/// ロックタブの 1 行（不変）。
/// </summary>
public sealed class LockRowItem
{
    /// <summary>リポジトリ相対パス。</summary>
    public string RelativePath { get; }

    /// <summary>保持者の表示（自分 / 他の人（名前）/ 不明 / ロックなし）。</summary>
    public string HolderText { get; }

    /// <summary>取得日時（不明なら「日時不明」）。</summary>
    public string TimestampText { get; }

    /// <summary>解除ボタンを出すか（自分のロックだけ）。</summary>
    public bool CanRelease { get; }

    /// <summary>
    /// このエディタが自動で取得しているロックか（開いているあいだ保持しているもの）。
    ///
    /// <para>
    /// 利用者から見ると「掛けた覚えのないロック」になるので、印を出して
    /// 手で掛けたロックと区別できるようにする。
    /// </para>
    /// </summary>
    public bool IsAutoHeld { get; }

    /// <summary>解除ボタンの文言。</summary>
    public static string ReleaseButtonText { get; } =
        SEEDEditor.VersionControl.VersionControlMessages.PANEL_LOCK_RELEASE_BUTTON;

    /// <summary>自動ロックの印の文言。</summary>
    public static string AutoHeldText { get; } =
        SEEDEditor.VersionControl.VersionControlMessages.PANEL_LOCK_AUTO_HELD;

    /// <summary>自動ロックの印のツールチップ。</summary>
    public static string AutoHeldTooltip { get; } =
        SEEDEditor.VersionControl.VersionControlMessages.PANEL_LOCK_AUTO_HELD_TOOLTIP;

    /// <summary>ロック情報から表示用の行を作る。</summary>
    /// <param name="info">元のロック情報。</param>
    public LockRowItem(LockInfo info)
    {
        RelativePath  = info.Path;
        HolderText    = VersionControlDisplay.ToLockHolderText(info.Holder, info.Owner);
        TimestampText = VersionControlDisplay.ToTimestampText(info.AcquiredAtUtc);
        CanRelease    = VersionControlDisplay.CanRelease(info.Holder);
        // 台帳に載っているのは「このエディタが自動で取ったもの」だけ。
        // 手で掛けたロックは載らないので、ここが真になることは無い。
        IsAutoHeld    = SEEDEditor.VersionControl.Locking.LockGatekeeper.IsAutoHeld(info.Path);
    }
}
