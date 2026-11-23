using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
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

    private sealed record ShopView(NMerchantInventory Screen, Control Root, Button Flip, Button Gold, PanelContainer Rewards,
        Button Toggle, Button Removal, ScrollContainer Scroll, VBoxContainer List, Label Notice);

    private readonly List<Gift> _gifts = [];
    private readonly List<Button> _giftButtons = [];
    private CoinKey Key => new(CoinScene.Merchant, inventory.Player.NetId);
    private CoinResult _result;
    private int _multiplier;
    private int _freeRemovalsRemaining;
    private bool _goldTaken;
    private bool _busy;
    private bool _waiting;
    private bool _showGifts;
    private ShopView? _view;
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
            CoinAction.RemoveCard => _result == CoinResult.Won && _freeRemovalsRemaining > 0 &&
                inventory.Player.Deck.Cards.Any(card => card.IsRemovable),
            _ => false
        };
    }

    public void Apply(CoinCommand command)
    {
        _waiting = false;
        if (command.Action == CoinAction.Flip)
        {
            _result = command.Won ? CoinResult.Won : CoinResult.Lost;
            _multiplier = command.Multiplier;
            if (command.Won)
            {
                var copies = command.Multiplier - 1;
                _freeRemovalsRemaining = copies;
                if (copies > 0)
                {
                    _gifts.AddRange(inventory.RelicEntries.Where(entry => entry.IsStocked)
                        .Select(entry => new Gift(entry.Model!.ClonePreservingMutability(), copies)));
                    _gifts.AddRange(inventory.PotionEntries.Where(entry => entry.IsStocked)
                        .Select(entry => new Gift(entry.Model!.ClonePreservingMutability(), copies)));
                    _gifts.AddRange(inventory.CardEntries.Where(entry => entry.IsStocked)
                        .Select(entry => new Gift(entry.CreationResult!.Card.ClonePreservingMutability(), copies)));
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
                    _freeRemovalsRemaining--;
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
        switch (gift.Model)
        {
            case CardModel card:
                var copy = player.RunState.CloneCard(card);
                if ((await CardPileCmd.Add(copy, PileType.Deck)).success)
                    gift.Remaining--;
                else
                {
                    player.RunState.RemoveCard(copy);
                    _notice = CoinUi.Text("当前无法领取这张卡牌。", "You cannot take this card right now.");
                }
                break;
            case RelicModel relic:
                await RelicCmd.Obtain((RelicModel)relic.ClonePreservingMutability(), player);
                gift.Remaining--;
                break;
            case PotionModel potion when (await PotionCmd.TryToProcure((PotionModel)potion.ClonePreservingMutability(), player)).success:
                gift.Remaining--;
                break;
            case PotionModel:
                _notice = CoinUi.Text("当前无法领取这瓶药水。", "You cannot take this potion right now.");
                break;
        }
    }

    private void Attach(NMerchantInventory screen)
    {
        if (_view is { } existing && GodotObject.IsInstanceValid(existing.Root))
            return;

        // The shop controls sit in front of the relic rows but hide whenever the shop loses screen focus.
        var layer = new CanvasLayer { Name = "CrookedPennyShopLayer", Layer = 1 };
        var root = new Control { Name = "CrookedPennyShop", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        screen.AddChild(layer);
        layer.AddChild(root);
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var controls = new VBoxContainer
        {
            AnchorLeft = 0.72f, AnchorRight = 0.90f, OffsetTop = 164, OffsetBottom = 164,
            GrowHorizontal = Control.GrowDirection.Begin, GrowVertical = Control.GrowDirection.End,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        root.AddChild(controls);
        var flip = CoinUi.AddFlipButton(controls, CoinScene.Merchant);
        flip.Pressed += () => Request(CoinAction.Flip);
        var gold = AddAction(controls, CoinUi.Text("领取 1 金币", "Claim 1 gold"), CoinAction.Claim);

        var rewards = new PanelContainer
        {
            Name = "CrookedPennyFreeRewards",
            AnchorLeft = 0.74f, AnchorRight = 0.94f, AnchorTop = 0.42f, AnchorBottom = 0.42f,
            GrowVertical = Control.GrowDirection.End
        };
        CoinUi.StylePanel(rewards);
        root.AddChild(rewards);
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        rewards.AddChild(rows);
        var toggle = CoinUi.CreateButton("", featured: true);
        toggle.Name = "CrookedPennyFreeRewardsHandle";
        toggle.TooltipText = CoinUi.Text("按住右键拖动免费奖励栏", "Right-drag to move the free rewards panel");
        toggle.Pressed += () => { _showGifts = !_showGifts; Render(); };
        rows.AddChild(toggle);
        var removal = AddAction(rows, "", CoinAction.RemoveCard);
        var scroll = new ScrollContainer
        {
            Name = "CrookedPennyFreeRewardsScroll",
            CustomMinimumSize = new Vector2(0, 300),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            FollowFocus = true
        };
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 5);
        scroll.AddChild(list);
        rows.AddChild(scroll);
        var notice = CoinUi.CreateLabel("", accent: true);
        notice.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(notice);
        _giftButtons.Clear();
        _view = new ShopView(screen, root, flip, gold, rewards, toggle, removal, scroll, list, notice);
        CoinUi.EnableRightDrag(root, rewards, toggle, "MerchantRewards");

        var context = ActiveScreenContext.Instance;
        void RefreshVisibility()
        {
            if (GodotObject.IsInstanceValid(root))
                root.Visible = screen.IsOpen && screen.IsVisibleInTree() && context.IsCurrent(screen);
        }
        context.Updated += RefreshVisibility;
        screen.VisibilityChanged += RefreshVisibility;
        screen.TreeExiting += () => context.Updated -= RefreshVisibility;
        RefreshVisibility();
        Render();
    }

    private void Render()
    {
        if (_view is not { } view || !GodotObject.IsInstanceValid(view.Root))
            return;

        view.Flip.Disabled = _result != CoinResult.Pending || _busy || _waiting;
        if (_result != CoinResult.Pending)
            CoinUi.ShowResult(view.Flip, _result, _multiplier);
        view.Gold.Visible = _result == CoinResult.Lost && !_goldTaken;
        view.Gold.Disabled = _busy || _waiting;
        if (_result == CoinResult.Lost)
        {
            foreach (var node in new[] { "%CharacterCards", "%ColorlessCards", "%Relics", "%Potions", "%BlitzSlots", "%BlitzSelectionText", "%MerchantCardRemoval" })
                view.Screen.GetNodeOrNull<Control>(node)?.Hide();
        }

        var availableItems = _gifts.Sum(gift => gift.Remaining);
        var total = availableItems + _freeRemovalsRemaining;
        view.Rewards.Visible = _result == CoinResult.Won && total > 0;
        view.Toggle.Text = _showGifts ? CoinUi.Text("收起免费奖励", "Hide free rewards") :
            CoinUi.Text($"查看免费奖励 ({total})", $"Free rewards ({total})");
        view.Removal.Visible = _showGifts && _freeRemovalsRemaining > 0;
        view.Removal.Disabled = _busy || _waiting || !inventory.Player.Deck.Cards.Any(card => card.IsRemovable);
        view.Removal.Text = CoinUi.Text($"免费删牌 ({_freeRemovalsRemaining})", $"Remove a card for free ({_freeRemovalsRemaining})");
        view.Scroll.Visible = _showGifts && availableItems > 0;
        view.Notice.Text = _notice ?? "";
        view.Notice.Visible = _showGifts && _notice != null;

        // Keep the ScrollContainer and claim buttons alive so a claim does not reset its scrollbar or focus.
        for (var i = 0; i < _gifts.Count; i++)
        {
            var gift = _gifts[i];
            if (i == _giftButtons.Count)
            {
                var button = AddAction(view.List, "", CoinAction.Claim, i);
                button.Alignment = HorizontalAlignment.Left;
                button.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                _giftButtons.Add(button);
            }

            var title = gift.Model switch
            {
                CardModel card => card.Title,
                RelicModel relic => relic.Title.GetFormattedText(),
                PotionModel potion => potion.Title.GetFormattedText(),
                _ => ""
            };
            var quantity = gift.Remaining > 1 ? $" ×{gift.Remaining}" : "";
            var label = CoinUi.Text($"免费领取 · {title}{quantity}", $"Free · {title}{quantity}");
            var claim = _giftButtons[i];
            claim.Text = label;
            claim.TooltipText = label;
            claim.Visible = gift.Remaining > 0;
            claim.Disabled = _busy || _waiting;
        }
    }

    private Button AddAction(Control parent, string label, CoinAction action, int option = 0)
    {
        var button = CoinUi.CreateButton(label);
        button.Disabled = _busy || _waiting;
        button.Pressed += () => Request(action, option);
        parent.AddChild(button);
        return button;
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
