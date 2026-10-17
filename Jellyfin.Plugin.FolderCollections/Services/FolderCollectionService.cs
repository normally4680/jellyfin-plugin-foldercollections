using System;
using System.Collections.Generic;
using System.Globalization;
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
/// 文件夹自动集合服务. 同时负责扫描的并发控制、状态汇报与进度上报.
/// </summary>
public class FolderCollectionService : IDisposable
{
    private const string PluginMarkerTag = "FolderCollections";
    private const string PluginMarkerOverview = "[FolderCollections]";

    /// <summary>保存失败时最大重试次数.</summary>
    private const int SaveMaxAttempts = 3;

    /// <summary>创建集合后等待 Jellyfin 内部首次保存完成的延迟（毫秒）.</summary>
    private const int PostCreateDelayMs = 300;

    /// <summary>写入后等待异步保存落盘并验证的延迟（毫秒）.</summary>
    private const int PostSaveVerifyDelayMs = 150;

    /// <summary>进度条节流（毫秒），避免刷屏.</summary>
    private const int ProgressThrottleMs = 500;

    /// <summary>日志节流（毫秒），避免刷屏.</summary>
    private const int LogThrottleMs = 500;

    // 阶段权重（合计 100）
    private const double Phase1End = 20.0;  // 全库查询媒体项
    private const double Phase2End = 25.0;  // 按媒体库路径筛选
    private const double Phase3End = 35.0;  // 分组 + 标签提取
    private const double Phase4End = 95.0;  // 创建/更新集合
    private const double Phase5End = 100.0; // 清理过时集合

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger<FolderCollectionService> _logger;

    // ========== 并发控制 ==========
    private readonly SemaphoreSlim _runLock = new(1, 1);

    // ========== 运行状态 ==========
    private readonly object _stateLock = new();
    private ScanState _state = new();

    // ========== 节流时间戳 ==========
    private DateTime _lastProgressReport = DateTime.MinValue;
    private DateTime _lastLogAt = DateTime.MinValue;

    private bool _disposed;

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
    /// Gets a value indicating whether 当前是否有扫描在运行.
    /// </summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _state.IsRunning;
            }
        }
    }

    /// <summary>
    /// 获取当前扫描状态的快照.
    /// </summary>
    /// <returns>状态快照.</returns>
    public ScanState GetSnapshot()
    {
        lock (_stateLock)
        {
            return _state.Clone();
        }
    }

    /// <summary>
    /// 统一入口：加锁、初始化状态、执行核心扫描、处理取消/异常.
    /// </summary>
    /// <param name="config">插件配置.</param>
    /// <param name="progress">进度上报（可为 null）.</param>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>扫描结果.</returns>
    public async Task<ScanRunResult> RunAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("扫描已在运行中，本次触发被跳过。");
            return ScanRunResult.AlreadyRunning;
        }

        try
        {
            lock (_stateLock)
            {
                _state = new ScanState
                {
                    IsRunning = true,
                    StartedAt = DateTime.UtcNow,
                    LastMessage = "扫描已启动"
                };
            }

            _lastProgressReport = DateTime.MinValue;
            _lastLogAt = DateTime.MinValue;

            progress?.Report(0);

            try
            {
                await ExecuteScanCoreAsync(config, progress, cancellationToken).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _state.IsRunning = false;
                    _state.Progress = Phase5End;
                    _state.EndedAt = DateTime.UtcNow;
                    _state.Cancelled = cancellationToken.IsCancellationRequested;
                }

                progress?.Report(Phase5End);
                return cancellationToken.IsCancellationRequested
                    ? ScanRunResult.Cancelled
                    : ScanRunResult.Completed;
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("扫描已被取消。");
                lock (_stateLock)
                {
                    _state.IsRunning = false;
                    _state.EndedAt = DateTime.UtcNow;
                    _state.Cancelled = true;
                    _state.LastMessage = "已取消";
                }

                return ScanRunResult.Cancelled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "扫描过程中发生未处理异常。");
                lock (_stateLock)
                {
                    _state.IsRunning = false;
                    _state.EndedAt = DateTime.UtcNow;
                    _state.ErrorMessage = ex.Message;
                    _state.LastMessage = "扫描失败：" + ex.Message;
                }

                return ScanRunResult.Failed;
            }
        }
        finally
        {
            _runLock.Release();
        }
    }

    // ============================================================
    //                   核 心 扫 描 逻 辑
    // ============================================================
    private async Task ExecuteScanCoreAsync(
        PluginConfiguration config,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("========== 开始扫描文件夹集合 ==========");

        // ---------- 构建标签正则 ----------
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

        Regex? otherTagPattern = otherPatterns.Count > 0
            ? new Regex(string.Join("|", otherPatterns), RegexOptions.Compiled)
            : null;

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

        _logger.LogInformation(
            "标签提取：{Msg}",
            enabledFormats.Count > 0
                ? $"已启用，格式 {string.Join(", ", enabledFormats)}"
                : "已禁用");

        var allCollectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int totalCreated = 0;
        int totalUpdated = 0;
        int totalUnchanged = 0;
        int totalMediaItems = 0;

        // ============================================================
        // 阶段 1/5：全库分批查询媒体项
        // ============================================================
        const int BatchSize = 5000;
        var allItemsById = new Dictionary<Guid, BaseItem>();
        int offset = 0;
        int batchIndex = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) }
            };

            var batch = _libraryManager.GetItemList(batchQuery);

            if (batch.Count == 0)
            {
                _logger.LogInformation("第 {Index} 批返回 0 条，全库查询结束。", batchIndex);
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
                "第 {Index} 批获取 {BatchCount} 条（新增 {NewCount}，去重后累计 {Total}）。",
                batchIndex,
                batch.Count,
                newCount,
                allItemsById.Count);

            // 阶段 1 进度：每批 +2.5%，最多到 Phase1End
            double p1 = Math.Min(Phase1End, batchIndex * 2.5);
            ReportProgress(
                progress,
                p1,
                "1/5 查询媒体项",
                statusMessage: $"第 {batchIndex} 批，累计 {allItemsById.Count} 条");

            if (batch.Count < BatchSize)
            {
                _logger.LogInformation("最后一批不足 {BatchSize} 条，全库查询结束。", BatchSize);
                break;
            }

            offset += BatchSize;

            if (batchIndex > 10000)
            {
                _logger.LogWarning("已查询 {BatchIndex} 批仍未结束，强制中止。", batchIndex);
                break;
            }
        }

        var allItems = allItemsById.Values.ToList();
        _logger.LogInformation("全库共查询到 {Count} 个候选媒体项（按 Id 去重后）。", allItems.Count);

        ReportProgress(
            progress,
            Phase1End,
            "1/5 查询媒体项",
            statusMessage: $"全库共 {allItems.Count} 个媒体项");

        // ============================================================
        // 准备媒体库列表
        // ============================================================
        var libraries = new List<CollectionFolder>();
        foreach (var libraryId in config.SelectedLibraryIds)
        {
            if (_libraryManager.GetItemById(libraryId) is CollectionFolder lib)
            {
                libraries.Add(lib);
            }
            else
            {
                _logger.LogWarning("未找到媒体库: {LibraryId}", libraryId);
            }
        }

        if (libraries.Count == 0)
        {
            _logger.LogWarning("没有可用的媒体库，扫描结束。");
            return;
        }

        _logger.LogInformation("共 {Count} 个媒体库待处理。", libraries.Count);

        double phase4LibraryWeight = (Phase4End - Phase3End) / libraries.Count;
        int libraryIndexZero = 0;

        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            libraryIndexZero++;
            int libraryIndex = libraryIndexZero;

            var locations = library.PhysicalLocations ?? Array.Empty<string>();
            _logger.LogInformation(
                "媒体库 \"{LibraryName}\"（{Index}/{Total}）物理位置: [{Locations}]",
                library.Name,
                libraryIndex,
                libraries.Count,
                string.Join("], [", locations));

            if (locations.Length == 0)
            {
                _logger.LogWarning("媒体库 \"{LibraryName}\" 没有有效物理路径，跳过。", library.Name);
                continue;
            }

            // ============================================================
            // 阶段 2/5：按路径筛选媒体项
            // ============================================================
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

            _logger.LogInformation(
                "媒体库 \"{LibraryName}\" 匹配到 {Count} 个媒体项。",
                library.Name,
                items.Count);

            double phase2Progress = Phase1End
                + ((libraryIndex / (double)libraries.Count) * (Phase2End - Phase1End));

            ReportProgress(
                progress,
                phase2Progress,
                "2/5 筛选媒体库",
                libraryName: library.Name,
                libraryIndex: libraryIndex,
                libraryTotal: libraries.Count,
                statusMessage: $"媒体库 {libraryIndex}/{libraries.Count}：{library.Name}，匹配 {items.Count} 个");

            // ============================================================
            // 阶段 3/5：分组 + 标签提取
            // ============================================================
            var groups = new Dictionary<string, List<Guid>>(StringComparer.OrdinalIgnoreCase);
            var groupTags = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            int processedItems = 0;
            int lastReportedItems = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processedItems++;

                // 每 500 个媒体项检查一次进度
                if (processedItems - lastReportedItems >= 500)
                {
                    lastReportedItems = processedItems;
                    double subPhase = items.Count > 0 ? processedItems / (double)items.Count : 1.0;
                    double libraryPortion = (libraryIndex - 1) / (double)libraries.Count;
                    double phase3Progress = Phase2End
                        + (libraryPortion * (Phase3End - Phase2End))
                        + ((subPhase / libraries.Count) * (Phase3End - Phase2End));

                    ReportProgress(
                        progress,
                        Math.Min(phase3Progress, Phase3End),
                        "3/5 分组标签",
                        libraryName: library.Name,
                        libraryIndex: libraryIndex,
                        libraryTotal: libraries.Count,
                        statusMessage: $"媒体库 {libraryIndex}/{libraries.Count}：{library.Name}，已处理 {processedItems}/{items.Count}");
                }

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
                "媒体库 \"{LibraryName}\" 分组完成：{Count} 个集合。",
                library.Name,
                groups.Count);

            double phase3Done = Phase2End
                + ((libraryIndex / (double)libraries.Count) * (Phase3End - Phase2End));

            ReportProgress(
                progress,
                phase3Done,
                "3/5 分组标签",
                libraryName: library.Name,
                libraryIndex: libraryIndex,
                libraryTotal: libraries.Count,
                statusMessage: $"媒体库 {libraryIndex}/{libraries.Count}：{library.Name}，生成 {groups.Count} 个集合");

            // ============================================================
            // 阶段 4/5：创建/更新集合
            // ============================================================
            int collectionIndex = 0;
            int collectionTotal = groups.Count;
            double phase4Base = Phase3End + ((libraryIndex - 1) * phase4LibraryWeight);

            foreach (var kvp in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                collectionIndex++;

                allCollectionNames.Add(kvp.Key);

                var tags = groupTags.TryGetValue(kvp.Key, out var tagSet)
                    ? tagSet
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var result = await CreateOrUpdateCollectionAsync(
                    kvp.Key,
                    kvp.Value,
                    tags,
                    config.OverwriteExisting).ConfigureAwait(false);

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

                double subPhase4 = collectionTotal > 0
                    ? collectionIndex / (double)collectionTotal
                    : 1.0;
                double phase4Progress = phase4Base + (subPhase4 * phase4LibraryWeight);

                ReportProgress(
                    progress,
                    Math.Min(phase4Progress, Phase4End),
                    "4/5 创建集合",
                    libraryName: library.Name,
                    libraryIndex: libraryIndex,
                    libraryTotal: libraries.Count,
                    collectionIndex: collectionIndex,
                    collectionTotal: collectionTotal,
                    created: totalCreated,
                    updated: totalUpdated,
                    removed: 0,
                    unchanged: totalUnchanged,
                    mediaItemCount: totalMediaItems);
            }

            _logger.LogInformation(
                "媒体库 \"{LibraryName}\" 处理完成：新增 {Created} 更新 {Updated} 未变 {Unchanged}。",
                library.Name,
                totalCreated,
                totalUpdated,
                totalUnchanged);
        }

        // ============================================================
        // 阶段 5/5：清理过时集合
        // ============================================================
        ReportProgress(
            progress,
            Phase4End,
            "5/5 清理过时",
            statusMessage: "开始检查过时集合...");

        int totalRemoved = 0;
        if (config.RemoveObsoleteCollections)
        {
            totalRemoved = await CleanupObsoleteCollectionsAsync(
                allCollectionNames,
                cancellationToken).ConfigureAwait(false);
        }

        ReportProgress(
            progress,
            Phase5End,
            "5/5 清理过时",
            created: totalCreated,
            updated: totalUpdated,
            removed: totalRemoved,
            unchanged: totalUnchanged,
            mediaItemCount: totalMediaItems,
            statusMessage: $"扫描完成：新增 {totalCreated} 更新 {totalUpdated} 删除 {totalRemoved} 未变 {totalUnchanged}");

        _logger.LogInformation(
            "========== 扫描完成：新增 {Created}，更新 {Updated}，删除 {Removed}，未变化 {Unchanged}，涉及 {MediaCount} 个媒体项 ==========",
            totalCreated,
            totalUpdated,
            totalRemoved,
            totalUnchanged,
            totalMediaItems);
    }

    // ============================================================
    //                   进 度 与 状 态 上 报
    // ============================================================
    private void ReportProgress(
        IProgress<double>? progress,
        double percent,
        string phase,
        string libraryName = "",
        int libraryIndex = 0,
        int libraryTotal = 0,
        int collectionIndex = 0,
        int collectionTotal = 0,
        int created = 0,
        int updated = 0,
        int removed = 0,
        int unchanged = 0,
        int mediaItemCount = 0,
        string? statusMessage = null)
    {
        if (percent < 0)
        {
            percent = 0;
        }

        if (percent > 100)
        {
            percent = 100;
        }

        string renderedMessage;
        lock (_stateLock)
        {
            _state.Progress = percent;
            _state.Phase = phase;
            if (!string.IsNullOrEmpty(libraryName))
            {
                _state.LibraryName = libraryName;
            }

            if (libraryIndex > 0)
            {
                _state.LibraryIndex = libraryIndex;
            }

            if (libraryTotal > 0)
            {
                _state.LibraryTotal = libraryTotal;
            }

            _state.CollectionIndex = collectionIndex;
            _state.CollectionTotal = collectionTotal;
            _state.Created = created;
            _state.Updated = updated;
            _state.Removed = removed;
            _state.Unchanged = unchanged;
            _state.MediaItemCount = mediaItemCount;

            // ========== 统一拼接前缀 ==========
            if (!string.IsNullOrEmpty(statusMessage))
            {
                // 如果传了自定义消息，强制在前面加上 [x/5 阶段名]
                _state.LastMessage = $"[{phase}] {statusMessage}";
            }
            else
            {
                // 如果没有传，用默认逻辑拼接（BuildStatusMessage 内部会自带 [x/5] 前缀）
                _state.LastMessage = BuildStatusMessage(
                    phase,
                    libraryName,
                    libraryIndex,
                    libraryTotal,
                    collectionIndex,
                    collectionTotal,
                    created,
                    updated,
                    removed);
            }

            renderedMessage = _state.LastMessage;

            // ========== 写入内存日志队列 ==========
            var timeStr = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string logLine = $"[{timeStr}] [进度 {percent:F1}%] {renderedMessage}";

            // 避免完全重复的行连续出现
            if (_state.RecentLogs.Count == 0 || _state.RecentLogs[^1] != logLine)
            {
                _state.RecentLogs.Add(logLine);
                // 只保留最近 100 条
                if (_state.RecentLogs.Count > 100)
                {
                    _state.RecentLogs.RemoveAt(0);
                }
            }
        }

        var now = DateTime.UtcNow;
        if ((now - _lastLogAt).TotalMilliseconds >= LogThrottleMs
            || statusMessage != null
            || percent >= 100)
        {
            _lastLogAt = now;
            _logger.LogInformation(
                "[进度 {Percent:F1}%] {Message}",
                percent,
                renderedMessage);
        }

        if (progress != null)
        {
            if ((now - _lastProgressReport).TotalMilliseconds >= ProgressThrottleMs
                || percent >= 100)
            {
                _lastProgressReport = now;
                progress.Report(percent);
            }
        }
    }

    private static string BuildStatusMessage(
        string phase,
        string libraryName,
        int libraryIndex,
        int libraryTotal,
        int collectionIndex,
        int collectionTotal,
        int created,
        int updated,
        int removed)
    {
        var parts = new List<string> { $"[{phase}]" };

        if (!string.IsNullOrEmpty(libraryName))
        {
            parts.Add($"媒体库 {libraryIndex}/{libraryTotal}：{libraryName}");
        }

        if (collectionTotal > 0)
        {
            parts.Add($"集合 {collectionIndex}/{collectionTotal}");
        }

        if (created > 0 || updated > 0 || removed > 0)
        {
            parts.Add($"新增 {created} 更新 {updated} 删除 {removed}");
        }

        return string.Join(" | ", parts);
    }

    // ============================================================
    //                    集 合 创 建 / 更 新
    // ============================================================
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
                await Task.Delay(PostCreateDelayMs).ConfigureAwait(false);

                await SaveCollectionWithRetryAsync(existing, customTags, itemIds, force: true).ConfigureAwait(false);

                return CollectionOperationResult.Updated;
            }
            else
            {
                var toAdd = targetItems.Except(existingItems).ToArray();
                var toRemove = existingItems.Except(targetItems).ToArray();

                if (toAdd.Length == 0 && toRemove.Length == 0)
                {
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

                await Task.Delay(PostCreateDelayMs).ConfigureAwait(false);
                await SaveCollectionWithRetryAsync(existing, customTags, itemIds, force: true).ConfigureAwait(false);

                return CollectionOperationResult.Updated;
            }
        }
        else
        {
            _logger.LogInformation("正在创建集合: {Name}", collectionName);

            var options = new CollectionCreationOptions
            {
                Name = collectionName,
                ItemIdList = itemIds.Select(id => id.ToString("N", CultureInfo.InvariantCulture)).ToList()
            };

            var result = await _collectionManager.CreateCollectionAsync(options).ConfigureAwait(false);

            if (result == null)
            {
                _logger.LogWarning("集合 \"{Name}\" 创建返回空结果。", collectionName);
                return CollectionOperationResult.Unchanged;
            }

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

            return CollectionOperationResult.Created;
        }
    }

    private async Task<bool> SaveCollectionWithRetryAsync(
        BaseItem collection,
        HashSet<string> customTags,
        List<Guid> itemIds,
        bool force)
    {
        bool metadataChanged = ApplyMetadataInMemory(collection, customTags, force);
        bool coverChanged = ApplyCoverInMemory(collection, itemIds, force);

        if (!metadataChanged && !coverChanged)
        {
            return false;
        }

        var xmlPath = GetCollectionXmlPath(collection);

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

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "为集合 \"{Name}\" 设置封面时出错。", collection.Name);
            }
        }

        return false;
    }

    private static string? GetCollectionXmlPath(BaseItem collection)
    {
        if (string.IsNullOrEmpty(collection.Path))
        {
            return null;
        }

        return Path.Combine(collection.Path, "collection.xml");
    }

    private static bool IsCollectionXmlPersisted(string? xmlPath)
    {
        if (xmlPath == null)
        {
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
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string TruncateWithHash(string name, int maxLength)
    {
        if (maxLength <= 0 || name.Length <= maxLength)
        {
            return name;
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        var hashStr = Convert.ToHexString(hashBytes).ToLowerInvariant()[..8];

        var suffixLen = hashStr.Length + 1;
        var prefixLen = Math.Max(1, maxLength - suffixLen);
        var prefix = name[..prefixLen];

        return prefix + "-" + hashStr;
    }

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

    private async Task<int> CleanupObsoleteCollectionsAsync(
        HashSet<string> currentNames,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("开始检查过时集合...");

        var allBoxSets = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            Recursive = true
        });

        var removedCount = 0;
        int checkedCount = 0;

        foreach (var boxSet in allBoxSets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedCount++;

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
                var deleteOptions = new DeleteOptions { DeleteFileLocation = true };
                _libraryManager.DeleteItem(boxSet, deleteOptions, true);
                removedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除集合 \"{Name}\" 时出错。", boxSet.Name);
            }

            await Task.Yield();
        }

        _logger.LogInformation(
            "过时集合清理完成：检查 {Checked} 个，删除 {Removed} 个。",
            checkedCount,
            removedCount);
        return removedCount;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源.
    /// </summary>
    /// <param name="disposing">是否主动释放.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _runLock.Dispose();
        }

        _disposed = true;
    }
}
