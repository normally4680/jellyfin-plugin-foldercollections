using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FolderCollections.Services;
using MediaBrowser.Common.Plugins;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FolderCollections;

/// <summary>
/// 启动时延迟触发一次扫描的后台服务.
/// 仅当 PluginConfiguration.ScanOnStartup = true 时生效.
/// 延迟 60 秒以避开 Jellyfin 启动初期的 IO 高峰。
/// </summary>
public class StartupScanHostedService : IHostedService
{
    /// <summary>启动后延迟触发扫描的秒数.</summary>
    private const int StartupDelaySeconds = 60;

    private readonly FolderCollectionService _service;
    private readonly IPluginManager _pluginManager;
    private readonly ILogger<StartupScanHostedService> _logger;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Initializes a new instance of the <see cref="StartupScanHostedService"/> class.
    /// </summary>
    /// <param name="service">文件夹集合服务.</param>
    /// <param name="pluginManager">插件管理器.</param>
    /// <param name="logger">日志记录器.</param>
    public StartupScanHostedService(
        FolderCollectionService service,
        IPluginManager pluginManager,
        ILogger<StartupScanHostedService> logger)
    {
        _service = service;
        _pluginManager = pluginManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    _logger.LogInformation(
                        "启动扫描服务已就绪，将在 {Seconds} 秒后检查 ScanOnStartup 配置。",
                        StartupDelaySeconds);

                    await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), _cts.Token)
                        .ConfigureAwait(false);

                    var localPlugin = _pluginManager.GetPlugin(Plugin.StaticId);
                    if (localPlugin?.Instance is not Plugin plugin)
                    {
                        _logger.LogWarning("无法获取插件实例，跳过启动时扫描。");
                        return;
                    }

                    var config = plugin.Configuration;

                    if (!config.ScanOnStartup)
                    {
                        _logger.LogInformation("ScanOnStartup 未启用，跳过启动时扫描。");
                        return;
                    }

                    _logger.LogInformation("启动时自动扫描开始（ScanOnStartup = true）。");

                    var result = await _service
                        .RunAsync(config, null, _cts.Token)
                        .ConfigureAwait(false);

                    _logger.LogInformation("启动时自动扫描结束，结果: {Result}。", result);
                }
                catch (OperationCanceledException)
                {
                    // Jellyfin 正在关闭，忽略
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "启动时自动扫描发生异常。");
                }
            },
            _cts.Token);

        // 不阻塞 Jellyfin 启动流程
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            // 忽略
        }

        _cts.Dispose();
        _cts = null;
    }
}
