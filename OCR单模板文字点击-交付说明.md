# OCR 增强：单模板 + 可选模板内文字定位点击 — 交付说明

> 日期：2026-08-18 ｜ 代码位置：`Z:\Document\Development\手机投影\csharp`（本仓库根目录即源码根）

## 一句话总结
OCR 指令改为**只能选一张模板图**，可进一步设置**点击文字**（如模板 `宝图任务.png` + 点击文本 `参加`）：运行时先在模板内识别文字位置，再识别模板在截图中的位置，最后算出文字在截图中的最终坐标精确点击。

## DSL 语法
```
OCR <模板图> [TEXT "文字"] [MAXERR 0.15] [TIMEOUT 0] [RETRY 1] [WAIT 300] [DX n] [DY n] [CENTER]
```
- **无 TEXT**：点击模板命中框（CENTER 取中心，否则随机）。
- **有 TEXT**：先在模板内定位文字（词级包围盒），再换算到截图坐标精确点击；文字未找到 / 文字引擎不可用时退化为点击模板中心。
- 兼容旧脚本：传多张图时只取第一张作模板，其余忽略并提示一次。

## 坐标换算（关键）
```
fx = (Nx - HalfW) + tx * 2 * HalfW
fy = (Ny - HalfH) + ty * 2 * HalfH   // ⚠️ y 必须用 HalfH，初版误用 HalfW 已修正
```
`(tx, ty)` 为文字在模板内的归一化中心（0–1，相对模板左上角）；`(Nx±HalfW, Ny±HalfH)` 为模板在截图中的命中框。

## 实现要点
| 文件 | 改动 |
|------|------|
| `MultiScrcpyPanel/Core/Scripting/ScriptEngine.cs` | `OcrInstruction.Text` + `ParseOcr` 的 `TEXT` 解析；`RunOcr` 重写为单模板+文字定位；新增 `GetTemplateTextOffsetAsync` + `s_templateTextCache`（按 `模板路径\|文字` 缓存，模板静态只识别一次）；`RunBlock` 派发传入 `textRecognizer`；补 `using System.Text` |
| `MultiScrcpyPanel/Core/Scripting/ScriptActionModel.cs` | `OcrStep` 增加 `Text` 属性（构造/解析/ToDsl/Summary 全链路） |
| `MultiScrcpyPanel/UI/ScriptEditorForm.cs` | `BuildOcr` 从多选 CheckedListBox 改为单模板下拉框 + 可选点击文字输入框 |
| `MultiScrcpyPanel/Core/Scripting/TextRecognition/ITextRecognizer.cs` | 新增 `RecognizeWordsAsync(Bitmap)`（词级包围盒） |
| `MultiScrcpyPanel/Core/Scripting/TextRecognition/TesseractTextRecognizer.cs` | `RecognizeWordsAsync` 用 `tsv` 输出解析 level==5 词行 → 真实归一化包围盒（`ParseTsvWords`）；`RunTesseractAsync` 支持附加输出格式参数 |
| `MultiScrcpyPanel/Core/Scripting/TextRecognition/TextMatcher.cs` | 新增 `FindBestSpan`：按阅读顺序合并连续词（兼容"参""加"被 Tesseract 拆词），返回并集包围盒+误差 |
| `MultiScrcpyPanel/Core/Scripting/TextRecognition/FakeTextRecognizer.cs` | 补 `RecognizeWordsAsync`（测试用） |
| `MultiScrcpy.Tests/ScriptOcrTests.cs` | 重写为单模板语义：TEXT 解析/带空格引号、文字偏移精确点击 (55,90)、未找到退化中心、多图取首图、高亮回调、重试等 |

## 验证结果（用户自行在 Z: 编译测试）
- `dotnet build -p:Platform=x64`：0 错误 0 警告（C: 副本上已验证）
- `dotnet test -p:Platform=x64 --no-build`：496 通过 / 0 失败（C: 副本上已验证）

## 后续建议
1. 运行时验证：选一张含文字的模板 + 设置点击文字，确认日志出现 `OCR 模板命中 ... 文字"参加"位置(x.xxx,y.yyy)`。
2. 若模板内文字识别不准，可调整 `MAXERR`（默认 0.15）或换用更清晰的模板截图。
3. 示例脚本可更新为 `OCR 宝图任务.png TEXT "参加" CENTER` 形式。
