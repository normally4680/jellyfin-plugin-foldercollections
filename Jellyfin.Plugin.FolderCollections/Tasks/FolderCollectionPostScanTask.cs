using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FolderCollections.Services;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FolderCollections.Tasks;

/// <summary>
/// 媒体库扫描完成后置任务：按配置自动触发文件夹集合同步.
/// 内部复用 FolderCollectionService.RunAsync，与计划任务共享并发锁.
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

    /// <summary>
    /// 媒体库扫描完成后调用.
    /// </summary>
    /// <param name="progress">进度上报（由 Jellyfin 提供，但媒体库扫描结束后通常已关闭 UI）.</param>
    /// <param name="cancellationToken">取消令牌.</param>
    /// <returns>异步任务.</returns>
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        // 从插件实例获取配置
        var localPlugin = _pluginManager.GetPlugin(Plugin.StaticId);
        if (localPlugin?.Instance is not Plugin plugin)
        {
            _logger.LogWarning("插件未加载或类型不匹配，跳过媒体库扫描后自动同步。");
            return;
        }

        var config = plugin.Configuration;

        if (!config.AutoSyncAfterLibraryScan)
        {
            // 用户关闭了自动同步，静默返回
            return;
        }

        _logger.LogInformation("媒体库扫描完成，自动触发文件夹集合同步...");

        try
        {
            // 复用统一入口：加锁 → 若已被其他入口（REST / 计划任务）占用则直接返回 AlreadyRunning
            var result = await _service
                .RunAsync(config, progress, cancellationToken)
                .ConfigureAwait(false);

            switch (result)
            {
                case ScanRunResult.AlreadyRunning:
                    _logger.LogInformation("媒体库扫描后置任务：检测到已有扫描在运行，本次自动同步被跳过。");
                    break;
                case ScanRunResult.Completed:
                    _logger.LogInformation("媒体库扫描后置任务：文件夹集合自动同步完成。");
                    break;
                case ScanRunResult.Cancelled:
                    _logger.LogInformation("媒体库扫描后置任务：文件夹集合自动同步被取消。");
                    break;
                case ScanRunResult.Failed:
                    _logger.LogWarning("媒体库扫描后置任务：文件夹集合自动同步失败。");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("媒体库扫描后置任务：文件夹集合自动同步被取消。");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "媒体库扫描后置任务：文件夹集合自动同步发生异常。");
        }
    }
}
