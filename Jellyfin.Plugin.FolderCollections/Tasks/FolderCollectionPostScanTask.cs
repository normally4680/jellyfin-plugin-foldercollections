using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FolderCollections.Services;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FolderCollections.Tasks;

/// <summary>
/// 媒体库扫描完成后自动同步文件夹集合.
/// </summary>
public class FolderCollectionPostScanTask : ILibraryPostScanTask
{
    private readonly FolderCollectionService _service;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<FolderCollectionPostScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderCollectionPostScanTask"/> class.
    /// </summary>
    /// <param name="service">文件夹集合服务.</param>
    /// <param name="pluginManager">插件管理器.</param>
    /// <param name="logger">日志记录器.</param>
    public FolderCollectionPostScanTask(
        FolderCollectionService service,
        IPluginManager pluginManager,
        ILogger<FolderCollectionPostScanTask> logger)
    {
        _service = service;
        _pluginManager = pluginManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // 每次运行时通过 IPluginManager 读取最新配置
        var localPlugin = _pluginManager.GetPlugin(Plugin.StaticId);
        if (localPlugin?.Instance is not Plugin plugin)
        {
            _logger.LogWarning("插件未加载，跳过媒体库扫描后置任务。");
            return;
        }

        var config = plugin.Configuration;
        if (!config.AutoSyncAfterLibraryScan)
        {
            return;
        }

        _logger.LogInformation("媒体库扫描完成，自动触发文件夹集合同步...");

        try
        {
            await _service.ScanAndCreateCollectionsAsync(config).ConfigureAwait(false);
            _logger.LogInformation("文件夹集合自动同步完成。");
        }
        catch (Exception ex)
        {
            // 不要让异常阻断 Jellyfin 的扫描流程
            _logger.LogError(ex, "文件夹集合自动同步时出错。");
        }

        progress.Report(100);
    }
}
