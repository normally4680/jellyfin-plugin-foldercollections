using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    /// 用于从文件夹名中提取 #XX 标记的正则.
    /// 匹配 # 后面所有非空白、非 # 的字符（支持括号、减号、点号等）.
    /// </summary>
    private static readonly Regex TagPattern = new(@"#([^\s#]+)", RegexOptions.Compiled);

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

        var allCollectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 全局统计（跨媒体库）
        int totalCreated = 0;
        int totalUpdated = 0;
        int totalUnchanged = 0;
        int totalMediaItems = 0;

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

            _logger.LogInformation("正在扫描媒体库: {LibraryName}", library.Name);

            var query = new InternalItemsQuery
            {
                ParentId = libraryId,
                Recursive = true,
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Episode,
                    BaseItemKind.Video,
                    BaseItemKind.Photo,
                    BaseItemKind.MusicVideo,
                    BaseItemKind.Audio,
                    BaseItemKind.Trailer
                }
            };

            var items = _libraryManager.GetItemList(query);
            _logger.LogInformation("媒体库 \"{LibraryName}\" 共查询到 {Count} 个媒体项。", library.Name, items.Count);

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

                // 从所有层级的文件夹名中提取 #XX 标签
                if (folderRelative.Contains('#', StringComparison.Ordinal))
                {
                    if (!groupTags.TryGetValue(collectionName, out var tagSet))
                    {
                        tagSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        groupTags[collectionName] = tagSet;
                    }

                    foreach (var part in parts)
                    {
                        foreach (Match m in TagPattern.Matches(part))
                        {
                            if (m.Groups.Count > 1 && !string.IsNullOrEmpty(m.Groups[1].Value))
                            {
                                tagSet.Add(m.Groups[1].Value);
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

        // 清理过时集合并获取删除数量
        int totalRemoved = 0;
        if (config.RemoveObsoleteCollections)
        {
            totalRemoved = await CleanupObsoleteCollectionsAsync(allCollectionNames).ConfigureAwait(false);
        }

        // 输出汇总（含删除）
        _logger.LogInformation(
            "本次扫描统计：新增集合 {Created} 个，删除集合 {Removed} 个，更新集合 {Updated} 个，未变化 {Unchanged} 个，共涉及 {MediaCount} 个媒体项。",
            totalCreated,
            totalRemoved,
            totalUpdated,
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

        using var md5 = MD5.Create();
        var hashBytes = md5.ComputeHash(Encoding.UTF8.GetBytes(name));
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

                await TrySetCollectionCoverAsync(existing, itemIds).ConfigureAwait(false);
                await SyncCollectionMetadataAsync(existing, customTags).ConfigureAwait(false);

                _logger.LogInformation("集合 \"{Name}\" 重建完成，现包含 {Count} 个媒体项。", collectionName, targetItems.Count);
                return CollectionOperationResult.Updated;
            }
            else
            {
                var toAdd = targetItems.Except(existingItems).ToArray();
                var toRemove = existingItems.Except(targetItems).ToArray();

                if (toAdd.Length == 0 && toRemove.Length == 0)
                {
                    // 内容无变化，但仍尝试更新封面和元数据（补齐历史遗留的标记）
                    await TrySetCollectionCoverAsync(existing, itemIds).ConfigureAwait(false);
                    await SyncCollectionMetadataAsync(existing, customTags).ConfigureAwait(false);

                    return CollectionOperationResult.Unchanged;
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

                await TrySetCollectionCoverAsync(existing, itemIds).ConfigureAwait(false);
                await SyncCollectionMetadataAsync(existing, customTags).ConfigureAwait(false);

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

            var newCollection = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Name = collectionName,
                Recursive = true
            }).FirstOrDefault();

            if (newCollection != null)
            {
                await TrySetCollectionCoverAsync(newCollection, itemIds).ConfigureAwait(false);
                await SyncCollectionMetadataAsync(newCollection, customTags).ConfigureAwait(false);
            }

            _logger.LogInformation("创建新集合: {Name}，包含 {Count} 个媒体项", collectionName, itemIds.Count);
            return CollectionOperationResult.Created;
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

        // BoxSet 继承自 Folder，需要转型后才能调用 GetLinkedChildren
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

        // 回退方式
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

    /// <summary>
    /// 同步集合的元数据：标签、Overview 标记、锁定状态.
    /// 只在有实际变化时写数据库.
    /// </summary>
    /// <param name="collection">集合对象.</param>
    /// <param name="customTags">从文件夹中提取的自定义标签.</param>
    /// <returns>异步任务.</returns>
    private async Task SyncCollectionMetadataAsync(BaseItem collection, HashSet<string> customTags)
    {
        bool needsUpdate = false;

        // 1. 计算目标标签集合（FolderCollections 标记 + 自定义标签）
        var currentTags = collection.Tags?.ToList() ?? new List<string>();

        var targetTags = new List<string> { PluginMarkerTag };
        foreach (var tag in customTags)
        {
            if (!targetTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                targetTags.Add(tag);
            }
        }

        // 判断标签是否变化（顺序无关）
        var currentSorted = currentTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        var targetSorted = targetTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();

        if (!currentSorted.SequenceEqual(targetSorted, StringComparer.OrdinalIgnoreCase))
        {
            collection.Tags = targetTags.ToArray();
            needsUpdate = true;
        }

        // 2. Overview 标记
        var overview = collection.Overview ?? string.Empty;
        if (!overview.Contains(PluginMarkerOverview, StringComparison.Ordinal))
        {
            collection.Overview = string.IsNullOrEmpty(overview)
                ? PluginMarkerOverview
                : PluginMarkerOverview + " " + overview;
            needsUpdate = true;
        }

        // 3. 锁定状态
        if (!collection.IsLocked)
        {
            collection.IsLocked = true;
            needsUpdate = true;
        }

        if (!needsUpdate)
        {
            return;
        }

        await _libraryManager.UpdateItemAsync(
            collection,
            null!,
            ItemUpdateType.MetadataEdit,
            CancellationToken.None).ConfigureAwait(false);

        _logger.LogInformation("已同步并锁定集合 \"{Name}\" 的元数据。", collection.Name);
    }

    /// <summary>
    /// 尝试为集合设置封面：取第一个有主图的媒体项图片作为集合封面.
    /// 如果集合已有封面，不会覆盖.
    /// </summary>
    /// <param name="collection">集合对象.</param>
    /// <param name="itemIds">集合内的媒体项 ID 列表（按顺序，第一个有封面的会被选中）.</param>
    /// <returns>异步任务.</returns>
    private async Task TrySetCollectionCoverAsync(BaseItem collection, List<Guid> itemIds)
    {
        // 如果集合已有 Primary 图，跳过
        if (collection.HasImage(ImageType.Primary, 0))
        {
            return;
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

                await _libraryManager.UpdateItemAsync(
                    collection,
                    null!,
                    ItemUpdateType.ImageUpdate,
                    CancellationToken.None).ConfigureAwait(false);

                _logger.LogInformation(
                    "已为集合 \"{Name}\" 设置封面（来源：{Source}）",
                    collection.Name,
                    item.Name);

                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "为集合 \"{Name}\" 设置封面时出错。", collection.Name);
            }
        }
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
