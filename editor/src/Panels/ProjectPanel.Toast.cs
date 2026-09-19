// ============================================================
//  ProjectPanel.Toast.cs — パネルから画面へ一言だけ出す通り道
//
//  【なぜ必要か】
//  トーストの実体（帯の表示と自動消去）は MainWindow が持っており、private である。
//  パネルから直接は呼べないので、起動時に「文字列を渡すと出してくれる関数」を
//  1 本だけ注入してもらう。パネルは MainWindow を知らないまま通知できる。
//
//  【同じ作りの前例】
//  Migration/Presentation/MigrationNotifier.cs（`Action<string>? toast` を受け取る）、
//  VersionControl/Locking/Presentation/LockGateNotifier.cs も同じ注入方式。
//
//  【MessageBox と使い分ける】
//  トーストは「気づけばよい」情報（試聴に失敗した・関連付けが無い）に使う。
//  操作の可否を決めさせたい確認（削除してよいか）は従来どおり MessageBox を使う。
// ============================================================

using System;

namespace SEEDEditor.Panels;

public partial class ProjectPanel
{
    /// <summary>
    /// 画面へ一言出す関数（MainWindow が注入する）。未注入ならログだけになる。
    /// </summary>
    private Action<string>? _toast;

    /// <summary>
    /// トーストの出し口を注入する。起動時に MainWindow から 1 回だけ呼ぶ。
    /// </summary>
    /// <param name="toast">文字列を受け取って画面へ出す関数。</param>
    public void SetToast(Action<string>? toast) => _toast = toast;

    /// <summary>
    /// 画面へ一言出す。注入されていない場合は何もしない
    /// （通知できないことを理由に処理を止めない）。
    /// </summary>
    /// <param name="message">出す文面。</param>
    private void ShowProjectToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        try { _toast?.Invoke(message); }
        catch { /* 通知の失敗で呼び出し元の処理を巻き添えにしない */ }
    }
}
