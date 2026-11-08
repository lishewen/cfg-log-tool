# .cfg 系统日志文件格式分析

本文档描述本工具所支持的仪器工作站系统日志 `.cfg` 容器格式，全部结论来自对文件结构的逆向分析。

## 1. 容器结构（二进制）

```
偏移 0  ┌──────────────────────────────┐
        │ uint32 (小端) = 解压后字节数   │
偏移 4  ├──────────────────────────────┤
        │ zlib 压缩流 (标准头 78 9C)     │
        │ 解压后为 UTF-8 文本            │
        └──────────────────────────────┘
```

- 前 4 字节是**小端无符号整数**，声明解压后的长度，可用于完整性校验。
- 偏移 4 起是标准 zlib 流（`78 9C`，默认压缩级别），解压后为 UTF-8 文本。

## 2. 伪 XML 载荷

整体层级：

```
TLogFiles
└─ subs
   └─ TLogFile        ← 日志文件级属性（编号/创建时间等）
      └─ ScanData
         └─ TLogDatas
            └─ subs
               └─ TLogData × N   ← 每条日志记录
```

**关键怪癖：元素的属性写在开标签的 `>` 之后**，例如：

```
<TLogFile> ItemObj="{...}" ItemCode="SL2405200001" Creationtime="2024-05-20 08:30:00" ItemMark=""<ScanData>
...
<TLogData> ItemObj="{...}" ItemCode="admin" ItemName="admin" ItemDate="2024-05-20 08:30:05" ItemType="Login" ItemMark="Login"</TLogData>
```

因此**标准 XML 解析器读不出任何属性**（开标签在 `>` 处已闭合，属性文本成为无标签的字符数据）。两条处理路线：

1. **正则直接提取**（本工具采用）：按 `<TLogData> ... </TLogData>` 匹配后抓取 `key="value"` 对；
2. 修复为标准 XML 后再解析：将 `<TLogFile> ` 替换为 `<TLogFile `、`<TLogData> ` 替换为 `<TLogData `（仅在需要 XML 工具链时使用，如 `xmllint`）。

## 3. 字段说明

### TLogFile（文件级）

| 属性 | 含义 |
|---|---|
| ItemObj | 文件对象 GUID |
| ItemCode | 日志编号（如 `SL2405200001`，SL+日期+序号） |
| ItemType | 日志类型（如 `System Log`） |
| CreatorCode / CreatorName | 创建者（通常为“系统”） |
| Creationtime / Modificationtime / Accesstime | 创建/修改/访问时间 |
| ItemMark | 备注（常为空） |

### TLogData（每条记录）

| 属性 | 含义 |
|---|---|
| ItemObj | 记录 GUID（**全局唯一**，可作为主键定位单条记录） |
| ItemOID | 关联对象 GUID |
| ItemCode / ItemName | 账号 / 用户名 |
| ItemDate | 记录时间 `yyyy-MM-dd HH:mm:ss` |
| ItemType | 类型：`Login` / `Logout` / 系统日志 / 标准曲线 / 用户 / 历史数据管理等 |
| ItemMark | 动作：`Login` / `Logout` / 打开 / 关闭 / 打开：20240520 等 |

## 4. 编辑与回写

- 删除记录 = 从文本中移除对应的 `<TLogData> ... </TLogData>` 片段；
- 修改时间 = 仅替换该条目内的 `ItemDate="..."` 属性值；
- 其余所有字节保持原样（**不格式化、不重排**），再按“4 字节小端长度 + zlib”重新打包；
- 回读验证：对新文件重新解压，与预期文本逐字节比对，一致才视为成功。

## 5. 其他

- zlib 头为 `78 9C`（默认级别）。重新压缩后字节流与原始压缩可能不同，但解压结果一致即等价；本工具以“解压文本逐字节一致 + 头部长度正确”作为校验标准。
- 样例数据 `samples/sample.cfg` 为程序合成的虚构数据（虚构用户/编号/时间），可安全用于测试。
