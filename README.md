# TreasureForecast - 挖宝预测

TreasureForecast 是一个面向 Dalamud (XIVLauncher) 的 FFXIV 挖宝预测插件，移植并复用了来自 FFCafe/Matcha (抹茶 ACT 插件) 的挖宝包解析与预测逻辑。

本项目通过监听并解析客户端的网络数据（ActorControl / ActorControlSelf 等包），在不依赖可变 opcode 的前提下，根据固定字节特征实时识别并汇报以下事件：

- 宝物库转盘召唤结果（G10 / G12 / G15）
- 宝物库开门/路结果（成功 / 失败）
- 巡梦金库（Hypnoslot）老虎机结果
- 在插件主窗口显示历史记录，并可选择在聊天框输出简短提示

## 主要特性

- 实时预测：解析网络包并即时触发预测事件
- 可配置：开/关不同类型预测、是否在聊天框显示、历史记录数量等（见插件设置）
- Dev-friendly：支持以开发插件方式加载 DLL 进行调试

## 命令

- `/tforecast` : 切换主窗口（无参数）
- `/tforecast config` 或 `/tforecast cfg` : 打开设置窗口

## 配置项（在插件设置中）

- EnableWheelPrediction：启用转盘结果预测（G10/G12/G15，默认开启）
- EnableGatePrediction：启用开门/路结果预测（默认开启）
- EnableHypnoslot：启用巡梦金库老虎机预测（默认开启）
- ShowInChat：是否在聊天框显示预测（默认开启）
- ShowHistory：主窗口是否显示历史记录（默认开启）
- MaxHistoryCount：历史记录最大条数（默认 50）
- IsConfigWindowMovable：设置窗口是否可移动

## 识别规则（实现细节）

说明插件如何从网络数据包中识别事件（源自代码实现）：

- 转盘结果（Treasure Shifting Wheel）
  - 处理时以 56 字节包为候选，读取 offset 24 的 32-bit 值判断来源：
	- 7636061 => G10 运河宝物库神殿
	- 8508181 => G12 梦羽宝殿
	- 9413549 => G15 育体宝殿
  - offset 40 的字节映射为转盘结果类型（wheel-low/medium/high/shift/special/end）

- 开门/路结果（Treasure Gate）
  - 72 字节包，offset 16 的 32-bit 标志为 0x04482c03 表明是此事件
  - offset 32 表示轮次（代码中为 data[32] + 1），offset 40 为 1 则视为成功

- 巡梦金库老虎机（Hypnoslot）
  - ActorControl 类型 category = 407 且 TerritoryType = 1279（限定地图）
  - 根据 arg1 映射 HypnoslotResultType（AllDiff / AllSame / Reroll / End）并生成 wheel-open / wheel-end

插件在实现上会尝试通过反射解析 FFXIVClientStructs 提供的 HandleActorControlPacket 地址并对其进行 Hook（API 15+ 的 Hook 方式）。该解析兼容多种 Address 表示（nint/IntPtr/带 Value 属性或字段的包装类型）。

## 日志与调试

- 插件在加载和配置变更时会记录信息到 Dalamud 日志
- 在 ActorControl 的处理里，插件会每 50 条包记录一次摘要（便于诊断）

## 输出样例

- 聊天框输出（若启用）：
  [巡梦金库] 成功
  [宝物库] 上级召唤

- 主窗口历史中按类型有颜色区分（例如：下级/中级/上级/失败/开门成功等）

## 构建与安装

前提：.NET 10+ SDK，XIVLauncher / Dalamud 已安装（用于运行插件）

构建：

```powershell
# 在仓库根目录运行：
dotnet build TreasureForecast\\TreasureForecast.csproj -c Debug
```

安装（开发者模式）：

1. 在构建后获取 `TreasureForecast.dll`（位于 bin\\Debug\\ 或 bin\\x64\\Debug\\）
2. 在 Dalamud 设置 → Experimental → Dev Plugin Locations 中添加 DLL 所在的文件夹路径
3. 在 Dalamud 插件管理器的 Dev Tools 中启用已加载的开发插件

## 项目结构（简要）

- TreasureForecast/           —— 插件源码
  - Models/                   —— DTO 与枚举（ShiftingWheel/Hypnoslot 等）
  - Utils/                    —— 结果格式化工具
  - Windows/                  —— ImGui 窗口 (主窗口 + 设置窗口)
  - TreasurePredictionService.cs —— 预测与包解析核心
  - Plugin.cs                 —— Dalamud 插件生命周期、Hook、命令与事件连接

## 授权与致谢

本项目的预测与解析逻辑移植自 FFCafe/Matcha（抹茶 ACT 插件），在实现上参考了其 NetworkMonitor/Formatter 等组件。

授权：AGPL-3.0-or-later

