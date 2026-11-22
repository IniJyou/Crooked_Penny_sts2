using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace CrookedPenny;

internal static class CoinUi
{
    private const string CoinIcon = "res://CrookedPenny/coin.res";
    private static bool IsChinese => LocManager.Instance.Language is "zhs" or "zht";
    internal static string Text(string chinese, string english) => IsChinese ? chinese : english;

    internal static Button CreateButton(string label)
    {
        var button = new Button { Text = label, CustomMinimumSize = new Vector2(0, 44) };
        button.AddThemeFontSizeOverride("font_size", 23);
        return button;
    }

    internal static Button AddFlipButton(Control parent, CoinScene scene)
    {
        var title = Text("使用弯币", "Use Crooked Penny");
        var button = CreateButton("");
        button.Name = "CrookedPennyFlip";
        button.AccessibilityName = title;
        button.CustomMinimumSize = new Vector2(120, 120);
        button.Icon = GD.Load<Texture2D>(CoinIcon);
        if (button.Icon == null)
            button.Text = title;
        button.ExpandIcon = true;
        button.IconAlignment = HorizontalAlignment.Center;
        button.AddThemeConstantOverride("icon_max_width", 96);
        button.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
        void RefreshTip()
        {
            if (button.Text == "" || button.Text == title)
                button.TooltipText = $"{title}\n{GetTip(scene)}\n{Text("按住右键拖动图标", "Right-drag to move the icon")}";
        }
        button.MouseEntered += RefreshTip;
        button.FocusEntered += RefreshTip;
        RefreshTip();

        var slot = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        slot.AddChild(button);
        parent.AddChild(slot);
        void FitSlot()
        {
            var minimum = button.GetCombinedMinimumSize();
            slot.CustomMinimumSize = minimum;
            button.Size = minimum;
        }
        EnableRightDrag(slot, button, button, scene.ToString(), FitSlot,
            () => button.Position = new Vector2(Mathf.Max(0, (slot.Size.X - button.Size.X) / 2), 0));
        return button;
    }

    internal static void EnableRightDrag(Control parent, Control movable, Control handle, string key,
        Action? fit = null, Action? placeDefault = null)
    {
        var viewport = parent.GetViewport();
        var saved = CoinUiPreferences.GetPosition(key);
        Vector2? dragOffset = null;
        var moved = false;

        (Vector2 Origin, Vector2 Travel) Bounds(Transform2D transform)
        {
            var view = viewport.GetVisibleRect();
            var size = (transform.BasisXform(movable.Size)).Abs();
            return (view.Position, (view.Size - size).Max(Vector2.Zero));
        }

        void Place()
        {
            if (!GodotObject.IsInstanceValid(parent) || !GodotObject.IsInstanceValid(movable) || !movable.IsInsideTree())
                return;
            fit?.Invoke();
            if (saved is { } position)
            {
                var transform = parent.GetGlobalTransformWithCanvas();
                var (origin, travel) = Bounds(transform);
                movable.Position = transform.AffineInverse() * (origin + travel * position);
            }
            else
                placeDefault?.Invoke();
        }

        void StopDragging()
        {
            if (moved && saved is { } position)
                CoinUiPreferences.SetPosition(key, position);
            dragOffset = null;
            moved = false;
        }

        handle.GuiInput += input =>
        {
            if (input is InputEventMouseButton { ButtonIndex: MouseButton.Right } mouse)
            {
                if (mouse.Pressed)
                {
                    moved = false;
                    dragOffset = handle.GetGlobalTransformWithCanvas() * mouse.Position -
                        movable.GetGlobalTransformWithCanvas().Origin;
                }
                else
                    StopDragging();
                handle.AcceptEvent();
            }
            else if (input is InputEventMouseMotion motion && dragOffset is { } offset)
            {
                if ((motion.ButtonMask & MouseButtonMask.Right) == 0)
                {
                    StopDragging();
                    return;
                }
                var transform = parent.GetGlobalTransformWithCanvas();
                var (origin, travel) = Bounds(transform);
                var topLeft = handle.GetGlobalTransformWithCanvas() * motion.Position - offset;
                var distance = (topLeft - origin).Clamp(Vector2.Zero, travel);
                saved = distance / travel.Max(Vector2.One);
                moved = true;
                Place();
                handle.AcceptEvent();
            }
        };
        movable.MinimumSizeChanged += Place;
        movable.Resized += Place;
        parent.ItemRectChanged += Place;
        viewport.SizeChanged += Place;
        movable.TreeExiting += () =>
        {
            if (GodotObject.IsInstanceValid(parent))
                parent.ItemRectChanged -= Place;
            if (GodotObject.IsInstanceValid(viewport))
                viewport.SizeChanged -= Place;
        };
        Place();
        Callable.From(Place).CallDeferred();
    }

    private static string GetTip(CoinScene scene)
    {
        if (CoinRulesSync.ForDisplay is not { } rules)
            return Text("成功概率和倍率由房主设定。", "The host sets the success chance and multiplier.");

        var multiplier = rules.Multiplier;
        var success = scene switch
        {
            CoinScene.Merchant => Text($"在售卡牌、遗物和药水变为 ×{multiplier}，副本免费，额外 {multiplier - 1} 次免费删牌。",
                $"Stocked cards, relics and potions ×{multiplier}, with free copies and {multiplier - 1} extra free card removals."),
            CoinScene.Ancient => Text($"从这组选项独立选择 {multiplier} 次。", $"Choose from the same options {multiplier} times."),
            CoinScene.Treasure => Text($"待领遗物数量 ×{multiplier}。", $"Multiply the remaining relics by {multiplier}."),
            _ => Text($"可复制的未领奖励 ×{multiplier}。", $"Multiply eligible unclaimed rewards by {multiplier}.")
        };
        var failure = scene == CoinScene.Merchant
            ? Text("清空商店并留下 1 金币。", "Empty the shop and leave 1 gold.")
            : Text("清空剩余奖励并发放 1 金币。", "Clear the remaining rewards and award 1 gold.");
        return Text($"成功 {rules.SuccessPercent}% · {success}\n失败 {100 - rules.SuccessPercent}% · {failure}",
            $"{rules.SuccessPercent}% success · {success}\n{100 - rules.SuccessPercent}% failure · {failure}");
    }

    internal static void ShowResult(Button button, CoinResult result)
    {
        button.Disabled = true;
        button.Text = result == CoinResult.Won ? Text("掷中", "Won the flip") : Text("掷空", "Lost the flip");
        button.AccessibilityName = button.Text;
        button.TooltipText = $"{button.Text}\n{Text("按住右键拖动图标", "Right-drag to move the icon")}";
    }
}

internal static class CoinUiPreferences
{
    private const string Path = "user://CrookedPennyUi.cfg";
    private static readonly ConfigFile Settings = new();

    static CoinUiPreferences() => Settings.Load(Path);

    internal static Vector2? GetPosition(string key)
    {
        if (!Settings.HasSectionKey("coin", key))
            return null;
        var position = Settings.GetValue("coin", key);
        return position.VariantType == Variant.Type.Vector2 ? position.AsVector2() : null;
    }

    internal static void SetPosition(string key, Vector2 position)
    {
        Settings.SetValue("coin", key, position);
        Settings.Save(Path);
    }

    internal static bool RelicsCollapsed
    {
        get => Settings.GetValue("relics", "collapsed", false).AsBool();
        set
        {
            Settings.SetValue("relics", "collapsed", value);
            Settings.Save(Path);
        }
    }
}
