using System;
using System.Collections.Generic;

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
/// Mails each raid member their closed-loop gold payout for a world-boss kill.
/// The sidecar call itself (<c>OnBossKilledAsync</c>) is made synchronously in
/// <c>Npc.DoDie</c> so the death path knows whether the sidecar acknowledged the
/// kill (and thus owns the respawn); this helper only handles the offline-safe
/// mail delivery, fired fire-and-forget so the death thread is never blocked on
/// mail I/O. Best-effort: a down sidecar returns no loot and nothing is mailed
/// (native bosses drop no gold, so there is no native fallback to mimic).
/// </summary>
public static class BossLootDelivery
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private const string SenderName = ".worldBoss";
    private const string MailTitle = "World Boss Reward";

    /// <summary>
    /// Mail the given per-member gold loot. <paramref name="loot"/> is the list
    /// returned by <c>OnBossKilledAsync</c>; each entry's <c>Gold</c> is in sidecar
    /// gold units and is converted to copper (×10000) on mail.
    /// </summary>
    public static async Task MailLootAsync(long bossId, List<BossMemberLoot> loot)
    {
        try
        {
            if (loot == null || loot.Count == 0)
                return;

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
                MailBossGold(bossId, entry.CharacterId, copper, entry.Thunderstruck);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"boss {bossId} loot delivery failed: {ex.Message}");
        }
    }

    private static void MailBossGold(long bossId, long characterId, int copper, bool thunderstruck)
    {
        var name = NameManager.Instance.GetCharacterName((uint)characterId);
        if (string.IsNullOrEmpty(name))
        {
            Logger.Warn($"boss {bossId} loot: no name for character {characterId}, skipping mail");
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
            Logger.Info($"mailed boss {bossId} loot to {name} ({characterId}): {copper}c" + (thunderstruck ? " +thunderstruck" : ""));
        else
            Logger.Warn($"mail.Send() returned false for boss {bossId} loot to {name} ({characterId})");
    }
}