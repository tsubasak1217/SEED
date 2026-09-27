// ============================================================
//  LaunchReasonCommand.java — platform.launch_reason（この起動の理由を返す。メインプロセスで答える。W1-4a）
//
//  返答 { launch: {kind, id, action_id, scheduled_at_utc_ms, fired_at_utc_ms, payload_json} }。
//  理由は MainActivity の onCreate・onNewIntent が LaunchReason で決めて SeedPlatform に預けたもの（最後に届いた Intent の理由）。
//  デスクトップの模擬（desktop_sim/app_commands.rs）は常に launcher を返す。
// ============================================================

package com.seedengine.runtime.platform.local;

import com.seedengine.runtime.platform.PlatformContract;
import com.seedengine.runtime.platform.PlatformJson;
import com.seedengine.runtime.platform.SeedPlatform;

import org.json.JSONObject;

/**
 * platform.launch_reason。
 */
final class LaunchReasonCommand implements MainProcessCommand {

    @Override
    public byte[] handle(JSONObject arguments) {
        JSONObject fields = new JSONObject();
        PlatformJson.put(fields, PlatformContract.KEY_LAUNCH, SeedPlatform.launchReason().toJson());
        return PlatformJson.okReply(fields);
    }
}
