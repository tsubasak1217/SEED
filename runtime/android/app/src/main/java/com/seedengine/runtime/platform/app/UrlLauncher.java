// ============================================================
//  UrlLauncher.java — URL を端末のアプリ（ブラウザ・メール・電話・その URL を受けるアプリ）で開く（メインプロセス。W1-6）
//
//  ACTION_VIEW の暗黙の Intent を、アプリの Context から FLAG_ACTIVITY_NEW_TASK 付きで startActivity する。
//    ・開けるアプリが無いかは startActivity の ActivityNotFoundException で知る（resolveActivity は使わない）。Android 11 以降は
//      パッケージの可視性で PackageManager の問い合わせ（queryIntentActivities・resolveActivity）が絞られ、<queries> を宣言しないと
//      ブラウザが見えないことがある一方、startActivity はパッケージの可視性を必要としないので <queries> も要らない
//      （developer.android.com「Fulfill common use cases while having limited package visibility」の「Open URLs in a browser or
//      other app」。2026-09-27 に読んだ）。
//    ・アプリの Context（ContextImpl）の startActivity は呼び出しのスレッドを確かめない（AOSP android16-release の
//      ContextImpl.startActivity・Instrumentation.execStartActivity を読んだ）ので、エンジンのスレッドから同期に呼び、
//      結果（開けたか・開けるアプリが無いか）をそのまま返答にする。Activity の Context の startActivity は描画面の View に触るので使わない。
//    ・FLAG_ACTIVITY_NEW_TASK … アプリの Context から開くのに要る（無いと AndroidRuntimeException）。ブラウザは自分のタスクで開き、
//      戻ると MainActivity に戻る。
//  scheme は Uri.normalizeScheme で小文字にそろえる（Android の Intent の照合は scheme の大文字小文字を区別する）。
//  URL を開いてよいかの判定は UrlPolicy（先に済ませてから呼ぶ）。
// ============================================================

package com.seedengine.runtime.platform.app;

import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;

/**
 * URL を開く（static のみ。どのスレッドから呼んでもよい）。
 */
public final class UrlLauncher {

    private UrlLauncher() {
    }

    /**
     * URL を開く。
     *
     * @param applicationContext アプリの Context（Activity ではない）
     * @param url                UrlPolicy で ALLOWED と判定した URL
     * @return 開いたら true。開けるアプリが無ければ false
     */
    public static boolean open(Context applicationContext, String url) {
        Uri uri = Uri.parse(url).normalizeScheme();
        Intent intent = new Intent(Intent.ACTION_VIEW, uri).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        try {
            applicationContext.startActivity(intent);
            return true;
        } catch (ActivityNotFoundException e) {
            return false;
        }
    }
}
