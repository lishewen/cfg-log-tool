# CfgLogTool

分析仪器工作站**系统日志 .cfg 文件**的查看 / 导出 / 编辑工具。

某些分析仪器工作站软件把系统日志导出为 `.cfg` 文件：**4 字节小端长度头 + zlib 压缩流**，解压后是 Delphi VCL 风格的“伪 XML”（属性写在标签 `>` 之后），标准 XML 工具无法读取。本工具完整支持该格式的解码、浏览、导出（XML/CSV/可读报告）与无损编辑（按时间删除记录、按 GUID 修改记录时间），提供**跨平台命令行**与 **Windows 图形界面（WPF）** 两个版本。

> ⚠️ **使用须知**：本工具仅限对**您本人拥有或负责维护的设备**的数据进行恢复、备份核对、审计验证等合法用途。请遵守所在实验室/机构的管理规定，妥善保管原始数据。

## 功能

| 能力 | CLI | GUI |
|---|:---:|:---:|
| 打开/解码 .cfg，查看全部日志条目 | ✅ | ✅ |
| 实时筛选（时间/用户/类型/操作/GUID） | — | ✅ |
| 导出标准 XML / Excel 友好 CSV / 可读报告 | ✅ | ✅ |
| 按时间前缀删除记录（日/时/分/秒粒度） | ✅ | 多选删除 |
| 按条目 GUID 修改记录时间 | ✅ | ✅ |
| 保存前自动回读验证，不覆盖原文件 | ✅ | ✅ |

## 快速开始

仓库自带合成样例 `samples/sample.cfg`（虚构数据，可安全用于测试）：

```bash
# 命令行 (macOS / Linux / Windows)
$ CfgLogTool list samples/sample.cfg
日志编号 : SL2405200001
日志类型 : System Log
创建时间 : 2024-05-20 08:30:00
条目总数 : 12
------------------------------------------------------------------------
  1. [2024-05-20 08:30:05] admin - Login - Login  {27548474-...}
  2. [2024-05-20 08:30:12] admin - 系统日志 - 打开  {A0ED2194-...}
  ...
```

### CLI 用法

```bash
CfgLogTool list   <file.cfg>                              # 查看日志信息与全部条目（含 GUID）
CfgLogTool decode <file.cfg> [--out 目录]                 # 导出 .xml / .csv / _readable.txt
CfgLogTool remove <file.cfg> --time "2024-05-20 08:31" [--out 新文件.cfg]
CfgLogTool setdate <file.cfg> --id "{GUID}" --date "2024-05-20 08:40:00" [--out 新文件.cfg]
```

- `--time` 前缀匹配：`2024-05-20`（整天）/ `2024-05-20 08:31`（整分）/ 到秒均可；
- `--id` 按条目 `ItemObj` GUID 唯一定位（忽略大小写，匹配多条会拒绝执行）；
- `--date` 支持 `yyyy-MM-dd HH:mm:ss` 等常见格式，自动规范化；
- 默认输出 `<原名>_new.cfg`（`setdate` 为 `<原名>_edit.cfg`），**从不覆盖原文件**。

### GUI（Windows x64）

打开或直接把 `.cfg` 拖入窗口 → 表格展示全部条目，顶部任意字段实时筛选：

- 选中行 → 右侧面板选择新日期、填写 `HH:mm:ss` → **应用新时间**（改动行黄色高亮）
- Ctrl/Shift 多选 → **删除选中条目**（灰显，可随时恢复）
- **保存修改** → 输出 `<原名>_edit.cfg`（已存在自动递增编号），保存前自动回读验证
- **导出三件套** → 标准 XML / CSV（当前编辑视图）/ 可读报告

## 构建

需要 .NET 10 SDK。仓库含解决方案 `CfgLogTool.slnx`（CLI + WPF 两个项目；WPF 已配置 `EnableWindowsTargeting`，任意平台可交叉编译）。

```bash
# CLI — 自包含单文件（macOS Apple Silicon）
cd src/CfgLogTool
dotnet publish -c Release -r osx-arm64 --self-contained true \
  /p:PublishSingleFile=true /p:PublishTrimmed=true /p:TrimMode=partial

# CLI — 自包含单文件（Windows x64）
dotnet publish -c Release -r win-x64 --self-contained true \
  /p:PublishSingleFile=true /p:PublishTrimmed=true /p:TrimMode=partial

# GUI — WPF 自包含单文件（Windows x64，不支持裁剪，约 130MB）
cd ../CfgLogToolWpf
dotnet publish -c Release -r win-x64 --self-contained true \
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

# GUI — 精简版（目标机需已装 .NET Desktop Runtime，约 2MB）
dotnet publish -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true
```

## 文件格式

详见 [docs/format.md](docs/format.md)：

```
uint32 (LE) = 解压后长度  |  zlib 流 (78 9C)  →  UTF-8 伪 XML
<TLogFiles><subs><TLogFile> 属性写在 > 之后 ...<ScanData><TLogDatas><subs>
  <TLogData> ItemObj="{GUID}" ItemCode/ItemName/ItemDate/ItemType/ItemMark="..."</TLogData>
```

## 实现要点

零外部依赖，纯 .NET BCL（`System.IO.Compression`）：

- **解析**：伪 XML 无法用标准解析器，采用编译正则提取 `key="value"` 属性；
- **编辑**：对解压文本做切片替换，仅改动目标属性，其余字节与原文件**逐字节一致**；
- **安全**：写盘前对新文件完整解码回读并逐字节比对（回读验证），不一致即报错不落盘。

## 许可

[MIT](LICENSE) · 样例数据为程序合成，与任何真实设备/人员无关。

---

**English** — Viewer/exporter/editor for instrument workstation system logs stored as `.cfg` (4-byte LE length + zlib + Delphi VCL pseudo-XML whose attributes appear after the opening tag). Cross-platform CLI + Windows WPF GUI. Zero dependencies. Byte-exact edits with readback verification; source files are never overwritten. See [docs/format.md](docs/format.md).
