using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AAEmu.Game.Models.Game.Mails;

using NLog;

namespace AAEmu.Game.Services.AaemuCustom;

/// <summary>
/// Delivers aaemu-custom world-boss loot (bank-funded gold) to the killing raid via mail.
/// Called fire-and-forget from <c>Npc.DoDie</c> so the death-handling thread never blocks
/// on the sidecar HTTP call. Mail is used (rather than the live wallet) so rewards reach
/// members who log off before delivery. All failures are logged and swallowed — boss death
/// is never disrupted by the sidecar being down.
/// </summary>
internal static class BossLootDelivery
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Notify the sidecar of a boss kill and mail each member their gold.
    /// </summary>
    /// <param name="bossId">Boss template id (stable identifier, not the per-instance ObjId).</param>
    /// <param name="teamId">Killing team/raid id (0 if solo).</param>
    /// <param name="roster">Raid roster captured at death: character id, account id, character name.</param>
    public static async Task DeliverAsync(
        long bossId,
        long teamId,
        List<(long CharId, long AccountId, string Name)> roster)
    {
        if (roster.Count == 0)
            return;

        var members = roster.Select(r => (r.CharId, r.AccountId)).ToList();
        var loot = await AaemuCustomClient.Instance.OnBossKilledAsync(bossId, teamId, members)
            .ConfigureAwait(false);
        if (loot == null || loot.Count == 0)
            return;

        var nameById = roster.ToDictionary(r => r.CharId, r => r.Name);
        foreach (var entry in loot)
        {
            if (entry.Gold <= 0)
                continue;
            if (!nameById.TryGetValue(entry.CharacterId, out var name) || string.IsNullOrEmpty(name))
                continue;

            // sidecar gold is in gold units; mail money is in copper (1g = 10000c)
            var copper = (int)(entry.Gold * 10000);
            SendBossLootMail((uint)entry.CharacterId, name, copper, entry.Thunderstruck);
        }
    }

    private static void SendBossLootMail(uint charId, string name, int copper, bool thunderstruck)
    {
        var mail = new BaseMail
        {
            MailType = MailType.Normal,
            Title = "World Boss Reward",
            ReceiverName = name,
            Header =
            {
                SenderId = 0,
                SenderName = "WorldBoss",
                ReceiverId = charId,
            },
            Body =
            {
                Text = thunderstruck
                    ? "Reward for landing the killing blow on a world boss. Fortune favors you — a Thunderstruck Tree was logged."
                    : "Reward for landing the killing blow on a world boss.",
                SendDate = System.DateTime.UtcNow,
                RecvDate = System.DateTime.UtcNow,
            },
        };
        mail.AttachMoney(copper);
        if (!mail.Send())
            Logger.Warn($"BossLootDelivery: failed to send loot mail to {name} ({charId}) for {copper}c");
    }
}