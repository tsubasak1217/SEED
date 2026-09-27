// ============================================================
//  PermissionCheckCommand.java — permission.check（権限の今の状態を返す。メインプロセスで答える。W1-5）
//
//  引数 { kind }。返答 { kind, status }（status は granted / denied / denied_permanently / needs_settings / not_applicable。
//  判定表は permission/PermissionStatusProbe）。Activity は無くてもよい（rationale を問い合わせられないので、通知の不許可は
//  「拒否の覚え」だけで denied か denied_permanently を決める）。状態の表（PermissionMonitor）は書き換えない。
//  デスクトップの模擬（desktop_sim/permission_commands.rs）は v1 の種類で常に granted。
// ============================================================

package com.seedengine.runtime.platform.local;

import android.content.Context;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.permission.PermissionStatusProbe;

import org.json.JSONObject;

/**
 * permission.check。
 */
final class PermissionCheckCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        Context context = HostActivity.applicationContext();
        if (context == null) {
            return PlatformJson.errorReply(PlatformContract.ERROR_NOT_INITIALIZED);
        }
        PermissionArguments.Parsed parsed = PermissionArguments.read(arguments, context);
        if (parsed.errorReply != null) {
            return parsed.errorReply;
        }
        String status = PermissionStatusProbe.status(context, HostActivity.get(), parsed.kind);
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_PERMISSION_KIND, parsed.kind.wireName);
        PlatformJson.put(fields, PlatformContract.KEY_PERMISSION_STATUS, status);
        return PlatformJson.okReply(fields);
    }
}
