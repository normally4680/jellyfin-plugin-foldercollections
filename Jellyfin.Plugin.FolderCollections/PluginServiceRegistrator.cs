using Jellyfin.Plugin.FolderCollections.Services;
using Jellyfin.Plugin.FolderCollections.Tasks;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.FolderCollections;

/// <summary>
/// 插件服务注册器.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<FolderCollectionService>();
        serviceCollection.AddSingleton<ILibraryPostScanTask, FolderCollectionPostScanTask>();
    }
}
