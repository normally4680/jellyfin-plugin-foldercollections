using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.FolderCollections.Configuration;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FolderCollections.Services;

/// <summary>
/// 集合操作结果.
/// </summary>
public enum CollectionOperationResult
{
    /// <summary>集合内容无变化.</summary>
    Unchanged,

    /// <summary>集合被创建.</summary>
    Created,

    /// <summary>集合内容被更新（新增或移除媒体项）.</summary>
    Updated
}

/// <summary>
/// 文件夹自动集合服务.
/// </summary>
public class FolderCollectionService
{
    private const string PluginMarkerTag = "FolderCollections";
    private const string PluginMarkerOverview = "[FolderCollections]";

    /// <summary>
    /// 保存失败时最大重试次数.
    /// </summary>
    private const int SaveMaxAttempts = 3;

    /// <summary>
    /// 创建集合后等待 Jellyfin 内部首次保存完成的延迟（毫秒）.
    /// </summary>
    private const int PostCreateDelayMs = 300;

    /// <summary>
    /// 写入后等待异步保存落盘并验证的延迟（毫秒）.
    /// </summary>
    private const int PostSaveVerifyDelayMs = 150;

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<FolderCollectionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderCollectionService"/> class.
    /// </summary>
    /// <param name="libraryManager">媒体库管理器.</param>
    /// <param name="collectionManager">集合管理器.</param>
    /// <param name="logger">日志记录器.</param>
    public FolderCollectionService(
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        ILogger<FolderCollectionService> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _logger = logger;
    }

    /// <summary>
    /// 执行扫描并生成集合.
    /// </summary>
    /// <param name="config">插件配置.</param>
    /// <returns>异步任务.</returns>
    public async Task ScanAndCreateCollectionsAsync(PluginConfiguration config)
    {
        _logger.LogInformation("开始扫描文件夹集合...");

        // ========== 构建标签正则 ==========
        // # 标签单独处理，因为需要把 "整段空格分隔内容" 拆成多个标签；
        // 其它标签类型（{}、[]、【】、@）保持"一个匹配 = 一个标签"。
        Regex? hashTagPattern = null;
        if (config.TagHashEnabled)
        {
            hashTagPattern = new Regex(
                @"#([^\s#\{\[【@]+(?:\s+[^\s#\{\[【@]+)*)",
                RegexOptions.Compiled);
        }

        var otherPatterns = new List<string>();
        if (config.TagBraceEnabled)
        {
            otherPatterns.Add(@"\{([^}\s]+)\}");
        }

        if (config.TagSquareBracketEnabled)
        {
            otherPatterns.Add(@"\[([^\]\n]+)\]");
        }

        if (config.TagChineseBracketEnabled)
        {
            otherPatterns.Add(@"【([^】\n]+)】");
        }

        if (config.TagAtEnabled)
        {
            otherPatterns.Add(@"@([^\s@]+)");
        }

        Regex? otherTagPattern = null;
        if (otherPatterns.Count > 0)
        {
            otherTagPattern = new Regex(string.Join("|", otherPatterns), RegexOptions.Compiled);
        }

        var enabledFormats = new List<string>();
        if (hashTagPattern != null)
        {
            enabledFormats.Add("#(宽松)");
        }

        if (config.TagBraceEnabled)
        {
            enabledFormats.Add("{}");
        }

        if (config.TagSquareBracketEnabled)
        {
            enabledFormats.Add("[]");
        }

        if (config.TagChineseBracketEnabled)
        {
            enabledFormats.Add("【】");
        }

        if (config.TagAtEnabled)
        {
            enabledFormats.Add("@");
        }

        if (enabledFormats.Count > 0)
        {
            _logger.LogInformation("标签提取已启用，格式: {Formats}", string.Join(", ", enabledFormats));
        }
        else
        {
            _logger.LogInformation("标签提取已禁用，本次扫描不会提取任何标签。");
        }

        var allCollectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int totalCreated = 0;
        int totalUpdated = 0;
        int totalUnchanged = 0;
        int totalMediaItems = 0;

        const int BatchSize = 5000;
        var allItemsById = new Dictionary<Guid, BaseItem>();
        int offset = 0;
        int batchIndex = 0;

        while (true)
        {
            batchIndex++;

            var batchQuery = new InternalItemsQuery
            {
                Recursive = true,
                Limit = BatchSize,
                StartIndex = offset,
                EnableTotalRecordCount = false,
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Episode,
                    BaseItemKind.Video,
                    BaseItemKind.Photo,
                    BaseItemKind.MusicVideo,
                    BaseItemKind.Audio,
                    BaseItemKind.Trailer
                },
                OrderBy = new[]
                {
                    (ItemSortBy.SortName, SortOrder.Ascending)
                }
            };

            var batch = _libraryManager.GetItemList(batchQuery);

            if (batch.Count == 0)
            {
                _logger.LogInformation("第 {Index} 批返回 0 条，全库查询结束。累计 {Total} 条。", batchIndex, allItemsById.Count);
                break;
            }

            int newCount = 0;
            foreach (var item in batch)
            {
                if (!allItemsById.ContainsKey(item.Id))
                {
                    allItemsById[item.Id] = item;
                    newCount++;
                }
            }

            _logger.LogInformation(
                "第 {Index} 批获取 {BatchCount} 条（新增 {NewCount} 条，去重后累计 {Total} 条）。",
                batchIndex,
                batch.Count,
                newCount,
                allItemsById.Count);

            if (batch.Count < BatchSize)
            {
                _logger.LogInformation("最后一批不足 {BatchSize} 条，全库查询结束。累计 {Total} 条。", BatchSize, allItemsById.Count);
                break;
            }

            offset += BatchSize;

            if (batchIndex > 10000)
            {
                _logger.LogWarning("已查询 {BatchCount} 批仍未结束，可能存在分页异常，强制中止。", batchIndex);
                break;
            }
        }

        var allItems = allItemsById.Values.ToList();
        _logger.LogInformation("全库共查询到 {Count} 个候选媒体项（按 Id 去重后），准备按媒体库路径筛选...", allItems.Count);

        foreach (var libraryId in config.SelectedLibraryIds)
        {
            var library = _libraryManager.GetItemById(libraryId) as CollectionFolder;
            if (library == null)
            {
                _logger.LogWarning("未找到媒体库: {LibraryId}", libraryId);
                continue;
            }

            var locations = library.PhysicalLocations ?? Array.Empty<string>();
            _logger.LogInformation(
                "媒体库 \"{LibraryName}\" 物理位置: [{Locations}]",
                library.Name,
                string.Join("], [", locations));

            if (locations.Length == 0)
            {
                _logger.LogWarning("媒体库 \"{LibraryName}\" 没有有效的物理路径，跳过。", library.Name);
                continue;
            }

            _logger.LogInformation("正在处理媒体库: {LibraryName}", library.Name);

            var items = new List<BaseItem>();
            foreach (var item in allItems)
            {
                if (string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                foreach (var location in locations)
                {
                    if (item.Path.StartsWith(location, StringComparison.OrdinalIgnoreCase))
                    {
                        items.Add(item);
                        break;
                    }
                }
            }

            _logger.LogInformation("媒体库 \"{LibraryName}\" 共匹配到 {Count} 个媒体项。", library.Name, items.Count);

            var groups = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            var groupTags = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Path))
                {
                    continue;
                }

                string? relativePath = null;
                foreach (var location in locations)
                {
                    if (item.Path.StartsWith(location, StringComparison.OrdinalIgnoreCase))
                    {
                        relativePath = Path.GetRelativePath(location, item.Path);
                        break;
                    }
                }

                if (string.IsNullOrEmpty(relativePath))
                {
                    continue;
                }

                var folderRelative = Path.GetDirectoryName(relativePath);
                if (string.IsNullOrEmpty(folderRelative))
                {
                    continue;
                }

                var parts = folderRelative.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 0)
                {
                    continue;
                }

                var fullName = string.Join("-", parts);
                var collectionName = TruncateWithHash(fullName, config.MaxNameLength);

                if (!groups.TryGetValue(collectionName, out var itemIds))
                {
                    itemIds = new List<Guid>();
                    groups[collectionName] = itemIds;
                }

                if (!itemIds.Contains(item.Id))
                {
                    itemIds.Add(item.Id);
                }

                if (!groupTags.TryGetValue(collectionName, out var tagSet))
                {
                    tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    groupTags[collectionName] = tagSet;
                }

                foreach (var part in parts)
                {
                    if (hashTagPattern != null)
                    {
                        foreach (Match m in hashTagPattern.Matches(part))
                        {
                            if (!m.Groups[1].Success)
                            {
                                continue;
                            }

                            var raw = m.Groups[1].Value;
                            var tokens = raw.Split(
                                new[] { ' ', '\t' },
                                StringSplitOptions.RemoveEmptyEntries);

                            foreach (var token in tokens)
                            {
                                tagSet.Add(token);
                            }
                        }
                    }

                    if (otherTagPattern != null)
                    {
                        foreach (Match m in otherTagPattern.Matches(part))
                        {
                            for (int g = 1; g < m.Groups.Count; g++)
                            {
                                if (m.Groups[g].Success && !string.IsNullOrEmpty(m.Groups[g].Value))
                                {
                                    tagSet.Add(m.Groups[g].Value);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            _logger.LogInformation(
                "媒体库 \"{LibraryName}\" 扫描完成：共生成 {GroupCount} 个集合分组。",
                library.Name,
                groups.Count);

            foreach (var kvp in groups)
            {
                allCollectionNames.Add(kvp.Key);

                var tags = groupTags.TryGetValue(kvp.Key, out var tagSet)
                    ? tagSet
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                _logger.LogInformation(
                    "  集合 \"{Name}\" ← {Count} 个媒体项，标签: [{Tags}]",
                    kvp.Key,
                    kvp.Value.Count,
                    string.Join(", ", tags));

                var result = await CreateOrUpdateCollectionAsync(
                    kvp.Key, kvp.Value, tags, config.OverwriteExisting).ConfigureAwait(false);

                totalMediaItems += kvp.Value.Count;

                switch (result)
                {
                    case CollectionOperationResult.Created:
                        totalCreated++;
                        break;
                    case CollectionOperationResult.Updated:
                        totalUpdated++;
                        break;
                    default:
                        totalUnchanged++;
                        break;
                }
            }
        }

        int totalRemoved = 0;
        if (config.RemoveObsoleteCollections)
        {
            totalRemoved = await CleanupObsoleteCollectionsAsync(allCollectionNames).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "本次扫描统计：新增集合 {Created} 个，更新集合 {Updated} 个，删除集合 {Removed} 个，未变化 {Unchanged} 个，共涉及 {MediaCount} 个媒体项。",
            totalCreated,
            totalUpdated,
            totalRemoved,
            totalUnchanged,
            totalMediaItems);

        _logger.LogInformation("所有媒体库扫描完成。");
    }

    private static string TruncateWithHash(string name, int maxLength)
    {
        if (maxLength <= 0 || name.Length <= maxLength)
        {
            return name;
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        var hashStr = Convert.ToHexString(hashBytes).ToLowerInvariant().Substring(0, 8);

        var suffixLen = hashStr.Length + 1;
        var prefixLen = Math.Max(1, maxLength - suffixLen);
        var prefix = name.Substring(0, prefixLen);

        return prefix + "-" + hashStr;
    }

    private async Task<CollectionOperationResult> CreateOrUpdateCollectionAsync(
        string collectionName,
        List<Guid> itemIds,
        HashSet<string> customTags,
        bool overwrite)
    {
        var existing = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Name = collectionName,
            Recursive = true
        }).FirstOrDefault();

        if (existing != null)
        {
            var existingItems = GetCollectionItemIds(existing);
            var targetItems = itemIds.ToHashSet();

            if (overwrite)
            {
                _logger.LogInformation("正在重建集合: {Name}", collectionName);

                if (existingItems.Count > 0)
                {
                    await _collectionManager.RemoveFromCollectionAsync(existing.Id, existingItems.ToArray()).ConfigureAwait(false);
                }

                await _collectionManager.AddToCollectionAsync(existing.Id, targetItems.ToArray()).ConfigureAwait(false);

                // 成员变更会触发 Jellyfin 内部的异步元数据保存，稍作等待避免文件竞争
                await Task.Delay(PostCreateDelayMs).ConfigureAwait(false);

                await SaveCollectionWithRetryAsync(existing, customTags, itemIds, force: true).ConfigureAwait(false);

                _logger.LogInformation("集合 \"{Name}\" 重建完成，现包含 {Count} 个媒体项。", collectionName, targetItems.Count);
                return CollectionOperationResult.Updated;
            }
            else
            {
                var toAdd = targetItems.Except(existingItems).ToArray();
                var toRemove = existingItems.Except(targetItems).ToArray();

                if (toAdd.Length == 0 && toRemove.Length == 0)
                {
                    // 成员无变化：未触发 Jellyfin 内部刷新，非强制模式即可（避免白写数据库）
                    bool changed = await SaveCollectionWithRetryAsync(existing, customTags, itemIds, force: false).ConfigureAwait(false);
                    return changed ? CollectionOperationResult.Updated : CollectionOperationResult.Unchanged;
                }

                _logger.LogInformation(
                    "正在同步集合 \"{Name}\"：新增 {Add} 个，移除 {Remove} 个。",
                    collectionName,
                    toAdd.Length,
                    toRemove.Length);

                if (toRemove.Length > 0)
                {
                    await _collectionManager.RemoveFromCollectionAsync(existing.Id, toRemove).ConfigureAwait(false);
                }

                if (toAdd.Length > 0)
                {
                    await _collectionManager.AddToCollectionAsync(existing.Id, toAdd).ConfigureAwait(false);
                }

                // 成员变更会触发 Jellyfin 内部的异步元数据保存，稍作等待
                await Task.Delay(PostCreateDelayMs).ConfigureAwait(false);

                await SaveCollectionWithRetryAsync(existing, customTags, itemIds, force: true).ConfigureAwait(false);

                _logger.LogInformation("集合 \"{Name}\" 同步完成，现包含 {Count} 个媒体项。", collectionName, targetItems.Count);
                return CollectionOperationResult.Updated;
            }
        }
        else
        {
            _logger.LogInformation("正在创建集合: {Name}", collectionName);

            var options = new CollectionCreationOptions
            {
                Name = collectionName,
                ItemIdList = itemIds.Select(id => id.ToString("N")).ToList()
            };

            var result = await _collectionManager.CreateCollectionAsync(options).ConfigureAwait(false);

            if (result == null)
            {
                _logger.LogWarning("集合 \"{Name}\" 创建返回空结果。", collectionName);
                return CollectionOperationResult.Unchanged;
            }

            // 关键：等待 Jellyfin 内部 CreateCollectionAsync 触发的异步 XML 保存完成。
            // 否则插件的 UpdateToRepositoryAsync 会和它竞争同一个 collection.xml 文件，
            // 导致我们的 Tags/LockData/Images 写入被 IOException 阻塞而丢失。
            await Task.Delay(PostCreateDelayMs).ConfigureAwait(false);

            var newCollection = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Name = collectionName,
                Recursive = true
            }).FirstOrDefault();

            if (newCollection != null)
            {
                await SaveCollectionWithRetryAsync(newCollection, customTags, itemIds, force: false).ConfigureAwait(false);
            }

            _logger.LogInformation("创建新集合: {Name}，包含 {Count} 个媒体项", collectionName, itemIds.Count);
            return CollectionOperationResult.Created;
        }
    }

    /// <summary>
    /// 在内存中应用元数据变更和封面，然后一次性写库并验证.
    /// 通过合并"元数据更新"和"封面更新"为单次 UpdateToRepositoryAsync，
    /// 并检查 collection.xml 的 LockData 是否已持久化，失败时自动重试，
    /// 从而避免 Jellyfin 内部异步保存造成的文件竞争。
    /// </summary>
    /// <param name="collection">集合对象.</param>
    /// <param name="customTags">从文件夹中提取的自定义标签.</param>
    /// <param name="itemIds">集合内媒体项 ID 列表.</param>
    /// <param name="force">是否强制重写（忽略内存缓存判断）.</param>
    /// <returns>是否有变更被应用并保存.</returns>
    private async Task<bool> SaveCollectionWithRetryAsync(
        BaseItem collection,
        HashSet<string> customTags,
        List<Guid> itemIds,
        bool force)
    {
        // ---------- 1. 内存中应用所有变更 ----------
        bool metadataChanged = ApplyMetadataInMemory(collection, customTags, force);
        bool coverChanged = ApplyCoverInMemory(collection, itemIds, force);

        if (!metadataChanged && !coverChanged)
        {
            return false;
        }

        var xmlPath = GetCollectionXmlPath(collection);

        // ---------- 2. 一次 Update + 验证 + 重试 ----------
        for (int attempt = 1; attempt <= SaveMaxAttempts; attempt++)
        {
            try
            {
                await collection.UpdateToRepositoryAsync(
                    ItemUpdateType.MetadataEdit,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "集合 \"{Name}\" 更新元数据抛出异常（第 {Attempt}/{Max} 次）",
                    collection.Name,
                    attempt,
                    SaveMaxAttempts);
            }

            // 等待 Jellyfin 的异步保存落盘
            await Task.Delay(PostSaveVerifyDelayMs).ConfigureAwait(false);

            if (IsCollectionXmlPersisted(xmlPath))
            {
                _logger.LogInformation("已保存并验证集合 \"{Name}\" 的元数据与封面。", collection.Name);
                return true;
            }

            _logger.LogWarning(
                "集合 \"{Name}\" 保存验证未通过（第 {Attempt}/{Max} 次），准备重试...",
                collection.Name,
                attempt,
                SaveMaxAttempts);

            // 退避等待，给 Jellyfin 内部保存让路
            if (attempt < SaveMaxAttempts)
            {
                await Task.Delay(200 * attempt).ConfigureAwait(false);
            }
        }

        _logger.LogError(
            "集合 \"{Name}\" 元数据保存失败，已重试 {Max} 次仍不成功。",
            collection.Name,
            SaveMaxAttempts);
        return false;
    }

    /// <summary>
    /// 在内存中应用元数据（标签、Overview、锁定）.
    /// </summary>
    /// <param name="collection">集合对象.</param>
    /// <param name="customTags">自定义标签.</param>
    /// <param name="force">是否强制重写.</param>
    /// <returns>是否发生了变更.</returns>
    private bool ApplyMetadataInMemory(BaseItem collection, HashSet<string> customTags, bool force)
    {
        bool needsUpdate = false;

        var currentTags = force
            ? new List<string>()
            : (collection.Tags?.ToList() ?? new List<string>());

        var targetTags = new List<string> { PluginMarkerTag };
        foreach (var tag in customTags)
        {
            if (!targetTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                targetTags.Add(tag);
            }
        }

        var currentSorted = currentTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        var targetSorted = targetTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

        if (force || !currentSorted.SequenceEqual(targetSorted, StringComparer.OrdinalIgnoreCase))
        {
            collection.Tags = targetTags.ToArray();
            needsUpdate = true;
        }

        var overview = collection.Overview ?? string.Empty;
        if (!overview.Contains(PluginMarkerOverview, StringComparison.Ordinal))
        {
            collection.Overview = string.IsNullOrEmpty(overview)
                ? PluginMarkerOverview
                : PluginMarkerOverview + " " + overview;
            needsUpdate = true;
        }

        if (!collection.IsLocked)
        {
            collection.IsLocked = true;
            needsUpdate = true;
        }

        return needsUpdate;
    }

    /// <summary>
    /// 在内存中应用封面（取第一个有主图的媒体项）.
    /// </summary>
    /// <param name="collection">集合对象.</param>
    /// <param name="itemIds">集合内媒体项 ID 列表.</param>
    /// <param name="force">是否强制重写.</param>
    /// <returns>是否发生了变更.</returns>
    private bool ApplyCoverInMemory(BaseItem collection, List<Guid> itemIds, bool force)
    {
        if (!force && collection.HasImage(ImageType.Primary, 0))
        {
            return false;
        }

        foreach (var id in itemIds)
        {
            var item = _libraryManager.GetItemById(id);
            if (item == null)
            {
                continue;
            }

            var imageInfo = item.GetImageInfo(ImageType.Primary, 0);
            if (imageInfo == null || string.IsNullOrEmpty(imageInfo.Path))
            {
                continue;
            }

            try
            {
                collection.SetImage(
                    new ItemImageInfo
                    {
                        Path = imageInfo.Path,
                        Type = ImageType.Primary,
                        DateModified = DateTime.UtcNow,
                        Width = imageInfo.Width,
                        Height = imageInfo.Height
                    },
                    0);

                _logger.LogInformation(
                    "已为集合 \"{Name}\" 预设置封面（来源：{Source}）",
                    collection.Name,
                    item.Name);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "为集合 \"{Name}\" 设置封面时出错。", collection.Name);
            }
        }

        return false;
    }

    /// <summary>
    /// 获取集合的 collection.xml 完整路径.
    /// </summary>
    /// <param name="collection">集合对象.</param>
    /// <returns>XML 路径；无法确定时返回 null.</returns>
    private static string? GetCollectionXmlPath(BaseItem collection)
    {
        if (string.IsNullOrEmpty(collection.Path))
        {
            return null;
        }

        return Path.Combine(collection.Path, "collection.xml");
    }

    /// <summary>
    /// 检查 collection.xml 是否已将 LockData 持久化为 true.
    /// </summary>
    /// <param name="xmlPath">XML 文件路径.</param>
    /// <returns>是否已持久化.</returns>
    private static bool IsCollectionXmlPersisted(string? xmlPath)
    {
        if (xmlPath == null)
        {
            // 无法验证，视为成功（避免死循环）
            return true;
        }

        if (!File.Exists(xmlPath))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(xmlPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var doc = XDocument.Load(fs);
            var lockDataElement = doc.Root?.Element("LockData");

            return lockDataElement != null
                && string.Equals(lockDataElement.Value, "true", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            // 文件被占用，说明保存进行中
            return false;
        }
        catch (Exception)
        {
            // 解析失败，视为未通过验证
            return false;
        }
    }

    /// <summary>
    /// 获取集合中当前包含的媒体项 ID（通过 LinkedChildren 读取，兼容 Jellyfin 10.x）.
    /// </summary>
    /// <param name="collection">集合对象.</param>
    /// <returns>媒体项 ID 集合.</returns>
    private HashSet<Guid> GetCollectionItemIds(BaseItem collection)
    {
        var result = new HashSet<Guid>();

        if (collection is Folder folder)
        {
            try
            {
                var children = folder.GetLinkedChildren();
                if (children != null)
                {
                    foreach (var child in children)
                    {
                        if (child != null)
                        {
                            result.Add(child.Id);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "通过 GetLinkedChildren 读取集合 \"{Name}\" 成员失败，回退到 ParentId 查询。", collection.Name);
            }
        }

        if (result.Count > 0)
        {
            return result;
        }

        try
        {
            var ids = _libraryManager.GetItemIds(new InternalItemsQuery
            {
                ParentId = collection.Id,
                Recursive = true
            });

            foreach (var id in ids)
            {
                result.Add(id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通过 ParentId 读取集合 \"{Name}\" 成员失败。", collection.Name);
        }

        return result;
    }

    private static bool IsPluginCollection(BaseItem item)
    {
        if (item.Tags != null && item.Tags.Any(t => string.Equals(t, PluginMarkerTag, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.Overview) &&
            item.Overview.Contains(PluginMarkerOverview, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(item.Name) &&
            item.Name.Contains('-', StringComparison.Ordinal) &&
            item.Name.Length > 8)
        {
            return true;
        }

        return false;
    }

    private async Task<int> CleanupObsoleteCollectionsAsync(HashSet<string> currentNames)
    {
        _logger.LogInformation("开始检查过时集合...");

        var allBoxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true
        });

        var removedCount = 0;

        foreach (var boxSet in allBoxSets)
        {
            if (!IsPluginCollection(boxSet))
            {
                continue;
            }

            if (currentNames.Contains(boxSet.Name))
            {
                continue;
            }

            _logger.LogInformation("删除过时集合: {Name}", boxSet.Name);

            try
            {
                var deleteOptions = new DeleteOptions
                {
                    DeleteFileLocation = true
                };

                _libraryManager.DeleteItem(boxSet, deleteOptions, true);
                removedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除集合 \"{Name}\" 时出错。", boxSet.Name);
            }

            await Task.Yield();
        }

        _logger.LogInformation("过时集合清理完成，共删除 {Count} 个集合。", removedCount);
        return removedCount;
    }
}
