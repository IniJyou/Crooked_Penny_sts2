# 被掰弯的硬币 · Crooked Penny

《杀戮尖塔 2》玩法 Mod。战后、事件奖励、商店、宝箱和先古之民处可以使用弯币。掷中后复制可用奖励，掷空后留下 1 金币。每一批奖励或每次到访商店有一次使用机会。

- **设置**在游戏的 Mod 设置中。成功概率默认 50%，可调至 0%–100%。成功倍率默认 ×2，可调至 ×1–×10。
- **商店**掷中后可免费领取在售卡牌、遗物和药水的副本。原货照常标价，×10 时额外获得 9 次免费删牌，取消选牌不扣次数。
- **弯币**左键点击图标使用，按住右键拖动。每种界面各自记住本机的摆放位置。商店的免费奖励栏可通过右键拖动标题移动，领取后保留滚动位置。
- **遗物栏**遗物换行后，第一行开头会出现「收起遗物」按钮。收起后可从同一位置展开。
- **联机**使用房主设置。各玩家的商店和个人奖励独立结算，商店副本只进入领取者的牌组或物品栏。共享宝箱按当前待领遗物叠乘，并分轮选择。

## 安装

已验证的游戏版本为 v0.113.1，依赖 [BaseLib v3.4.8 或以上](https://github.com/Alchyr/BaseLib-StS2/releases)。通过 Steam 创意工坊订阅时，先安装 [BaseLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3737335127)。手动安装时，将解压后的 `CrookedPenny` 文件夹放入游戏的 `mods` 目录，启动后在游戏设置中启用 Mod。

联机玩家需使用相同版本的 Crooked Penny 和兼容的 BaseLib。当前构建属于预览版，包含后续多步流程的事件读档和 Steam 断线重连仍需验证。

## 从源码构建

Windows 构建需要 .NET 9 SDK、Godot 4.5.1 的控制台程序、游戏本地安装和 BaseLib。`-BaseLibPath` 指向含有 `BaseLib.dll` 的目录。运行后在 `artifacts/CrookedPenny` 获得 `.dll`、`.pck` 和 `.json`。

```powershell
.\build.ps1 -Godot 'C:\Tools\Godot_v4.5.1-stable_win64_console.exe' `
  -Sts2Path 'C:\SteamLibrary\steamapps\common\Slay the Spire 2' `
  -BaseLibPath 'C:\SteamLibrary\steamapps\workshop\content\2868840\3737335127\BaseLib'
```

构建默认使用随源码提供的原创硬币插画。用 `-CoinImage` 可指定本地 PNG 或 SVG。请在公开分发前确认自定义素材的使用授权。

玩法灵感来自《以撒的结合》的 Crooked Penny。源码及随源码提供的原创素材使用 [MIT 许可](LICENSE)。
