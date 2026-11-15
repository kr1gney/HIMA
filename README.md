# HIMA — Harmony Intelligent Mobility Alliance

HIMA 是 Spaceflight Simulator Windows 版的飞行辅助模组，提供统一菜单，包含自动升空与轨道调整、自动回收着陆、交会对接和行星导航。

当前源码版本：**1.4.6**。游戏版本要求：**1.6.00.16 或更高版本**。该项目仍在开发和测试，建议在独立存档中测试，并保留手动接管能力。

## 依赖

- Spaceflight Simulator Windows 版及其 Mod Loader。
- UITools  或兼容版本。
- ANAIS  或兼容版本；交会与行星导航读取 ANAIS 数据，其内部接口变化可能导致不兼容。

游戏及上述第三方依赖需由使用者自行安装，可去官方论坛搜寻并安装；本仓库不包含它们的文件。它们各自适用自己的许可和使用条款。

## 构建

在 Windows PowerShell 中，指定游戏目录和 C# 编译器 `csc.exe` 的路径：

```powershell
.\build.ps1 -GamePath '游戏安装目录\Spaceflight Simulator Game' -CompilerPath '编译器目录\csc.exe'
```

编译器需要支持项目使用的 C# 语法。脚本引用游戏的 `Spaceflight Simulator_Data\Managed` 和已安装的 `Mods\UITools\UITools.dll`，生成 `out\HIMA.dll`。请将该 DLL 放入游戏的 `Mods\HIMA` 目录并重启游戏。

## 功能和限制

- **Ascent / Orbit**：升空与轨道调整。
- **Recovery / Landing**：识别当前启用的引擎，自动回收和着陆；部分飞行器及地形仍需测试。
- **Rendezvous / Docking**：使用 ANAIS 规划辅助交会，并执行对接。
- **Planet Navigation**：在 ANAIS 指引下执行行星导航。

自动飞行涉及大气、地形、推重比、控制能力和游戏版本等因素，不能保证所有飞船及星球都能成功。
发现问题时注意模组版本、游戏版本、场景和日志；分享日志前请检查个人信息。

## 源码与第三方内容

`src/` 包含 HIMA 源码；游戏和第三方依赖不随项目分发。项目使用并参考了用户提供的材料与其他模组的思路，相关源码来源与许可需要在发布前核实。
