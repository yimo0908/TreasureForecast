# TreasureForecast - 挖宝预测

TreasureForecast 是一个面向 Dalamud (XIVLauncher) 的 FFXIV 挖宝预测插件，移植并复用了来自 FFCafe/Matcha (抹茶 ACT 插件) 的挖宝包解析与预测逻辑。

本项目通过 Hook 游戏网络层实时识别并汇报以下事件：

- 宝物库转盘召唤结果（G10 / G12 / G15）
- 宝物库开门/路结果（成功 / 失败）
- 巡梦金库（Hypnoslot）老虎机结果
- 选门开门地图回退记录：当玩家未进动画导致无预测网络包时，通过 Hook logmessage 回退补记开门/失败记录
- 在主窗口显示带时间戳的历史记录，可选择在聊天框输出简短提示
- 成就进度追踪：Hook `ReceiveAchievementProgress` 展示 10 个宝藏副本对应成就的完成进度，支持自选追踪，并提供一键导出到剪贴板
- 副本完成/团灭检测：低延迟识别宝藏副本完成与团灭事件，均写入历史记录
- 历史自动分隔：进入宝藏副本时自动插入分隔线，便于区分每次下底历程

## 主要特性

- 实时预测：Hook `HandleActorControlPacket` 和 `PacketDispatcher.OnReceivePacket` 双通道捕获网络数据包
- 成就进度追踪：通过 FFXIVClientStructs Hook 捕获 `ReceiveAchievementProgress`，支持 4 色进度条
- 副本完成/团灭检测：使用 `IDutyState.DutyCompleted` / `IDutyState.DutyWiped` 事件，覆盖 G8～G18 共 10 个宝藏副本；完成与团灭均写入历史记录
- 选门地图回退：在选门开门地图（588/712/725/879/1000/1123）中，当玩家未进动画导致无预测网络包时，通过 Hook `RaptureLogModule.ShowLogMessageUInt` 拦截 "打开了通往第{n}区的大门！" logmessage（ID 6998/9365），直接从 `value` 参数获取轮数并补记开门记录；退出地图时若无失败网络包且无团灭/完成事件，则补记一条失败记录
- 历史去重：开门/关门结果在写入历史记录时对比上一条，相同 Value 和轮数则跳过，避免重复
- 不依赖可变 opcode：通过固定字节特征（level 值 / 标志位）识别事件
- 自动去重：5 秒内同一结果只触发一次，避免游戏回调重复导致重复输出
- 进度自动刷新：后台每 5 秒批量刷新成就进度，未初始化的成就每 0.5 秒快速轮询重试
- 可配置：开/关不同类型预测、是否在聊天框显示、Toast 提示开关、成就追踪、历史上限、Debug 日志等
- Debug 模式：开启后输出诊断日志（hex dump + 识别偏移量），便于排查
- 性能优化：历史条目在写入时预计算显示文本与颜色（Draw 零分配）、领地名称 O(1) 字典查找、去重字典定期清理过期条目、成就显示列表按需缓存避免每帧 ToList

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
- 历史记录最大条数（MaxHistoryCount）— 默认 50，超出上限时自动淘汰最旧条目

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
- offset+40 的字节映射为结果类型（ShiftingWheelResultType 枚举）：191=Low, 192=Medium, 193=High, 194=Shift, 195=Special, 196=End

### 开门/路结果（Treasure Gate）

- offset+16 的 32-bit 标志为 `0x04482c03` 表明是此事件
- offset+32 表示轮次（`data[32] + 1`），offset+40 为 1 则视为 gate-open，否则 gate-fail

### 巡梦金库老虎机（Hypnoslot）

- 通过 `HandleActorControlPacket` Hook 拦截
- category = 407 且 TerritoryType = 1279（限定地图）
- 根据 arg1 映射 HypnoslotResultType（156=AllDiff, 157=AllSame, 158=Preserve, 159=Reroll, 161=Resume → wheel-open，160=End → wheel-end）

### 选门开门地图回退（Door Selection Fallback）

选门开门地图（TerritoryType: 588/712/725/879/1000/1123）中，当玩家未进动画时不会产生预测网络包。此时通过以下两条回退机制补记历史记录：

1. **开门回退**：订阅 `IChatGui.LogMessage` 事件，当 `logMessageId` 为 6998（6 区版）或 9365（4 区版）时，从第 0 个整数参数获取轮数（Case(1)→第二区→round=1，Case(2)→第三区→round=2，…），静默新增一条 "开门（第 N 轮）" 历史记录。该记录不播报、不聊天输出，仅写入历史列表。
2. **退出失败回退**：当从选门地图退出（领地变动）时，若本次会话未收到 gate-fail 网络包且未发生 `DutyWiped` / `DutyCompleted` 事件，则取上一条历史记录的轮数 X，静默新增一条 "失败（第 X+1 轮）" 历史记录。

两条回退记录均通过 `MainWindow.AddResult` 的去重逻辑（对比当前会话内上一条记录的 Value 和 Round，遇分隔线即停止）确保不重复。

## 日志与调试

- 插件在加载和配置变更时会记录信息到 Dalamud 日志
- 开启设置中的 **"Debug 日志输出"** 后：
  - 转盘/开门匹配成功时输出 80 字节 hex dump 及各字段偏移量标注，便于验证
  - 仅在宝藏领地输出 ActorControl 类别摘要（每 50 包），避免无关区域刷屏
  - 仅在宝藏领地输出 ActorControl category=407 事件详细参数
  - 转盘 level 签名匹配但结果字节未知时输出 Warning，提示可能需要更新枚举

## 输出样例

- 主窗口历史（带时间戳）：
  ```
  [20:58:12] [G15 育体宝殿] 下级召唤
  [20:58:10] [宝物库] 开门 (第1轮)
  [20:57:55] [巡梦金库] 成功
  [20:50:01] ❀❀下底成功❀❀
  [20:45:30] 挖宝也能团灭？回家吧，孩子
  ──────────────────
  ```

- 选门地图回退记录（静默写入，不播报）：
  ```
  [21:05:30] [G8 水城宝物库] 开门 (第2轮)   ← logmessage 回退补记
  [21:05:45] [G8 水城宝物库] 失败 (第3轮)   ← 退出地图回退补记
  ```

- 聊天框输出（若启用）：
  ```
  [巡梦金库] 成功
  [宝物库] 上级召唤
  ```

- 成就进度标签页展示（每个成就带 4 色进度条及称号信息）：
  ```
  G18 巡梦金库
  [████████████░░░░░░░] 15 / 20 (75%)  巡梦者
  ```

- 主窗口历史中按类型有颜色区分：
  - 蓝色 — 下级召唤 (wheel-low)
  - 绿色 — 中级召唤 (wheel-medium)
  - 红色 — 上级召唤 / 开门失败 / 团灭 (wheel-high / gate-fail / duty-wiped)
  - 金色 — 召唤式变动 / 下底成功 (wheel-shift / dungeon-complete)
  - 银色 — 特殊召唤 (wheel-special)
  - 紫色 — 失败 (wheel-end)
  - 亮绿 — 开门成功 (gate-open / wheel-open)

## 项目结构

```
TreasureForecast/
├── Data/
│   └── Constants.cs              —— 成就 ID / 宝藏领土 ID 等常量
├── Models/
│   ├── AchievementProgressInfo.cs —— 成就进度数据模型
│   └── TreasureResultDTO.cs      —— 挖宝结果 DTO（含时间戳）
├── Utils/
│   └── ResultFormatter.cs        —— 结果格式化（中文描述映射）
├── Windows/
│   ├── MainWindow.cs             —— 主窗口（历史记录 + 成就进度 + 导出）
│   ├── ConfigWindow.cs           —— 设置窗口
│   └── Style.cs                  —— 窗口样式常量（颜色 / 进度条配色 / Push-Pop 计数）
├── AchievementTracker.cs         —— 成就进度 Hook（ReceiveAchievementProgress）
├── NetworkReceiver.cs            —— 底层网络 Hook 管理（双通道捕获 + 偏移量试探 + 嵌套枚举 + 领地过滤）
├── TreasurePredictionService.cs  —— 预测逻辑核心（结果触发 + 去重）
├── Plugin.cs                     —— Dalamud 插件生命周期、选门地图回退、团灭/完成事件追踪
├── Configuration.cs              —— 插件配置
└── TreasureForecast.json         —— Dalamud 插件清单
```

## 编码规范

本项目遵循以下编码规范（参考 [DailyRoutines.CodeAnalysis](https://github.com/Dalamud-DailyRoutines/DailyRoutines.CodeAnalysis) 规则集）：

- **禁止下划线前缀**：私有字段不使用 `_` 前缀，实例字段使用 camelCase，静态/常量字段使用 PascalCase
- **缩写大小写一致**：英文缩写（如 ID、DTO、UI 等）在标识符中保持全大写或全小写，不混用
- **使用 nint 代替 IntPtr**：统一使用 C# 原生类型别名 `nint` 而非 `System.IntPtr`

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

