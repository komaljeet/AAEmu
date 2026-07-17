using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;

using NLog;

namespace AAEmu.Game.Services.AaemuCustom;

/// <summary>
/// Skill Point Tome — a repurposed in-game item (template id <see cref="ItemId"/>)
/// that the player "uses" to convert Honor into a bonus skill point via the
/// aaemu-custom sidecar. The sidecar is authoritative for the bonus-point total:
/// it deducts the configured Honor cost and writes <c>character_skill_points.points</c>.
/// This helper only fires the call, consumes the tome on success, and syncs the
/// character's in-memory <see cref="CharacterSkills.BonusSkillPoints"/> to the
/// sidecar's returned total.
/// </summary>
/// <remarks>
/// Best-effort: when the sidecar is down/disabled, or the player lacks the
/// configured Honor, <see cref="TryUse"/> returns <c>false</c> and the tome is
/// NOT consumed, so the player keeps the item and can retry. The client only
/// needs this item to be usable — a <c>use_skill_id</c> set in client item data
/// so it sends <c>CSStartSkillPacket</c> with a <c>SkillItem</c> caster. The
/// server intercepts by template id regardless of the skill id value, so any
/// valid trigger skill works.
/// <para>
/// Bonus skill points are not a native client concept: the 1.2 client derives
/// available skill points from level with no server sync packet. Learning is
/// server-gated though, so a player with bonus points can learn beyond their
/// level's allowance once the client sends the learn request. The client UI may
/// not display the bonus total; that is a client-side display concern.
/// </para>
/// </remarks>
public static class SkillPointTome
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Item template id repurposed as the Skill Point Tome
    /// (공간 기록서 / Space Record Book).</summary>
    public const uint ItemId = 19885;

    /// <summary>
    /// Attempt to use a Skill Point Tome. On success the sidecar deducts Honor,
    /// grants a bonus skill point, the tome is consumed, and the character's
    /// <see cref="CharacterSkills.BonusSkillPoints"/> is synced to the sidecar's
    /// returned total. On any failure the tome is left intact.
    /// </summary>
    /// <param name="player">The character using the tome.</param>
    /// <param name="tome">The tome item being used (the triggering stack).</param>
    /// <returns><c>true</c> if a bonus point was granted and the tome consumed.</returns>
    public static bool TryUse(Character player, Item tome)
    {
        if (player == null || tome == null)
            return false;

        long newTotal;
        try
        {
            // Sync call from the packet handler thread; the local sidecar answers
            // in well under the HTTP client's 5s timeout, and a down sidecar fails
            // the TCP connect instantly.
            newTotal = AaemuCustomClient.Instance
                .UseSkillPointTomeAsync(player.AccountId, player.Id)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn($"skill point tome: sidecar call failed for char {player.Id}: {ex.Message}");
            player.SendMessage("The tome failed to activate.");
            return false;
        }

        // -1 = sidecar down/disabled, or insufficient Honor. Either way, do not
        // consume the tome so the player can retry.
        if (newTotal < 0)
        {
            player.SendMessage("You don't have enough Honor to use this tome.");
            return false;
        }

        // Consume one tome from the triggering stack. ConsumeItem handles stacks
        // and emits the SCItemTaskSuccessPacket the client needs to update the bag.
        var consumed = 0;
        var container = tome._holdingContainer;
        if (container != null)
            consumed = container.ConsumeItem(ItemTaskType.ConsumeSkillSource, tome.TemplateId, 1, tome);

        if (consumed <= 0)
        {
            Logger.Warn($"skill point tome: failed to consume item {tome.TemplateId} for char {player.Id}");
            player.SendMessage("The tome failed to activate.");
            return false;
        }

        player.Skills.BonusSkillPoints = (int)newTotal;
        player.SendMessage($"You gained a skill point! Bonus skill points: {newTotal}.");
        Logger.Info($"skill point tome: char {player.Id} ({player.Name}) granted bonus point, total now {newTotal}");
        return true;
    }
}