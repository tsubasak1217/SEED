// ============================================================
//  OpenUrlCommand.java — app.open_url（URL を端末のアプリで開く。W1-6）
//
//  引数 { url }。返答 { scheme }（小文字にそろえた scheme）。
//    1. url が文字列でない → invalid_argument
//    2. app/UrlPolicy の判定: 形の誤り → invalid_argument、file / content / javascript → scheme_not_allowed
//    3. 操作する Activity が無い（破棄された）→ no_activity（前面のアプリだけが他のアプリの画面を開ける）
//    4. app/UrlLauncher で ACTION_VIEW を startActivity（アプリの Context・FLAG_ACTIVITY_NEW_TASK・呼び出し元のスレッドで同期）→
//       開けるアプリが無ければ no_handler
//  ログには URL そのもの（トークン等を含みうる）を出さず、scheme と長さだけを出す。デスクトップの模擬は http / https / mailto だけを
//  PC の既定のアプリで開き、ほかの scheme は判定だけ（runtime/src/engine/platform/bridge/desktop_sim/app_commands.rs）。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.app.Activity;
import android.util.Log;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.app.UrlLauncher;
import com.seedengine.runtime.platform.app.UrlPolicy;

import org.json.JSONObject;

/**
 * app.open_url。
 */
final class OpenUrlCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        Object value = arguments.opt(PlatformContract.KEY_APP_URL);
        if (!(value instanceof String)) {
            return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_ARGUMENT, PlatformContract.KEY_APP_URL + " は文字列にしてください");
        }
        String url = (String) value;
        UrlPolicy.Result check = UrlPolicy.check(url);
        switch (check.verdict) {
            case INVALID:
                return PlatformJson.errorReply(PlatformContract.ERROR_INVALID_ARGUMENT, check.detail);
            case SCHEME_NOT_ALLOWED:
                return PlatformJson.errorReply(PlatformContract.ERROR_SCHEME_NOT_ALLOWED, check.detail);
            default:
                break;
        }
        Activity activity = HostActivity.get();
        if (activity == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_ACTIVITY);
        }
        String summary = check.scheme + ": の URL（" + url.codePointCount(0, url.length()) + " 文字）";
        if (!UrlLauncher.open(activity.getApplicationContext(), url)) {
            Log.w(PlatformContract.LOG_TAG, summary + "を開けるアプリがありません");
            return PlatformJson.errorReply(PlatformContract.ERROR_NO_HANDLER, check.scheme + ": の URL を開けるアプリが端末にありません");
        }
        Log.i(PlatformContract.LOG_TAG, summary + "を開きました");
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_APP_SCHEME, check.scheme);
        return PlatformJson.okReply(fields);
    }
}
