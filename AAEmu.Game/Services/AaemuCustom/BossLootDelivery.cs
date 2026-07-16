using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Mails;

using NLog;

namespace AAEmu.Game.Services.AaemuCustom;

/// <summary>
/// Per-member loot returned by the sidecar for a world-boss kill. <c>Gold</c> is
/// in sidecar gold units; the delivery helper converts to copper (×10000) on mail.
/// </summary>
public sealed class BossMemberLoot
{
    public long CharacterId { get; set; }
    public long Gold { get; set; }
    public bool Thunderstruck { get; set; }
}

/// <summary>
/// Records a world-boss kill with the sidecar and mails each raid member their
/// closed-loop gold payout. Fire-and-forget from <c>Npc.DoDie</c> — mail delivery
/// works whether or not the member is still online, so the death thread is never
/// blocked. Every step is best-effort: a down sidecar returns no loot and nothing
/// is mailed (native bosses drop no gold, so there is no native fallback to mimic).
/// </summary>
public static class BossLootDelivery
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const string SenderName = ".worldBoss";
    private const string MailTitle = "World Boss Reward";

    /// <summary>
    /// Notify the sidecar of a boss kill and mail the returned per-member gold.
    /// <paramref name="members"/> is the killing raid roster as
    /// (character_id, account_id) pairs.
    /// </summary>
    public static async Task RunAsync(long bossId, long raidId, List<(long CharacterId, long AccountId)> members)
    {
        try
        {
            var loot = await AaemuCustomClient.Instance
                .OnBossKilledAsync(bossId, raidId, members)
                .ConfigureAwait(false);
            if (loot == null || loot.Count == 0)
            {
                Logger.Warn($"boss {bossId} kill: sidecar returned no loot (down, unseeded, or empty roster)");
                return;
            }

            foreach (var entry in loot)
            {
                // Sidecar gold → AAEmu copper (1g = 10000c). AttachMoney takes int copper.
                var copperLong = entry.Gold * 10000L;
                if (copperLong <= 0)
                {
                    if (entry.Thunderstruck)
                        Logger.Info($"boss {bossId} member {entry.CharacterId}: thunderstruck roll, no gold");
                    continue;
                }

                var copper = copperLong > int.MaxValue ? int.MaxValue : (int)copperLong;
                MailBossGold(entry.CharacterId, copper, entry.Thunderstruck);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"boss {bossId} loot delivery failed: {ex.Message}");
        }
    }

    private static void MailBossGold(long characterId, int copper, bool thunderstruck)
    {
        var name = NameManager.Instance.GetCharacterName((uint)characterId);
        if (string.IsNullOrEmpty(name))
        {
            Logger.Warn($"boss loot: no name for character {characterId}, skipping mail");
            return;
        }

        var mail = new BaseMail
        {
            MailType = MailType.SysExpress,
            Title = MailTitle,
            ReceiverName = name,
        };
        mail.Header.SenderId = 0;
        mail.Header.SenderName = SenderName;
        mail.Header.ReceiverId = (uint)characterId;
        mail.Body.RecvDate = DateTime.UtcNow;
        mail.Body.Text = thunderstruck
            ? "Reward for slaying a world boss. A thunderstruck tree was among the spoils."
            : "Reward for slaying a world boss.";
        mail.AttachMoney(copper, 0, 0);

        if (mail.Send())
            Logger.Info($"mailed boss loot to {name} ({characterId}): {copper}c" + (thunderstruck ? " +thunderstruck" : ""));
        else
            Logger.Warn($"mail.Send() returned false for boss loot to {name} ({characterId})");
    }
}