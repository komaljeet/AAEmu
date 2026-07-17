using System;
using System.Threading.Tasks;

using NLog;

namespace AAEmu.Game.Services.AaemuCustom;

/// <summary>
/// Logs player-to-player gold transfers (mail money) to the aaemu-custom sidecar
/// so the closed-loop ledger reflects gold moving between accounts, and asks the
/// sidecar to evaluate the recipient for RMT-suspect flagging (admin review only
/// — it never blocks a transfer).
/// </summary>
/// <remarks>
/// The sidecar tracks gold in whole-gold units; AAEmu's wallet is copper
/// (1g = 10000c), so amounts are converted <c>/10000</c> and sub-gold transfers
/// are skipped. Transfers don't create or destroy gold, so the sidecar's
/// <c>world_bank</c> tally (<c>circulating + pool == cap</c>) is untouched —
/// only the per-account <c>account_gold.balance</c> moves (sender -X, recipient
/// +X, net zero). The hourly integrity check only verifies the tally, so the
/// mail-escrow window (sender debited at send, recipient credited at claim) can
/// never false-trip it.
/// <para>
/// Both calls are fire-and-forget and best-effort: a down/disabled sidecar is
/// silently skipped and the mail still sends/claims normally. Only
/// <c>MailType.Normal</c> (player-to-player) transfers are logged on claim;
/// system mails (boss loot, auction proceeds) are gameplay rewards already
/// recorded by their own hooks.
/// </para>
/// </remarks>
public static class GoldTransfer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Copper per gold (AAEmu wallet is copper, sidecar is gold).</summary>
    private const long CopperPerGold = 10000;

    /// <summary>
    /// Log the outbound leg of a player-to-player gold transfer (mail send).
    /// Fire-and-forget; best-effort. Amounts under 1 gold are skipped. Never
    /// blocks the send path.
    /// </summary>
    /// <param name="senderAccountId">The sending account's id.</param>
    /// <param name="senderCharacterId">The sending character's id.</param>
    /// <param name="copper">The copper attached to the mail.</param>
    public static void LogSend(long senderAccountId, long senderCharacterId, int copper)
    {
        if (!AaemuCustomClient.Instance.Enabled || senderAccountId <= 0 || copper < CopperPerGold)
            return;
        var gold = copper / CopperPerGold;
        _ = FireAsync(senderAccountId, senderCharacterId, "transfer_out", gold, flagRmt: false);
    }

    /// <summary>
    /// Log the inbound leg of a player-to-player gold transfer (mail claim) and
    /// ask the sidecar to evaluate the recipient for RMT-suspect flagging. The
    /// flag is admin-review-only (<c>rmt_suspects</c> table) and never blocks the
    /// claim. Fire-and-forget; best-effort. Amounts under 1 gold are skipped.
    /// </summary>
    /// <param name="recipientAccountId">The claiming account's id.</param>
    /// <param name="recipientCharacterId">The claiming character's id.</param>
    /// <param name="copper">The copper claimed from the mail.</param>
    public static void LogClaim(long recipientAccountId, long recipientCharacterId, int copper)
    {
        if (!AaemuCustomClient.Instance.Enabled || recipientAccountId <= 0 || copper < CopperPerGold)
            return;
        var gold = copper / CopperPerGold;
        _ = FireAsync(recipientAccountId, recipientCharacterId, "transfer_in", gold, flagRmt: true);
    }

    private static async Task FireAsync(long accountId, long characterId, string txType, long gold, bool flagRmt)
    {
        try
        {
            await AaemuCustomClient.Instance
                .LogTransactionAsync(accountId, characterId, txType, gold)
                .ConfigureAwait(false);
            if (flagRmt)
                _ = AaemuCustomClient.Instance.FlagRmtSuspectAsync(accountId);
        }
        catch (Exception ex)
        {
            Logger.Warn($"gold transfer log ({txType}, acct {accountId}, {gold}g) failed: {ex.Message}");
        }
    }
}