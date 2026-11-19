using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace CrookedPenny;

internal sealed class MerchantScene(MerchantInventory inventory) : ICoinScene
{
    private static readonly ConditionalWeakTable<MerchantInventory, MerchantScene> Inventories = new();
    private static readonly AccessTools.FieldRef<MerchantInventory, bool> PurchaseInFlight =
        AccessTools.FieldRefAccess<MerchantInventory, bool>("_purchaseInFlight");

    private sealed class Gift(AbstractModel model, int remaining)
    {
        internal AbstractModel Model { get; } = model;
        internal int Remaining { get; set; } = remaining;
    }

    private readonly List<Gift> _gifts = [];
    private CoinKey Key => new(CoinScene.Merchant, inventory.Player.NetId);
    private CoinResult _result;
    private bool _freeRemovalUsed;
    private bool _goldTaken;
    private bool _busy;
    private bool _waiting;
    private bool _showGifts;
    private VBoxContainer? _panel;
    private string? _notice;
    public bool IsBusy => _busy;

    public void Rejected()
    {
        _waiting = false;
        Render();
    }

    public void BeforeFlip()
    {
        var manager = RunManager.Instance;
        if (!manager.ShouldSave || inventory.IsBlitzShop || inventory.Player.RunState.CurrentRoom is not MerchantRoom)
            return;

        var snapshot = manager.ToSave(null);
        snapshot.PreFinishedRoom = new SerializableRoom { RoomType = RoomType.Shop };
        var entries = inventory.Player.RunState.Players.ToDictionary(player => player.NetId,
            player => MerchantCheckpoint.Capture(manager.MerchantSynchronizer.GetInventoryFor(player)));
        CoinCheckpoint.Save(CoinScene.Merchant, snapshot, entries, inventory.Player.NetId);
    }

    public bool CanApply(CoinCommand command)
    {
        if (command.Player != inventory.Player.NetId || _busy || PurchaseInFlight(inventory))
            return false;

        return command.Action switch
        {
            CoinAction.Flip => _result == CoinResult.Pending,
            CoinAction.Claim when _result == CoinResult.Lost => !_goldTaken,
            CoinAction.Claim when _result == CoinResult.Won =>
                command.Option >= 0 && command.Option < _gifts.Count && _gifts[command.Option].Remaining > 0,
            CoinAction.RemoveCard => _result == CoinResult.Won && !_freeRemovalUsed,
            _ => false
        };
    }

    public void Apply(CoinCommand command)
    {
        _waiting = false;
        if (command.Action == CoinAction.Flip)
        {
            _result = command.Won ? CoinResult.Won : CoinResult.Lost;
            if (command.Won)
            {
                var copies = command.Multiplier - 1;
                if (copies > 0)
                {
                    _gifts.AddRange(inventory.RelicEntries.Where(entry => entry.IsStocked)
                        .Select(entry => new Gift(entry.Model!.ClonePreservingMutability(), copies)));
                    _gifts.AddRange(inventory.PotionEntries.Where(entry => entry.IsStocked)
                        .Select(entry => new Gift(entry.Model!.ClonePreservingMutability(), copies)));
                }
            }
            Render();
            return;
        }

        _busy = true;
        _notice = null;
        Render();
        TaskHelper.RunSafely(CompleteAction(command));
    }

    private async Task CompleteAction(CoinCommand command)
    {
        try
        {
            var player = inventory.Player;
            if (command.Action == CoinAction.RemoveCard)
            {
                var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1)
                {
                    Cancelable = true,
                    RequireManualConfirmation = true
                };
                var card = (await CardSelectCmd.FromDeckForRemoval(player, prefs)).FirstOrDefault();
                if (card != null)
                {
                    await CardPileCmd.RemoveFromDeck(card);
                    _freeRemovalUsed = true;
                }
            }
            else if (_result == CoinResult.Lost)
            {
                _goldTaken = true;
                await PlayerCmd.GainGold(1, player);
            }
            else
                await ClaimGift(_gifts[command.Option], player);
        }
        finally
        {
            _busy = false;
            Render();
            CoinNetwork.Drain(Key);
        }
    }

    private async Task ClaimGift(Gift gift, Player player)
    {
        switch (gift.Model.ClonePreservingMutability())
        {
            case RelicModel relic:
                await RelicCmd.Obtain(relic, player);
                gift.Remaining--;
                break;
            case PotionModel potion when (await PotionCmd.TryToProcure(potion, player)).success:
                gift.Remaining--;
                break;
            case PotionModel:
                _notice = CoinUi.Text("当前无法领取这瓶药水。", "You cannot take this potion right now.");
                break;
        }
    }

    private void Attach(NMerchantInventory screen)
    {
        if (_panel != null && GodotObject.IsInstanceValid(_panel))
            return;

        _panel = new VBoxContainer
        {
            Name = "CrookedPennyShop",
            AnchorLeft = 0.72f, AnchorRight = 0.90f, AnchorTop = 0.12f, AnchorBottom = 0.12f,
            GrowHorizontal = Control.GrowDirection.Begin, GrowVertical = Control.GrowDirection.End,
            Visible = screen.IsOpen
        };
        screen.AddChild(_panel);
        Render();
    }

    private void Render()
    {
        if (_panel is not { } panel || !GodotObject.IsInstanceValid(panel))
            return;

        foreach (var child in panel.GetChildren())
        {
            panel.RemoveChild(child);
            child.QueueFree();
        }

        var flip = CoinUi.FlipButton(CoinScene.Merchant);
        if (_result == CoinResult.Pending)
            flip.Pressed += () => Request(CoinAction.Flip);
        else
            CoinUi.ShowResult(flip, _result);
        flip.Disabled |= _busy || _waiting;
        panel.AddChild(flip);

        if (_result == CoinResult.Lost)
        {
            var screen = (NMerchantInventory)panel.GetParent();
            foreach (var node in new[] { "%CharacterCards", "%ColorlessCards", "%Relics", "%Potions", "%BlitzSlots", "%BlitzSelectionText", "%MerchantCardRemoval" })
                screen.GetNodeOrNull<Control>(node)?.Hide();
            if (!_goldTaken)
                AddAction(CoinUi.Text("领取 1 金币", "Claim 1 gold"), CoinAction.Claim);
        }
        else if (_result == CoinResult.Won)
        {
            var remaining = _gifts.Sum(gift => gift.Remaining) + (_freeRemovalUsed ? 0 : 1);
            if (remaining > 0)
            {
                var toggle = CoinUi.CreateButton(_showGifts ? CoinUi.Text("收起免费奖励", "Hide free rewards") :
                    CoinUi.Text($"查看免费奖励 ({remaining})", $"Free rewards ({remaining})"));
                toggle.Pressed += () => { _showGifts = !_showGifts; Render(); };
                panel.AddChild(toggle);
            }

            if (_showGifts)
                RenderGifts();
        }

        if (_notice != null)
            panel.AddChild(new Label { Text = _notice });
    }

    private void RenderGifts()
    {
        for (var i = 0; i < _gifts.Count; i++)
        {
            var gift = _gifts[i];
            if (gift.Remaining <= 0)
                continue;

            var title = gift.Model switch
            {
                RelicModel relic => relic.Title.GetFormattedText(),
                PotionModel potion => potion.Title.GetFormattedText(),
                _ => ""
            };
            var quantity = gift.Remaining > 1 ? $" ×{gift.Remaining}" : "";
            AddAction(CoinUi.Text($"免费领取 · {title}{quantity}", $"Free · {title}{quantity}"), CoinAction.Claim, i);
        }
        if (!_freeRemovalUsed)
            AddAction(CoinUi.Text("免费删牌", "Remove a card for free"), CoinAction.RemoveCard);
    }

    private void AddAction(string label, CoinAction action, int option = 0)
    {
        var button = CoinUi.CreateButton(label);
        button.Disabled = _busy || _waiting;
        button.Pressed += () => Request(action, option);
        _panel!.AddChild(button);
    }

    private void Request(CoinAction action, int option = 0)
    {
        if (_waiting || !CanApply(new CoinCommand(inventory.Player.NetId, action, option, 0)))
            return;

        _waiting = true;
        CoinNetwork.Request(Key, action, option);
        Render();
    }

    [HarmonyPatch(typeof(MerchantSynchronizer), nameof(MerchantSynchronizer.SetMerchantInventory))]
    private static class TrackInventory
    {
        private static void Postfix(MerchantInventory inventory)
        {
            var scene = Inventories.GetValue(inventory, static value => new MerchantScene(value));
            CoinNetwork.Register(scene.Key, scene);
        }
    }

    [HarmonyPatch(typeof(NMerchantInventory), nameof(NMerchantInventory.Initialize))]
    private static class AttachPanel
    {
        private static void Postfix(NMerchantInventory __instance, MerchantInventory inventory)
        {
            if (Inventories.TryGetValue(inventory, out var scene))
                scene.Attach(__instance);
        }
    }

    [HarmonyPatch]
    private static class SetPanelVisibility
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() =>
        [
            AccessTools.Method(typeof(NMerchantInventory), nameof(NMerchantInventory.Open)),
            AccessTools.Method(typeof(NMerchantInventory), nameof(NMerchantInventory.Close))
        ];

        private static void Postfix(NMerchantInventory __instance)
        {
            if (__instance.Inventory is { } inventory && Inventories.TryGetValue(inventory, out var scene))
                scene._panel?.SetVisible(__instance.IsOpen);
        }
    }

    [HarmonyPatch(typeof(MerchantInventory), nameof(MerchantInventory.TryPurchaseEntryWithIdUnsynchronized))]
    private static class GuardPurchase
    {
        private static bool Prefix(MerchantInventory __instance, ref Task<bool> __result)
        {
            if (!Inventories.TryGetValue(__instance, out var scene) ||
                scene._result != CoinResult.Lost && !scene._busy && !scene._waiting)
                return true;

            __result = Task.FromResult(false);
            return false;
        }
    }
}
