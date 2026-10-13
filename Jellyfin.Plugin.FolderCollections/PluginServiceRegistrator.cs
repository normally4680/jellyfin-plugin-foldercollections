using Jellyfin.Plugin.FolderCollections.Services;
using Jellyfin.Plugin.FolderCollections.Tasks;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FolderCollections;

/// <summary>
/// 插件服务注册.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // 核心扫描服务（单例，内部有并发锁与状态机）
        serviceCollection.AddSingleton<FolderCollectionService>();

        // 媒体库扫描后置任务
        serviceCollection.AddSingleton<FolderCollectionPostScanTask>();

        // 计划任务：注册为 IScheduledTask，Jellyfin 的 TaskManager 会发现它
        serviceCollection.AddSingleton<IScheduledTask, FolderCollectionScheduledTask>();

        // 启动时自动扫描（由配置项 ScanOnStartup 控制，通过 IHostedService 延迟 60 秒触发）
        serviceCollection.AddHostedService<StartupScanHostedService>();
    }
}
