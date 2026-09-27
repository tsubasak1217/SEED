namespace SEED.Platform;

/// <summary>
/// 権限の状態が変わったイベント "platform.permission_changed" の中身（不変値型。W1-5）。
///
/// <para>
/// Android でアプリが前面へ戻ったとき（onResume。設定の画面から戻った・通知の引き下ろしから戻った等）に、APK に機能が入っている 3 種
/// （通知・正確なアラーム・フルスクリーン通知）の状態を前回の onResume と比べ、違っていれば種類ごとに届く。確認の画面で許可したときも、
/// <see cref="PermissionResultEvent"/> の後にこれが届く（どちらを受けてもよい）。プロセスの最初の onResume では届かない（比べる前回が無い）。
/// デスクトップの模擬では起きない。
/// </para>
/// </summary>
public readonly struct PermissionChangedEvent
{
    /// <summary>イベントの名前（SEED.Events でもこの名前で届く）。</summary>
    public const string Name = "platform.permission_changed";

    /// <summary>種類。</summary>
    public PermissionKind Kind { get; }

    /// <summary>種類の元の文字列。</summary>
    public string KindName { get; }

    /// <summary>今の状態。</summary>
    public PermissionStatus Status { get; }

    /// <summary>状態の元の文字列。</summary>
    public string StatusName { get; }

    /// <summary>デスクトップの模擬が作ったか（今は常に false）。</summary>
    public bool Simulated { get; }

    /// <summary>値を作る。</summary>
    internal PermissionChangedEvent(string kindName, string statusName, bool simulated)
    {
        KindName = kindName;
        Kind = PermissionJson.ParseKind(kindName);
        StatusName = statusName;
        Status = PermissionJson.ParseStatus(statusName);
        Simulated = simulated;
    }

    /// <summary>
    /// イベントの JSON（SEED.Events の引数）を読む。
    /// </summary>
    /// <param name="json">イベントの JSON 全体。</param>
    /// <param name="value">読めた値（読めなければ既定値）。</param>
    /// <returns>名前が <see cref="Name"/> で、中身が読めたら true。</returns>
    public static bool TryParse(string json, out PermissionChangedEvent value) =>
        AlarmJson.TryReadEvent(json, Name, data => new PermissionChangedEvent(
            AlarmJson.GetString(data, PermissionJson.KeyKind),
            AlarmJson.GetString(data, PermissionJson.KeyStatus),
            AlarmJson.GetBool(data, AlarmJson.KeySimulated)), out value);

    /// <summary>ログ向けの 1 行。</summary>
    public override string ToString() => $"{KindName} → {StatusName}";
}
