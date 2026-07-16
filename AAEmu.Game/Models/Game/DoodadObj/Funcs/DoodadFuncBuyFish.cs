using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Services.AaemuCustom;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncBuyFish : DoodadFuncTemplate
{
    // doodad_funcs
    public uint ItemId { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Trace("DoodadFuncBuyFish");

        if (caster is Character character)
        {
            var backpack = character.Inventory.GetEquippedBySlot(EquipmentItemSlot.Backpack);
            if (backpack == null)
            {
                character.SendErrorMessage(ErrorMessageType.StoreBackpackNogoods);
                return;
            }

            owner.ItemTemplateId = backpack.TemplateId; // to display the phase animation correctly for doodad

            // aaemu-custom: replace the native fish refund with the closed-loop economy payout
            // (50g base × labor multiplier). Best-effort — falls back to the native fish refund
            // when the sidecar is disabled or unreachable. The blocking call is safe: AAEmu runs
            // without a SynchronizationContext, the sidecar is local (replies in <10ms), and a
            // down sidecar fails the connection instantly rather than waiting on the 5s timeout.
            var sidecarGold = AaemuCustomClient.Instance
                .CalculateFishGoldAsync(character.AccountId).GetAwaiter().GetResult();

            if (sidecarGold >= 0)
            {
                // custom economy payout — award the sidecar amount once via the canonical path
                character.Equipment.RemoveItem(ItemTaskType.SkillEffectConsumption, backpack, true);
                character.AddMoney(SlotType.Inventory, (int)sidecarGold);
            }
            else
            {
                // native fallback (preserved verbatim)
                var total = backpack.Template.Refund;
                character.Money += total;

                character.Equipment.RemoveItem(ItemTaskType.SkillEffectConsumption, backpack, true);
                character.AddMoney(SlotType.Inventory, total);
            }
        }
    }
}
