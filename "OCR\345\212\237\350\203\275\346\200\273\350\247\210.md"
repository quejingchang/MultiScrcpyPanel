# OCR + 点击脚本功能（OpenCV 模板匹配）

> 目标：脚本识别一张或多张模板图片 → 取各命中框「位置交集」为点击区域 → 区域内随机点点击；
> 支持为每个日常任务编排 OCR 步骤。每天任务位置变化也能自动找到。

## 1. 新增 `OCR` 脚本指令
```
OCR <图1> [图2 ...] [MAXERR 0.15] [TIMEOUT 0] [RETRY 1] [WAIT 300] [DX n] [DY n] [CENTER]
```
- 单图：直接点击该图标命中框（区域内随机点；`CENTER` 取中心）。
- 多图：**各命中框求位置交集**作为最终点击区；多图不相交则视为未命中并重试。
- `MAXERR` 相似度阈值（越小越严）；`TIMEOUT`/`RETRY` 未全命中时重试；`DX`/`DY` 最终点归一化偏移。
- 旧 `FIND <图标> [THEN TAP dx dy]` 仍可用，二者可混编。

模板图解析顺序：脚本目录 → `templates/` 目录（仓库 `csharp/templates/`）→ 默认脚本目录。

## 2. 匹配器实现（OpenCV 优先，托管回退）
- `ITemplateMatcher` 抽象；`TemplateMatch` 结果含归一化中心、半宽高、相似度。
- `OpenCvTemplateMatcher`：P/Invoke 项目自带的 `opencv_world4120.dll`（OpenCV 4.1.2 C API），
  `TM_CCOEFF_NORMED` 多尺度（0.10–1.20）宽范围搜索 —— 因为模板多为整屏截图裁出的图标、视频帧被降采样，
  尺度差大，固定比例搜不到。
- `ManagedTemplateMatcher`：纯托管 `TemplateMatcher`（彩色 SSD），库不可用时自动回退。
- `TemplateMatcherFactory.Default` 在运行时选择（静态构造探测 OpenCV 可用性）。
- 注：该 opencv 构建未导出 `cvLoadImage`，图像经 GDI+ 载入后转 `IplImage`（BGR 8bit）交给 OpenCV。

## 3. 部署
`MultiScrcpyPanel.csproj` 已将 `opencv_world4120.dll`（输出根目录）与 `templates/**`（输出 `templates/`）复制到输出目录。

## 4. 各日常任务 OCR 脚本
`scripts/mhxy/OCR_*.scr`（对应原 01–14 日常任务），全部 OCR 识别定位，不依赖固定坐标。
缺专属图标的任务（封妖/副本/答题/门派闯关/帮派强盗/比武大会）用最近似的挑战/秘境模板占位并注释提示补充。

## 5. 验证
本地副本 `dotnet build` + `dotnet test`：458 项测试全过（含 7 项 OCR 单测，用 `FakeMatcher` 注入确定命中），
0 警告 0 错误。OpenCV 原生路径在用户机器（含 MSVC 运行库）构建/运行，托管回退保证无 OpenCV 时功能仍可用。

## 6. 用法
工具栏「脚本」下拉 → 选 `OCR_师门任务` 等 → 运行。建议先用真实截图微调等待时长与各任务专属图标。
