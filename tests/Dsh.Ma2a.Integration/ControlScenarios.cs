using System.Text.Json.Nodes;

namespace Dsh.Ma2a.Integration;

internal static class ControlScenarios
{
    private const int InviteLifetimeMilliseconds = 300_000;
    public static async Task InitializeAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var create = NativeRuntimePair.Mutation("space_create");
        create["name"] = "Native client acceptance";
        var created = await pair.Target.CallAsync(create, cancellationToken);
        pair.SpaceId = NativeRuntimePair.Payload(created).GetProperty("space_id").GetString()!;
        await NativeRuntimePair.PassedAsync("Created an owned Space through native IPC.");
        var replay = await pair.Target.CallAsync(create, cancellationToken);
        NativeRuntimePair.Check(NativeRuntimePair.Payload(replay).GetProperty("space_id").GetString() == pair.SpaceId, "Mutation replay must preserve the created Space.");
        await NativeRuntimePair.PassedAsync("Replayed Space creation without a second Space.");
        await InviteAsync(pair, cancellationToken);
        var details = NativeRuntimePair.Command("space_details_fetch");
        details["space_id"] = pair.SpaceId;
        var members = NativeRuntimePair.Payload(await pair.Target.CallAsync(details, cancellationToken)).GetProperty("members");
        NativeRuntimePair.Check(members.GetArrayLength() == 2, "Enrollment must produce exactly two Space members.");
        await NativeRuntimePair.PassedAsync("Both enrolled Space members appear in native details.");
        var echo = NativeRuntimePair.Payload(await pair.Caller.CallAsync(Echo(pair.TargetId), cancellationToken));
        NativeRuntimePair.Check(echo.GetProperty("payload").GetString() == "Native MA2A acceptance", "Echo must return the exact payload.");
        await pair.GrantAsync(true, cancellationToken);
        await pair.SyncAsync(cancellationToken);
        await NativeRuntimePair.PassedAsync("Authorized Echo, signed service grants and synchronization.");
    }

    private static async Task InviteAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var invitationPath = Path.Combine(pair.Root, "invite");
        var invite = NativeRuntimePair.Mutation("space_invite");
        invite["space_id"] = pair.SpaceId;
        invite["ttl_ms"] = InviteLifetimeMilliseconds;
        invite["output_path"] = invitationPath;
        await pair.Target.CallAsync(invite, cancellationToken);
        await NativeRuntimePair.PassedAsync("Created a private enrollment ticket.");
        var redeem = NativeRuntimePair.Mutation("space_redeem");
        redeem["invitation"] = await File.ReadAllTextAsync(invitationPath, cancellationToken);
        var result = await pair.Caller.CallAsync(redeem, cancellationToken);
        NativeRuntimePair.Check(NativeRuntimePair.Payload(result).GetProperty("space_id").GetString() == pair.SpaceId, "The invitation must enroll the caller in the intended Space.");
        await NativeRuntimePair.PassedAsync("Redeemed the enrollment ticket over real Iroh.");
    }

    public static async Task RevokeAndRestartAsync(NativeRuntimePair pair, CancellationToken cancellationToken)
    {
        var before = await pair.Caller.GetSnapshotAsync(cancellationToken);
        var boot = before.GetProperty("runtime_boot_id").GetString();
        await pair.RestartCallerAsync(cancellationToken);
        await NativeRuntimePair.PassedAsync("Restarted the task-owned Caller Runtime.");
        var after = await pair.Caller.GetSnapshotAsync(cancellationToken);
        NativeRuntimePair.Check(after.GetProperty("runtime_boot_id").GetString() != boot, "Runtime restart must change the snapshot boot identity.");
        var endpoint = NativeRuntimePair.Payload(await pair.Caller.CallAsync(NativeRuntimePair.Command("endpoint_info"), cancellationToken));
        NativeRuntimePair.Check(endpoint.GetProperty("endpoint_id").GetString() == pair.CallerId, "Runtime restart must retain the persistent Endpoint identity.");
        var revoke = NativeRuntimePair.Mutation("space_revoke");
        revoke["space_id"] = pair.SpaceId;
        revoke["peer_endpoint_id"] = pair.CallerId;
        await pair.Target.CallAsync(revoke, cancellationToken);
        await NativeRuntimePair.PassedAsync("Committed signed membership revocation.");
        await ExpectApiErrorAsync(() => pair.Caller.CallAsync(Echo(pair.TargetId), cancellationToken), "unauthorized");
        await NativeRuntimePair.PassedAsync("Runtime restart preserves Endpoint identity, changes boot identity, and signed membership revocation denies remote Echo.");
    }

    private static JsonObject Echo(string target)
    {
        var echo = NativeRuntimePair.Mutation("echo_call");
        echo["target_endpoint_id"] = target;
        echo["payload"] = "Native MA2A acceptance";
        return echo;
    }

    internal static async Task ExpectApiErrorAsync(Func<Task<System.Text.Json.JsonElement>> call, string expected)
    {
        try { await call(); }
        catch (Ma2aProtocolException error) when (error.Error == expected) { return; }
        throw new InvalidOperationException($"The operation must be refused with {expected}.");
    }
}
