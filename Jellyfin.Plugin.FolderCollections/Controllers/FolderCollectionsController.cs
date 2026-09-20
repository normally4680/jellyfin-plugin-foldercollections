using System.Threading.Tasks;
using Jellyfin.Plugin.FolderCollections.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.FolderCollections.Controllers;

/// <summary>
/// 文件夹集合 API 控制器.
/// </summary>
[ApiController]
[Route("FolderCollections")]
public class FolderCollectionsController : ControllerBase
{
    private readonly FolderCollectionService _service;
    private readonly IPluginManager _pluginManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderCollectionsController"/> class.
    /// </summary>
    /// <param name="service">文件夹集合服务.</param>
    /// <param name="pluginManager">插件管理器.</param>
    public FolderCollectionsController(FolderCollectionService service, IPluginManager pluginManager)
    {
        _service = service;
        _pluginManager = pluginManager;
    }

    /// <summary>
    /// 触发扫描.
    /// </summary>
    /// <returns>操作结果.</returns>
    [HttpPost("Scan")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult TriggerScan()
    {
        // 1. 获取 LocalPlugin 对象
        var localPlugin = _pluginManager.GetPlugin(Plugin.StaticId);

        // 2. 从 LocalPlugin 的 Instance 属性中获取实际的插件实例
        if (localPlugin?.Instance is not Plugin plugin)
        {
            return NotFound("插件未加载或类型不匹配。");
        }

        // 3. 通过插件实例访问 Configuration 属性
        var config = plugin.Configuration;
        Task.Run(() => _service.ScanAndCreateCollectionsAsync(config));
        return Ok("扫描已启动，请查看 Jellyfin 控制台日志。");
    }
}
