namespace SEED.Platform;

/// <summary>
/// 権限を求めた結果のイベント "platform.permission_result" の中身（不変値型。W1-5）。
///
/// <para>
/// 受け方: <c>this.On(PermissionResultEvent.Name, (string json) =&gt; { if (PermissionResultEvent.TryParse(json, out var e) &amp;&amp; e.RequestId == myId) … });</c>
/// （SEED.Events の引数はイベントの JSON 全体）。<see cref="Permissions.Request"/> を呼んだフレームより後に届く（確認の画面・設定の画面なら
/// 利用者が答えて戻ってから）。同じ種類の要求が重なったときは、出ている画面の結果がそれぞれの ID で届く。
/// </para>
/// </summary>
public readonly struct PermissionResultEvent
{
    /// <summary>イベントの名前（SEED.Events でもこの名前で届く）。</summary>
    public const string Name = "platform.permission_result";

    /// <summary>要求の ID（<see cref="Permissions.Request"/> の戻り値）。</summary>
    public int RequestId { get; }

    /// <summary>種類。</summary>
    public PermissionKind Kind { get; }

    /// <summary>種類の元の文字列（"post_notifications" など）。</summary>
    public string KindName { get; }

    /// <summary>結果の状態。</summary>
    public PermissionStatus Status { get; }

    /// <summary>状態の元の文字列（"granted" など）。</summary>
    public string StatusName { get; }

    /// <summary>デスクトップの模擬が作ったか。</summary>
    public bool Simulated { get; }

    /// <summary>値を作る。</summary>
    internal PermissionResultEvent(long requestId, string kindName, string statusName, bool simulated)
    {
        RequestId = requestId is > 0 and <= int.MaxValue ? (int)requestId : PermissionJson.NoRequestId;
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
    public static bool TryParse(string json, out PermissionResultEvent value) =>
        AlarmJson.TryReadEvent(json, Name, data => new PermissionResultEvent(
            AlarmJson.GetLong(data, PermissionJson.KeyRequestId),
            AlarmJson.GetString(data, PermissionJson.KeyKind),
            AlarmJson.GetString(data, PermissionJson.KeyStatus),
            AlarmJson.GetBool(data, AlarmJson.KeySimulated)), out value);

    /// <summary>ログ向けの 1 行。</summary>
    public override string ToString() => $"要求 {RequestId}: {KindName} → {StatusName}";
}
