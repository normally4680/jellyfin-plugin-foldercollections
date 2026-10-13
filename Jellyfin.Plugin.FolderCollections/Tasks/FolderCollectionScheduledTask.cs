using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FolderCollections.Services;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FolderCollections.Tasks;

/// <summary>
/// Folder Collections 扫描计划任务.
/// 出现在 控制台 → 计划任务 页面，可手动触发或配置定时规则.
/// </summary>
public class FolderCollectionScheduledTask : IScheduledTask
{
    /// <summary>
    /// 任务 Key（用于日志、/Status 查询、任务查重）.
    /// </summary>
    public const string TaskKey = "FolderCollectionScan";

    private readonly FolderCollectionService _service;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<FolderCollectionScheduledTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderCollectionScheduledTask"/> class.
    /// </summary>
    /// <param name="service">文件夹集合服务.</param>
    /// <param name="pluginManager">插件管理器.</param>
    /// <param name="logger">日志记录器.</param>
    public FolderCollectionScheduledTask(
        FolderCollectionService service,
        IPluginManager pluginManager,
        ILogger<FolderCollectionScheduledTask> logger)
    {
        _service = service;
        _pluginManager = pluginManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Folder Collections 扫描";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description => "根据物理文件夹结构自动生成 Jellyfin 媒体集合，并同步标签与封面。";

    /// <inheritdoc />
    public string Category => "Folder Collections";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // 默认无自动触发：用户在 控制台 → 计划任务 页面自行添加定时规则。
        // 媒体库扫描后的自动同步由 FolderCollectionPostScanTask 处理（可通过插件配置开关）。
        return Array.Empty<TaskTriggerInfo>();
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("计划任务 \"{Name}\" 开始执行。", Name);

        var localPlugin = _pluginManager.GetPlugin(Plugin.StaticId);
        if (localPlugin?.Instance is not Plugin plugin)
        {
            _logger.LogError("无法获取插件实例，扫描中止。");
            progress?.Report(100);
            return;
        }

        var config = plugin.Configuration;

        var result = await _service
            .RunAsync(config, progress, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "计划任务 \"{Name}\" 执行结束，结果: {Result}。",
            Name,
            result);
    }
}
