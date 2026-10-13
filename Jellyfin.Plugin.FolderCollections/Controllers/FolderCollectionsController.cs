using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.FolderCollections.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<FolderCollectionsController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FolderCollectionsController"/> class.
    /// </summary>
    /// <param name="service">文件夹集合服务.</param>
    /// <param name="pluginManager">插件管理器.</param>
    /// <param name="logger">日志记录器.</param>
    public FolderCollectionsController(
        FolderCollectionService service,
        IPluginManager pluginManager,
        ILogger<FolderCollectionsController> logger)
    {
        _service = service;
        _pluginManager = pluginManager;
        _logger = logger;
    }

    /// <summary>
    /// 触发扫描（后台异步执行，不阻塞响应）.
    /// </summary>
    /// <returns>
    /// 202 Accepted: 扫描已启动；
    /// 409 Conflict: 已有扫描在运行。
    /// </returns>
    [HttpPost("Scan")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult TriggerScan()
    {
        // 若已有扫描在运行，直接返回 409，避免并发写数据库/XML
        if (_service.IsRunning)
        {
            _logger.LogInformation("收到扫描请求，但扫描已在运行中。");
            return Conflict(new
            {
                message = "扫描已在运行中。请到 控制台 → 计划任务 → \"Folder Collections 扫描\" 查看进度。"
            });
        }

        // 从插件实例获取配置
        var localPlugin = _pluginManager.GetPlugin(Plugin.StaticId);
        if (localPlugin?.Instance is not Plugin plugin)
        {
            _logger.LogError("插件未加载或类型不匹配，无法触发扫描。");
            return NotFound(new { message = "插件未加载或类型不匹配。" });
        }

        var config = plugin.Configuration;

        // 后台触发，不阻塞 HTTP 响应
        _ = Task.Run(
            async () =>
            {
                try
                {
                    // 从 REST 触发的扫描不向计划任务 UI 上报进度（progress 传 null），
                    // 但状态可以通过 /FolderCollections/Status 查询。
                    // 如需可视化进度条，请从 控制台 → 计划任务 页面手动触发。
                    await _service.RunAsync(config, null, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "REST 触发的后台扫描失败。");
                }
            },
            CancellationToken.None);

        return Accepted(new
        {
            message = "扫描已启动。请访问 /FolderCollections/Status 查看实时状态，"
                + "或到 控制台 → 计划任务 → \"Folder Collections 扫描\" 页面查看。"
        });
    }

    /// <summary>
    /// 查询当前扫描状态.
    /// </summary>
    /// <returns>状态快照；若从未运行过，返回 IsRunning=false 的默认值。</returns>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStatus()
    {
        var snapshot = _service.GetSnapshot();
        return Ok(snapshot);
    }
}
