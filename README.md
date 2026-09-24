# Jellyfin Folder Collections

[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.10.7-blue)](https://jellyfin.org)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com)
[![License](https://img.shields.io/badge/license-MIT-green)](LICENSE)

一个 Jellyfin 插件，根据物理文件夹结构自动生成媒体集合。适合个人媒体库、无刮削资源的分类整理，让 Jellyfin 的搜索能够按文件夹结构精准定位内容。

---

## ✨ 功能特性

### 核心功能

- **文件夹结构驱动**：扫描媒体库的物理文件夹，自动为每个文件夹生成一个 Jellyfin 集合。
- **多级路径拼接**：支持深层嵌套，例如 `Captures\2024\春节\video.mp4` 会生成集合 `Captures-2024-春节`。
- **超长名称保护**：当拼接后的集合名超过设定长度时，使用 MD5 哈希后缀保证唯一性，避免不同文件夹被错误合并。
- **增量同步**：每次扫描对比差异，只新增或移除变化的媒体项，不做全量重建，性能友好。
- **自动清理**：媒体库中删除的文件夹，其对应的集合会在下次扫描时自动清理（仅删除插件自己创建的集合，不会误删手动创建的）。

### 标签系统

- **从文件夹名提取标签**：支持 4 种标记格式，每种可独立开关：
  - `#标签` — 例如 `视频 #教程 #入门`
  - `{标签}` — 例如 `视频 {教程}`
  - `[标签]` — 例如 `视频 [教程]`
  - `【标签】` — 例如 `视频 【教程】`
- **自动更新**：文件夹名变化时，集合标签同步更新。
- **标签可用于筛选**：在 Jellyfin 集合页面可以按标签筛选集合。

### 封面管理

- **自动封面**：集合创建时，自动取集合内第一个有缩略图的视频作为集合封面。
- **不覆盖已有封面**：如果你手动为集合设置过封面，插件不会替换。

### 性能与稳定性

- **分页查询**：分批从数据库读取媒体项，避免大数据量（数万甚至数十万）下的查询截断问题。
- **元数据锁定**：集合创建后自动锁定元数据，避免 Jellyfin 触发 TMDb 等在线服务请求导致扫描卡顿。
- **可观测日志**：每一步操作都有清晰的日志，方便排查问题。

### 触发方式

- **手动触发**：在插件设置页面点击"立即扫描"。
- **自动触发**：媒体库扫描完成后自动同步集合（可关闭）。

### 配置页面

提供完整的 Web 配置页面：

- 选择需要扫描的媒体库
- 覆盖模式（清空重建 vs 增量同步）
- 自动删除过时集合
- 媒体库扫描后自动同步
- 集合名最大字符长度
- 4 种标签格式的独立开关

---

## 🖥️ 环境要求

| 项目 | 要求 |
|------|------|
| **Jellyfin** | 10.10.7 |
| **.NET SDK** | 8.0 或更高 |
| **操作系统** | Windows / Linux / macOS（推荐 Windows，长路径支持更完备） |

---

## 📦 安装

### 从发布包安装

1. 从 [Releases](https://github.com/yourname/jellyfin-plugin-foldercollections/releases) 页面下载最新版本。
2. 在 Jellyfin 数据目录下找到 `plugins` 文件夹（Windows 通常为 `%LOCALAPPDATA%\jellyfin\plugins` 或 `%ProgramData%\Jellyfin\Server\plugins`）。
3. 创建子文件夹 `FolderCollections`，把 DLL 和 `meta.json` 复制进去：
   ```
   plugins/
   └── FolderCollections/
       ├── Jellyfin.Plugin.FolderCollections.dll
       └── meta.json
   ```
4. 重启 Jellyfin。

### 从源码编译

```bash
# 克隆项目
git clone https://github.com/yourname/jellyfin-plugin-foldercollections.git
cd jellyfin-plugin-foldercollections

# 编译
dotnet build -c Release

# 编译输出在 bin/Release/net8.0/
```

---

## 🚀 使用

### 1. 配置媒体库

进入 Jellyfin 控制台 → 插件 → **Folder Collections** → 设置：

1. 在"选择需要扫描的媒体库"中勾选需要生成集合的媒体库。
2. 根据需求调整选项：
   - **覆盖已存在的同名集合**：勾选后每次扫描会完全重建集合。日常使用建议不勾选，走增量同步。
   - **自动删除不再需要的集合**：当媒体库中删除文件夹后，自动清理对应的集合。
   - **媒体库扫描完成后自动同步集合**：Jellyfin 扫描媒体库后自动触发插件。
   - **集合名最大字符长度**：超过后使用哈希后缀。推荐 80。
   - **标签提取开关**：按需启用 4 种标签格式。
3. 点击"保存配置"。

### 2. 触发扫描

两种方式：

- **手动**：在插件设置页面点击"立即扫描"。
- **自动**：Jellyfin 媒体库扫描结束后自动触发（需在设置中开启）。

### 3. 查看结果

在 Jellyfin 首页 → **集合** 页面可以看到生成的集合。

> 💡 **提示**：集合页面的前端缓存有 10-30 秒延迟，Ctrl+R 可立即刷新。

---

## 📁 目录结构示例

假设媒体库根路径为 `D:\Videos`，结构如下：

```
D:\Videos\
├── 电影\
│   ├── 科幻\
│   │   ├── 星际穿越.mp4
│   │   └── 盗梦空间.mp4
│   └── 动作\
│       └── 复仇者联盟.mp4
└── 教程\
    └── 【Python】入门\
        ├── 01-基础.mp4
        └── 02-进阶.mp4
```

插件会生成以下集合：

| 集合名 | 包含媒体 |
|--------|---------|
| `电影-科幻` | 星际穿越、盗梦空间 |
| `电影-动作` | 复仇者联盟 |
| `教程-【Python】入门` | 01-基础、02-进阶 |

标签提取结果：

| 集合 | 标签 |
|------|------|
| `电影-科幻` | `FolderCollections` |
| `电影-动作` | `FolderCollections` |
| `教程-【Python】入门` | `FolderCollections`、`Python` |

---

## ⚙️ 技术细节

### 架构

- **依赖注入**：使用 `IPluginServiceRegistrator` 注册服务。
- **媒体库查询**：分批查询 `InternalItemsQuery`，避免大规模媒体库的截断问题。
- **路径匹配**：根据 `PhysicalLocations` 前缀匹配媒体项，不依赖 `ParentId` 关系，稳定性更高。
- **元数据持久化**：使用 `BaseItem.UpdateToRepositoryAsync` 保存标签、锁定状态、封面。
- **集合成员读取**：使用 `Folder.GetLinkedChildren()` 读取集合成员（Jellyfin 10.x 标准方式）。

### 集合识别标记

插件创建的集合会带上以下标记，用于区分手动创建的集合：

- **标签**：`FolderCollections`
- **简介**：包含 `[FolderCollections]` 前缀
- **兜底规则**：集合名包含 `-` 且长度 > 8（用于兼容早期版本创建的集合）

清理逻辑只处理带这些标记的集合，不会误删手动创建的集合。

### 关键类

| 类 | 作用 |
|----|------|
| `Plugin` | 插件主类 |
| `PluginConfiguration` | 配置数据模型 |
| `FolderCollectionService` | 核心扫描与集合生成逻辑 |
| `FolderCollectionPostScanTask` | 媒体库扫描后置任务 |
| `FolderCollectionsController` | REST API 端点（手动触发扫描） |
| `PluginServiceRegistrator` | 依赖注入注册 |

### REST API

| 端点 | 方法 | 说明 |
|------|------|------|
| `/FolderCollections/Scan` | POST | 触发一次扫描 |

---

## 🪵 日志说明

插件日志前缀为 `Jellyfin.Plugin.FolderCollections.Services.FolderCollectionService`。

### 典型日志

```
开始扫描文件夹集合...
第 1 批获取 5000 条（新增 5000 条，去重后累计 5000 条）。
...
全库共查询到 78398 个候选媒体项（按 Id 去重后），准备按媒体库路径筛选...
媒体库 "家庭视频和照片" 物理位置: [D:\Videos]
正在处理媒体库: "家庭视频和照片"
媒体库 "家庭视频和照片" 共匹配到 78398 个媒体项。
媒体库 "家庭视频和照片" 扫描完成：共生成 457 个集合分组。
  集合 "电影-科幻" ← 2 个媒体项，标签: [FolderCollections]
  集合 "教程-【Python】入门" ← 2 个媒体项，标签: [FolderCollections, Python]
...
开始检查过时集合...
删除过时集合: 电影-旧分类
过时集合清理完成，共删除 1 个集合。
本次扫描统计：新增集合 0 个，更新集合 5 个，删除集合 1 个，未变化 451 个，共涉及 2928 个媒体项。
所有媒体库扫描完成。
```

---

## 🐛 故障排查

### 集合显示 `NotSupported`

- 确认 DLL 是从 `bin/Release/net8.0/` 编译的（而不是 `net10.0`）。
- 确认 `meta.json` 中 `targetAbi` 为 `10.10.7.0`。

### 集合数量远小于预期

- 检查日志中 `媒体库 "..." 共匹配到 X 个媒体项` 的数字，与数据库中该媒体库的媒体项数量对比。
- 使用 SQLite 工具（如 DB Browser for SQLite）打开 `library.db`，执行：
  ```sql
  SELECT COUNT(*) FROM TypedBaseItems 
  WHERE Path LIKE '%你的文件夹名%';
  ```

### 集合没有封面或标签

- 确认视频的缩略图已生成（Jellyfin 需要时间提取）。
- 前端缓存问题，Ctrl+R 刷新。
- 检查日志中是否有 `已同步并锁定集合` 的输出。

### 扫描耗时长

- 30TB 媒体库首次扫描可能需要几分钟到几十分钟，属正常现象。
- 第二次及以后的扫描会快很多（增量同步）。
- 关闭"媒体库扫描后自动同步"可以减少后台负载。

---

## 📄 许可证

MIT License — 详见 [LICENSE](LICENSE)。

---

## 🤝 贡献

欢迎提交 Issue 和 Pull Request。

### 开发环境

- .NET 8.0 SDK
- Visual Studio 2022 / VS Code + C# Dev Kit
- Jellyfin 10.10.7 测试服务器

### 本地测试

```bash
dotnet build
# 把 bin/Debug/net8.0/ 下的 DLL 复制到 Jellyfin 插件目录
# 重启 Jellyfin 查看效果
```

---

## 🙏 致谢

- [Jellyfin](https://jellyfin.org) — 开源的媒体服务器
- 所有贡献者和使用者
```
