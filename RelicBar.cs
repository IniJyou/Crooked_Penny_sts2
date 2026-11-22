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
            Name = "CrookedPennyRelicToggle", Size = new Vector2(30, 28), Visible = false,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        inventory.GetParent().AddChild(_toggle);
        _toggle.Pressed += () =>
        {
            CoinUiPreferences.RelicsCollapsed = !CoinUiPreferences.RelicsCollapsed;
            Refresh();
        };
        inventory.RelicsChanged += QueueRefresh;
        inventory.Resized += QueueRefresh;
        inventory.ItemRectChanged += PlaceToggle;
        inventory.VisibilityChanged += PlaceToggle;
        inventory.TreeExiting += _toggle.QueueFree;
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
        if (!GodotObject.IsInstanceValid(_inventory) || !_inventory.IsInsideTree())
            return;

        var relics = _inventory.RelicNodes.Where(relic => relic.GetParent() == _inventory).ToArray();
        var firstRow = 0;
        var width = 0f;
        var gap = _inventory.GetThemeConstant("h_separation", "FlowContainer");
        foreach (var relic in relics)
        {
            var nextWidth = width + (firstRow > 0 ? gap : 0) + relic.GetCombinedMinimumSize().X;
            if (firstRow > 0 && nextWidth > _inventory.Size.X)
                break;
            width = nextWidth;
            firstRow++;
        }

        _overflow = firstRow < relics.Length;
        var collapsed = CoinUiPreferences.RelicsCollapsed;
        var action = collapsed ? CoinUi.Text("展开遗物", "Expand relics") : CoinUi.Text("收起遗物", "Collapse relics");
        _toggle.Text = collapsed ? "+" : "−";
        _toggle.AccessibilityName = action;
        _toggle.TooltipText = action;
        PlaceToggle();
        for (var i = 0; i < relics.Length; i++)
        {
            var show = !collapsed || i < firstRow;
            if (!show && relics[i].HasFocus() && _toggle.IsVisibleInTree())
                _toggle.GrabFocus();
            relics[i].Visible = show;
        }
        if (!_overflow && _toggle.HasFocus() && relics.Length > 0)
            relics[0].GrabFocus();
        _inventory.UpdateNavigation();
    }

    private void PlaceToggle()
    {
        if (!GodotObject.IsInstanceValid(_toggle) || !_inventory.IsInsideTree())
            return;

        var viewport = _inventory.GetViewportRect();
        var toLocal = _inventory.GetGlobalTransformWithCanvas().AffineInverse();
        var near = toLocal * viewport.Position;
        var far = toLocal * viewport.End;
        var availableRight = far.X - _inventory.Size.X;
        var offset = availableRight >= _toggle.Size.X + 4 ? new Vector2(_inventory.Size.X + 4, 0) :
            near.X <= -_toggle.Size.X - 4 ? new Vector2(-_toggle.Size.X - 4, 0) : new Vector2(0, -_toggle.Size.Y - 4);
        _toggle.GlobalPosition = _inventory.GetGlobalTransform() * offset;
        _toggle.Visible = _overflow && _inventory.IsVisibleInTree() && offset.Y >= near.Y && offset.Y + _toggle.Size.Y <= far.Y;
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
