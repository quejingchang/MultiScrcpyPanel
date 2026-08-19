# OCR 增强：模板图片文字识别能力优化（双通道融合）— 交付说明

> 日期：2026-08-18 ｜ 仓库：`D:\阙靖昌\其他\软件\手机投影` ｜ 交付：✅ 已完成并通过 QA

## 一句话总结
`OCR <模板> TEXT "文字"` 的"模板内文字定位"能力升级为**双通道自动融合**：原图（灰度+2x）与 Otsu 二值化两条通道并行识别，词级结果合并去重。游戏 UI 模板（深棕字 + 米色渐变 + 装饰边框）原本完全识别不出的文字被救回，且**绝不丢失**原图能识别的词。

## 为什么需要优化（实测基线，tesseract 5.5.3 + chi_sim）
| 模板 | 旧实现（灰度+2x） | Otsu 二值化 | 结论 |
|------|------------------|------------|------|
| 日常_三界奇缘 / 帮派任务 | 关键词识别不全 | 完整识别 | Otsu 有效 |
| 日常_捉鬼任务 | **完全无"捉/鬼/任务"** | 补出 捉\|鬼\|任务 | **Otsu 救回** |
| 挑战_蜃影秘境 | 无"秘境" | 补出 古\|影\|秘境 | **Otsu 救回** |
| 日常_师门任务 | 放大后丢失"师门任务" | 同样丢失 | Otsu 会破坏此类图 |
| 按钮_使用 | 拆词"使 用" | 完整 | 双通道均覆盖 |

→ **不能"加二值化一刀切"**：Otsu 救回失败样本，但会破坏师门任务类模板。因此采用**双通道融合**，两路结果取并集，保底不丢词。

## 实现要点
| 文件 | 改动 |
|------|------|
| `MultiScrcpyPanel/Core/Scripting/TextRecognition/TesseractTextRecognizer.cs` | `RecognizeWordsAsync` 双通道：A=灰度+2x（原行为，PSM6 词数<3 回退 PSM11）；B=灰度+2x+**Otsu**（`THRESH_BINARY|THRESH_OTSU`，单通道二值图直写 PNG）同套 PSM 回退；新增 `MergeAndDedupeWords`（internal static）与 `BuildBinaryMat`；临时文件前缀区分（A=`mscp_tessw_`，B=`mscp_tessw_b_`），finally 全清理；构造器新增可选参数 `enableBinaryChannel=true`（向后兼容，未加 AppConfig、未动 UI） |
| `MultiScrcpy.Tests/TesseractTextRecognizerTests.cs` | 注释修正为真实基线 + 3 个双通道集成测试（三界奇缘/按钮使用/超集不变量）+ **2 个真实救回样本强判别测试**（日常_捉鬼任务、挑战_蜃影秘境：单通道必挂、双通道命中） |
| `MultiScrcpy.Tests/WordMergeTests.cs` | **新增**，8 个去重纯单元测试（含阈值 0.02、通道空降级、超集等） |
| 未改动 | `ITextRecognizer` / `TextRecognizerFactory` / `FakeTextRecognizer` / `TextMatcher` / `ScriptEngine`（模板文字缓存 `s_templateTextCache` 保留）/ `UI/` / csproj（0 NuGet 变更） |

### 去重规则（核心）
- 文字相同（Ordinal）且归一化中心**欧氏距离 ≤ 0.02**（常量 `DedupeCenterDistanceThreshold`）→ 视为同一词，保留通道 A 版本（避免 B 的坐标偏移）
- 通道 A 为空 → 直接返回通道 B（Otsu 救回灰度完全失败的样本）
- 通道 B 异常（IO/OpenCvSharp/Win32 等）→ 降级为仅通道 A；`OperationCanceledException` 正常传播
- **超集不变量**：双通道结果 ⊇ 单通道结果，永不丢词（有测试保证）

## 验证结果
- `dotnet build -p:Platform=x64`：**0 错误 0 警告**
- `dotnet test -p:Platform=x64 --no-build`：**509 / 509 全过**，0 失败 0 跳过
- QA 独立验证（43 张真实模板全量扫描）：
  - **双通道救回 5 个模板**：日常_捉鬼任务、挑战_蜃影秘境、挑战_星辰之路、秘境_海底秘境、弹窗_师门任务完成标题
  - **丢失 0 个**，超集不变量在全部 43 个模板上成立
- 强判别测试实证：
  - 日常_捉鬼任务：span 误差 1.000 → **0.500**（单通道搜不到 → 双通道搜到）
  - 挑战_蜃影秘境：span 误差 1.000 → **0.000**

## 已知限制与后续建议
1. **师门任务 / 确定 类模板**：在"灰度+2x 放大"预处理下本就不识别（放大反而破坏识别，原图直传 CLI 可识别），双通道无法救回；OCR TEXT 会退化到点击模板中心——**建议立项"第三通道（原图直传）"**：QA 实测可额外救回 4 个模板（挑战_九转天阶、挑战_十二元辰、日常_师门任务、日常_金蝉心_普通），复用现有 `MergeAndDedupeWords` 做并集，零回归风险
2. 仍有约 10 个模板任何通道（含原图直传）都识别不出（艺术装饰字/低对比），只能靠模板中心兜底
3. 若加第三通道，建议先做 43 模板全量回归，确认无新垃圾词干扰 `FindBestSpan`

## 使用说明（用户无感知）
- 无需改任何脚本 / 配置 / UI——原 `OCR 模板 TEXT "文字"` 指令行为自动增强
- 构造器新参数 `enableBinaryChannel`（默认 true）仅供需要强制单通道时使用
