using System;
using System.Collections.Generic;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.NPChar;

using NLog;

namespace AAEmu.Game.Services.AaemuCustom;

/// <summary>
/// Periodic respawn poll for world bosses the aaemu-custom sidecar owns.
/// </summary>
/// <remarks>
/// When the sidecar acknowledges a world-boss kill it sets <c>SidecarManagesRespawn</c>
/// on the dead Npc, which makes <c>NpcSpawner.DoDespawn</c> skip the native respawn.
/// The sidecar instead schedules a configurable respawn timer and reports the boss as
/// ready via <c>GET /boss/ready</c> once the timer elapses. This tick polls that
/// endpoint, (re)spawns each ready boss by calling <c>DoSpawn()</c> on the matching
/// <c>NpcSpawner</c>, and confirms via <c>POST /boss/spawned</c> so the boss isn't
/// re-reported as ready next tick.
/// <para>
/// The handler is <c>void(TimeSpan)</c> registered with <c>useAsync: true</c>, so the
/// TickManager runs it on a <c>Task.Run</c> worker with an ActiveTask guard that
/// prevents overlapping invocations. The blocking <c>.GetAwaiter().GetResult()</c>
/// calls therefore stall the worker, never the 20ms tick thread.
/// </para>
/// <para>
/// Double-spawn is prevented by <c>NpcSpawner.DoSpawn</c>'s <c>CurrentSpawnCount &gt;=
/// MaxPopulation</c> guard: a boss that's already alive (or whose confirm hasn't
/// landed yet) won't be re-spawned. The confirm is best-effort; if it fails, the next
/// tick retries it (and DoSpawn no-ops while the boss lives).
/// </para>
/// </remarks>
public static class BossRespawnPoll
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static void Tick(TimeSpan delta)
    {
        if (!AaemuCustomClient.Instance.Enabled)
            return;

        List<long> ready;
        try
        {
            ready = AaemuCustomClient.Instance.GetBossesReadyToSpawnAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn($"boss respawn poll: /boss/ready failed: {ex.Message}");
            return;
        }

        if (ready == null || ready.Count == 0)
            return;

        foreach (var bossId in ready)
            TrySpawnBoss((uint)bossId);

        // Confirm every ready boss so the ready signal clears and the poll doesn't
        // retry forever. Spawners are loaded at startup from JSON; if none matches a
        // boss now, none will later — so confirm even bosses we couldn't spawn.
        try
        {
            _ = AaemuCustomClient.Instance.MarkBossSpawnedAsync(ready).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn($"boss respawn poll: /boss/spawned confirm failed: {ex.Message}");
        }
    }

    private static void TrySpawnBoss(uint bossId)
    {
        var spawned = false;
        foreach (var world in WorldManager.Instance.GetWorlds())
        {
            var spawners = world.SpawnManager?.GetAllSpawners();
            if (spawners == null)
                continue;

            foreach (var list in spawners.Values)
            {
                if (list == null)
                    continue;
                foreach (var spawner in list)
                {
                    if (spawner?.SpawnableNpcs == null)
                        continue;

                    var matches = false;
                    foreach (var n in spawner.SpawnableNpcs)
                    {
                        if (n != null && n.MemberId == bossId)
                        {
                            matches = true;
                            break;
                        }
                    }

                    if (!matches)
                        continue;

                    try
                    {
                        // DoSpawn() respects CurrentSpawnCount >= MaxPopulation, so an
                        // already-live boss (or a not-yet-confirmed one) won't double-spawn.
                        spawner.DoSpawn();
                        spawned = true;
                        Logger.Info($"sidecar boss respawn: spawned template {bossId} via spawner {spawner.UnitId}:{spawner.SpawnerId} in world {world.Id}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"sidecar boss respawn: spawn failed for template {bossId} via spawner {spawner.UnitId}:{spawner.SpawnerId}: {ex.Message}");
                    }
                }
            }
        }

        if (!spawned)
            Logger.Warn($"sidecar boss respawn: no spawner found for ready boss template {bossId}; confirming without spawning");
    }
}