using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Services.AaemuCustom;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class GiveHonorPoint : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.GiveHonorPoint;

    public override void Execute(BaseUnit caster, SkillCaster casterObj, BaseUnit target, SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill, SkillObject skillObject, DateTime time, int amount, int value2, int value3, int value4)
    {
        if (caster is Character) { Logger.Debug("Special effects: GiveHonorPoint amount {0}, value2 {1}, value3 {2}, value4 {3}", amount, value2, value3, value4); }

        if (caster is not Character character)
            return;

        // aaemu-custom: event honor is scaled by the sidecar's honor multiplier
        // (default x10) and recorded in the sidecar's account_honor ledger.
        // Best-effort — falls back to the native HonorRate scaling when the
        // sidecar is down or disabled (-1). Blocking call is safe (no
        // SynchronizationContext; local sidecar <10ms; down fails fast).
        var custom = AaemuCustomClient.Instance
            .GrantEventHonorAsync(character.AccountId, amount).GetAwaiter().GetResult();
        var points = custom >= 0
            ? (int)custom
            : (int)Math.Round(AppConfiguration.Instance.World.HonorRate * amount);
        character.ChangeGamePoints(GamePointKind.Honor, points);
    }
}