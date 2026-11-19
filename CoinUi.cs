using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace CrookedPenny;

internal static class CoinUi
{
    private static bool IsChinese => LocManager.Instance.Language is "zhs" or "zht";
    internal static string Text(string chinese, string english) => IsChinese ? chinese : english;

    internal static Button CreateButton(string label)
    {
        var button = new Button { Text = label, CustomMinimumSize = new Vector2(0, 44) };
        button.AddThemeFontSizeOverride("font_size", 23);
        return button;
    }

    internal static Button FlipButton(CoinScene scene)
    {
        var button = CreateButton(Text("使用弯币", "Use Crooked Penny"));
        button.Icon = GD.Load<Texture2D>("res://CrookedPenny/coin.res");
        button.ExpandIcon = true;
        button.TextureFilter = CanvasItem.TextureFilterEnum.Nearest;
        void RefreshTip() => button.TooltipText = GetTip(scene);
        button.MouseEntered += RefreshTip;
        button.FocusEntered += RefreshTip;
        RefreshTip();
        return button;
    }

    private static string GetTip(CoinScene scene)
    {
        if (CoinRulesSync.ForDisplay is not { } rules)
            return Text("成功概率和倍率由房主设定。", "The host sets the success chance and multiplier.");

        var multiplier = rules.Multiplier;
        var success = scene switch
        {
            CoinScene.Merchant => Text($"遗物和药水变为 ×{multiplier}，新增副本免费，另送一次免费删牌。",
                $"Relics and potions ×{multiplier}, with free copies and one free card removal."),
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
    }
}
