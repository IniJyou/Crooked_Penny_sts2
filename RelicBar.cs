using System.Runtime.CompilerServices;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes.Relics;

namespace CrookedPenny;

internal sealed class RelicBar
{
    private static readonly ConditionalWeakTable<NRelicInventory, RelicBar> Bars = new();
    private readonly NRelicInventory _inventory;
    private readonly Button _toggle;
    private bool _overflow;
    private bool _refreshQueued;

    private RelicBar(NRelicInventory inventory)
    {
        _inventory = inventory;
        _toggle = new Button
        {
            Name = "CrookedPennyRelicToggle", Visible = false,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        _toggle.AddThemeFontSizeOverride("font_size", 20);
        inventory.AddChild(_toggle);
        _toggle.Pressed += () =>
        {
            CoinUiPreferences.RelicsCollapsed = !CoinUiPreferences.RelicsCollapsed;
            Refresh();
        };
        inventory.RelicsChanged += QueueRefresh;
        inventory.Resized += QueueRefresh;
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        Callable.From(Refresh).CallDeferred();
    }

    private void Refresh()
    {
        _refreshQueued = false;
        if (!GodotObject.IsInstanceValid(_inventory) || !GodotObject.IsInstanceValid(_toggle) || !_inventory.IsInsideTree())
            return;

        var relics = _inventory.RelicNodes.Where(relic => relic.GetParent() == _inventory).ToArray();
        var gap = _inventory.GetThemeConstant("h_separation", "FlowContainer");
        _overflow = CountInFirstRow(relics, _inventory.Size.X, gap) < relics.Length;
        var collapsed = CoinUiPreferences.RelicsCollapsed;
        var action = collapsed ? CoinUi.Text("展开遗物", "Expand relics") : CoinUi.Text("收起遗物", "Collapse relics");
        _toggle.Text = action;
        _toggle.AccessibilityName = action;
        if (_overflow)
        {
            _toggle.CustomMinimumSize = new Vector2(114, relics[0].GetCombinedMinimumSize().Y);
            if (_toggle.GetIndex() != 0)
                _inventory.MoveChild(_toggle, 0);
        }
        else if (_toggle.HasFocus() && relics.Length > 0)
            relics[0].GrabFocus();
        _toggle.Visible = _overflow;

        var firstRow = collapsed && _overflow
            ? CountInFirstRow(relics, _inventory.Size.X - _toggle.GetCombinedMinimumSize().X - gap, gap)
            : relics.Length;
        for (var i = 0; i < relics.Length; i++)
        {
            var show = i < firstRow;
            if (!show && relics[i].HasFocus() && _toggle.IsVisibleInTree())
                _toggle.GrabFocus();
            relics[i].Visible = show;
        }
        _inventory.UpdateNavigation();
    }

    private static int CountInFirstRow(IEnumerable<NRelicInventoryHolder> relics, float available, int gap)
    {
        var count = 0;
        foreach (var relic in relics)
        {
            available -= relic.GetCombinedMinimumSize().X + (count > 0 ? gap : 0);
            if (count > 0 && available < 0)
                break;
            count++;
        }
        return count;
    }

    private void LinkFocus()
    {
        if (!_overflow || !GodotObject.IsInstanceValid(_toggle) || !_toggle.IsInsideTree())
            return;
        var shown = _inventory.RelicNodes.Where(relic => relic.Visible && relic.IsInsideTree()).ToArray();
        if (shown.Length == 0)
            return;
        var path = _toggle.GetPath();
        shown[0].FocusNeighborLeft = path;
        shown[^1].FocusNeighborRight = path;
        _toggle.FocusNeighborLeft = shown[^1].GetPath();
        _toggle.FocusNeighborRight = shown[0].GetPath();
        _toggle.FocusNeighborTop = shown[^1].FocusNeighborTop;
        _toggle.FocusNeighborBottom = shown[^1].FocusNeighborBottom;
    }

    [HarmonyPatch(typeof(NRelicInventory), nameof(NRelicInventory.Initialize))]
    private static class AttachToggle
    {
        private static void Postfix(NRelicInventory __instance) => Bars.GetValue(__instance, static inventory => new RelicBar(inventory));
    }

    [HarmonyPatch(typeof(NRelicInventory), nameof(NRelicInventory.UpdateNavigation))]
    private static class MaintainFocus
    {
        private static void Postfix(NRelicInventory __instance)
        {
            if (Bars.TryGetValue(__instance, out var bar))
                bar.LinkFocus();
        }
    }
}
