# SeeMe

一个 Windows 桌面文档查看器，基于 **WPF + WebView2**。以 Markdown 为核心，并扩展支持 Office 文档与 PDF 的「所见即所得」预览。

> 当前版本：**v1.0.3** ｜ 技术栈：.NET 8.0 (Windows) · WPF · WebView2 · Markdig · OpenXML · PdfPig · anydoc-wasm · ECharts · markmap · docx-preview · Univer · Lucene.NET · Velopack · Sentry

---

## ✨ 核心特性

**查看**
- Markdown 全语法渲染：Prism 代码高亮、KaTeX 公式、Mermaid 流程图、ECharts 图表、markmap 思维导图（全离线）
- Office / PDF / ePub 统一预览；PDF 支持逐页图文重建、图片放大、目录大纲
- 双栏分屏对照阅读，明 / 暗主题，缩放与滚动位置记忆

**标注**
- 高亮笔：选中即标注（md / Office / PDF），重启后按文本恢复
- 笔记面板：自由记录、自动保存，可导出 Markdown
- 书签、最近文件、反向链接

**检索**
- `Ctrl+F` 页内查找，逐条高亮定位
- 搜索框 `>关键词`：Lucene.NET 全文索引，秒级检索历史文件

**编辑与导出**
- 文本 / docx 内联编辑，输入停顿自动保存
- pandoc 导出 docx / pdf / html / epub / latex

**集成**
- 文件关联：一键设为 `.md` 默认查看器 + 右键菜单（HKCU，免管理员）
- 拖拽打开、命令行传参、源文件改动自动刷新、打印（Ctrl+P）
- 可选：Velopack 自动更新、Sentry 崩溃上报（环境变量开启）

**安全**
- 禁用原始 HTML，全链路输出转义，CSP nonce 脚本白名单
- 文件头与扩展名一致性校验，拦截伪造文件
- 外链 scheme 白名单，远程图片默认拦截
- Release 构建错误页脱敏，PDF 正文不落日志

---

## 🧩 支持的文档类型

| 格式 | 扩展名 |
|------|--------|
| 📝 Markdown | `.md` `.markdown` `.mkd` `.mdown` |
| 📄 Word | `.docx` `.doc` `.docm` `.rtf` `.odt` |
| 📊 Excel | `.xlsx` `.xls` `.xlsm` `.ods` `.csv` |
| 📽 PowerPoint | `.pptx` `.ppt` `.odp` |
| 📚 电子书 | `.epub` |
| 📕 PDF | `.pdf` |

**渲染链路**

- **Office / 电子书** — anydoc-wasm 页内转换；失败或超时自动回退本地解析（`.docx` → docx-preview 高保真，`.xlsx/.xlsm` → Univer 原生渲染，其余 → OpenXML）
- **PDF** — PDF.js 4.x 逐页图文重建，支持文本层、大纲、页内查找
- **宏文档**（`.docm` / `.xlsm`）— 只解析内容，不执行宏

**Markdown 扩展围栏**

- `` ```echarts `` — 写 ECharts option JSON，渲染交互图表（跟随主题）
- `` ```markmap `` — 写 Markdown 标题，渲染可缩放思维导图
- 左侧「大纲」Tab 的 🗺 导图按钮：当前文档标题结构一键转导图

---

## 📁 项目结构

```
SeeMe/
├── src/                       # 全部源码
│   ├── MainWindow.*.cs        # 主窗口，按功能分部（文件 / 渲染 / 面板 / 笔记 / 日志）
│   ├── RenderService.*.cs     # Markdown → HTML、样式与脚本，含 CSP 与消毒
│   ├── *Store.cs              # 持久化：历史 / 书签 / 高亮 / 笔记（共用 JsonListStore）
│   ├── SearchIndexService.cs  # Lucene.NET 全文检索索引
│   ├── FileConverter.cs       # Office / PDF → HTML（本地解析器）
│   ├── OfficeFallback.cs      # anydoc-wasm 失败后的回退决策
│   ├── ThemeManager.cs        # 亮 / 暗主题构建与持久化
│   ├── CommandPaletteWindow.* # 命令面板
│   └── Controls/              # 自定义控件
├── tools/                     # 构建 / 发布 / 依赖扫描脚本（本地开发用）
├── SeeMe.csproj               # .NET 8 WPF 工程
└── README.md · LICENSE · .gitignore
```

---

## 🚀 使用

1. 启动后通过「文件 → 打开」选择文档，或直接**拖拽文件**到窗口。
2. 命令行打开指定文件：
   ```bash
   SeeMe.exe path/to/document.md
   ```
3. 常用操作：
   - **高亮笔**：标题栏 🖍 按钮开启，选中文本自动高亮（右键标注删除）
   - **笔记**：状态栏「笔记」打开面板，输入内容点「添加」，选中笔记直接修改（自动保存），可导出 Markdown
   - **打印**：Ctrl+P 或状态栏「打印」
   - **文件关联**：设置 → 通用设置 → 文件关联，一键设为 .md 默认查看器 / 注册右键菜单
   - **页内查找**：Ctrl+F 悬浮搜索栏；**内容搜索**：搜索框输入 `>关键词`
   - **大纲**：左侧「文件 / 大纲」Tab 切换，点击条目跳转

### ⌨ 快捷键

| 快捷键 | 功能 |
|--------|------|
| Ctrl+O | 打开文件 |
| Ctrl+T | 切换双栏 |
| Ctrl+F | 页内查找 |
| Ctrl+P | 打印 |
| Ctrl+S | 编辑模式保存 |
| Ctrl+N / Ctrl+W | 新建窗口 / 关闭窗口 |
| Ctrl+Shift+D | 切换主题 |
| Ctrl+0 / Ctrl+= / Ctrl+- | 缩放复位 / 放大 / 缩小 |
| F5 | 手动刷新 |

---

## 🛠 构建

要求：Windows + [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) + WebView2 运行时。

```bash
# 还原依赖并构建
dotnet build -c Release

# 单文件自包含发布（win-x64，含运行时）
dotnet publish -c Release -p:PublishSingleFile=true -r win-x64
```

生成物位于 `bin/Release/net8.0-windows/`；单文件发布产物在输出目录（含 `Resources/` 离线资源，需与 exe 同目录）。

> **发布安装包请用 `tools\publish-installer.cmd`**，不要手工拼步骤。它从
> `SeeMe.csproj` 的 `<Version>` 读版本（单一来源），依次执行：生成 → 单文件发布到
> `D:\allll\SeeMeOut` → 静态检查 → 测试门禁 → 便携 ZIP → NSIS 安装包。
> 注意安装包打包的是**发布目录**而非 `bin/Release`，所以这一步不能省 ——
> 否则安装包名字取自新版本、里面装的却可能是旧 exe（本仓库实测踩过此坑）。
> NSIS 在编译期会校验待打包 exe 的文件版本，不一致直接中止。

---

## ⚙ 环境变量

全部为**可选**，不设置时对应功能自动关闭，应用照常运行。

| 变量 | 作用 | 缺省行为 |
|------|------|----------|
| `SEEME_SENTRY_DSN` | 崩溃与异常上报的 Sentry DSN。设置后启用上报 | 不设置 → 关闭，不发送任何数据 |
| `VELOPACK_FEED` | 自动更新源地址（如 GitHub Releases 目录）。设置后启动时检查更新 | 不设置 → 关闭，不联网检查 |
| `SEEME_PANDOC_DIR` | 指定 Pandoc 可执行文件所在目录（用于导出 Word/PDF 等） | 不设置 → 依次尝试 PATH 中的 `pandoc`、应用目录下的 `tools/pandoc` |

`SEEME_PANDOC_DIR` 示例（Windows）：

```powershell
# 本次会话生效
$env:SEEME_PANDOC_DIR = "D:\allll\pandoc"
# 永久生效（当前用户）
[Environment]::SetEnvironmentVariable("SEEME_PANDOC_DIR", "D:\allll\pandoc", "User")
```

> 日志默认写入 `%LOCALAPPDATA%\SeeMe\logs\seeme.log`（单文件滚动，上限 512 KB，超限轮转为 `seeme.log.1`）。`Warn` / `Error` 级别在 Release 构建下仍会落盘，`Info` 仅在 Debug 构建下记录。

---

## 📜 许可证

基于 **MIT 许可证** 开源，详见仓库根目录 `LICENSE` 文件。
