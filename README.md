# TreasureForecast - 挖宝预测

TreasureForecast 是一个面向 Dalamud (XIVLauncher) 的 FFXIV 挖宝预测插件，移植并复用了来自 FFCafe/Matcha (抹茶 ACT 插件) 的挖宝包解析与预测逻辑。

本项目通过 Hook 游戏网络层实时识别并汇报以下事件：

- 宝物库转盘召唤结果（G10 / G12 / G15）
- 宝物库开门/路结果（成功 / 失败）
- 巡梦金库（Hypnoslot）老虎机结果
- 在主窗口显示带时间戳的历史记录，可选择在聊天框输出简短提示
- 成就进度追踪：Hook `ReceiveAchievementProgress` 展示 10 个宝藏副本对应成就的完成进度，支持自选追踪
- 副本完成检测：低延迟识别宝藏副本完成事件，输出下底成功提示并写入历史记录

## 主要特性

- 实时预测：Hook `HandleActorControlPacket` 和 `PacketDispatcher.OnReceivePacket` 双通道捕获网络数据包
- 成就进度追踪：通过 FFXIVClientStructs Hook 捕获 `ReceiveAchievementProgress`，支持 4 色进度条
- 副本完成检测：使用 `IDutyState.DutyCompleted` 事件，覆盖 G8～G18 共 10 个宝藏副本
- 不依赖可变 opcode：通过固定字节特征（level 值 / 标志位）识别事件
- 自动去重：2 秒内同一结果只触发一次，避免游戏回调重复导致重复输出
- 可配置：开/关不同类型预测、是否在聊天框显示、Toast 提示开关、成就追踪、Debug 日志等
- Debug 模式：开启后输出诊断日志（hex dump + 识别偏移量），便于排查

## 命令

- `/tforecast` : 切换主窗口（无参数）
- `/tforecast config` 或 `/tforecast cfg` : 打开设置窗口

## 配置项（在插件设置中）

### 预测开关
- 转盘结果预测 (G10/G12/G15) — 默认开启
- 开门/路结果预测 — 默认开启
- 巡梦金库老虎机预测 — 默认开启

### 输出设置
- 在聊天框显示结果 — 默认开启
- Toast2 显示结果 — 默认开启，控制游戏屏幕中央的 GimmickHint 提示
- 副本完成时提示下底成功 — 默认开启

### 成就追踪
- 启用自选成就进度追踪 — 默认关闭，开启后仅显示勾选的成就
- 10 个单项成就勾选框（G8～G18 对应成就）

### 调试
- Debug 日志输出 — 默认关闭，开启后输出网络包 hex dump 和匹配信息到 Dalamud 日志

## 识别规则

### 转盘结果（Treasure Shifting Wheel）

通过 `OnReceivePacket` Hook 拦截 IPC 数据包，在多个候选偏移量（0x00 / 0x10 / 0x20）上尝试匹配：

- 读取 offset+24 的 32-bit 值判断来源：
  - 7636061 → G10 运河宝物库神殿
  - 8508181 → G12 梦羽宝殿
  - 9413549 → G15 育体宝殿
- offset+40 的字节映射为结果类型：wheel-low / medium / high / shift / special / end

### 开门/路结果（Treasure Gate）

- offset+16 的 32-bit 标志为 `0x04482c03` 表明是此事件
- offset+32 表示轮次（`data[32] + 1`），offset+40 为 1 则视为 gate-open，否则 gate-fail

### 巡梦金库老虎机（Hypnoslot）

- 通过 `HandleActorControlPacket` Hook 拦截
- category = 407 且 TerritoryType = 1279（限定地图）
- 根据 arg1 映射 HypnoslotResultType（AllDiff / AllSame / Reroll → wheel-open，End → wheel-end）

## 日志与调试

- 插件在加载和配置变更时会记录信息到 Dalamud 日志
- 开启设置中的 **"Debug 日志输出"** 后：
  - 每 200 个 IPC 数据包输出一次前 48 字节的 hex dump
  - 每 50 个 ActorControl 包输出一次类别摘要
  - 每次匹配成功输出包含 `bodyOff=0x??` 的日志，便于验证偏移量

## 输出样例

- 主窗口历史（带时间戳）：
  ```
  [20:58:12] [G15 育体宝殿] 下级召唤
  [20:58:10] [宝物库] 开门 (第1轮)
  [20:57:55] [巡梦金库] 成功
  [20:50:01] ❀❀下底成功❀❀
  ──────────────────
  ```

- 聊天框输出（若启用）：
  ```
  [巡梦金库] 成功
  [宝物库] 上级召唤
  ❀❀下底成功❀❀
  ```

- 成就进度标签页展示（每个成就带 4 色进度条及称号信息）：
  ```
  G18 巡梦金库
  [████████████░░░░░░░] 15 / 20 (75%)  巡梦者
  ```

- 主窗口历史中按类型有颜色区分：
  - 蓝色 — 下级召唤 (wheel-low)
  - 绿色 — 中级召唤 (wheel-medium)
  - 红色 — 上级召唤 / 开门失败 (wheel-high / gate-fail)
  - 金色 — 召唤式变动 / 下底成功 (wheel-shift / dungeon-complete)
  - 银色 — 特殊召唤 (wheel-special)
  - 紫色 — 召唤失败 (wheel-end)
  - 亮绿 — 开门成功 (gate-open / wheel-open)

## 项目结构

```
TreasureForecast/
├── Data/
│   └── Constants.cs              —— 成就 ID / 宝藏领土 ID 等常量
├── Models/
│   ├── AchievementProgressInfo.cs —— 成就进度数据模型
│   ├── TreasureEnums.cs          —— ShiftingWheelResultType / HypnoslotResultType
│   └── TreasureResultDTO.cs      —— 挖宝结果 DTO（含时间戳）
├── Utils/
│   └── ResultFormatter.cs        —— 结果格式化（中文描述映射）
├── Windows/
│   ├── MainWindow.cs             —— 主窗口（历史记录 + 成就进度 + 导出）
│   └── ConfigWindow.cs           —— 设置窗口
├── AchievementTracker.cs         —— 成就进度 Hook（ReceiveAchievementProgress）
├── NetworkReceiver.cs            —— 底层网络 Hook 管理（双通道捕获 + 偏移量试探）
├── TreasurePredictionService.cs  —— 预测逻辑核心（结果触发 + 去重）
├── Plugin.cs                     —— Dalamud 插件生命周期与命令
└── Configuration.cs              —— 插件配置
```

## 构建与安装

前提：.NET 10+ SDK，XIVLauncher / Dalamud 已安装（用于运行插件）

构建：

```powershell
dotnet build TreasureForecast\\TreasureForecast.csproj -c Debug
```

安装（开发者模式）：

1. 构建后获取 `TreasureForecast.dll`（位于 `bin\x64\Debug\`）
2. 在 Dalamud 设置 → Experimental → Dev Plugin Locations 中添加 DLL 所在文件夹路径
3. 在 Dalamud 插件管理器的 Dev Tools 中启用已加载的开发插件

## 授权与致谢

本项目的预测与解析逻辑移植自 [FFCafe/Matcha](https://github.com/thewakingsands/matcha)（抹茶 ACT 插件），在实现上参考了其 NetworkMonitor / TreasureHandler / Formatter 等组件。

授权：AGPL-3.0-or-later

