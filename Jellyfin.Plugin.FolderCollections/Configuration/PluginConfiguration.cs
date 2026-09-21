using System;
using System.Collections.ObjectModel;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.FolderCollections.Configuration;

/// <summary>
/// 插件配置类.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
#pragma warning disable CA2227
    /// <summary>
    /// Gets or sets 需要扫描的媒体库 ID 列表.
    /// </summary>
    public Collection<Guid> SelectedLibraryIds { get; set; } = new Collection<Guid>();
#pragma warning restore CA2227

    /// <summary>
    /// Gets or sets a value indicating whether 是否覆盖已存在的同名集合.
    /// </summary>
    public bool OverwriteExisting { get; set; } = false;

    /// <summary>
    /// Gets or sets a value indicating whether 服务器启动时是否自动扫描.
    /// </summary>
    public bool ScanOnStartup { get; set; } = true;

    /// <summary>
    /// Gets or sets 集合名最大字符长度。0 = 不限制.
    /// </summary>
    public int MaxNameLength { get; set; } = 80;

    /// <summary>
    /// Gets or sets a value indicating whether 是否自动删除不再需要的集合.
    /// </summary>
    public bool RemoveObsoleteCollections { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 媒体库扫描完成后是否自动同步集合.
    /// </summary>
    public bool AutoSyncAfterLibraryScan { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 提取 #XX 形式的标签.
    /// </summary>
    public bool TagHashEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 提取 {XX} 形式的标签.
    /// </summary>
    public bool TagBraceEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 提取 [XX] 形式的标签.
    /// </summary>
    public bool TagSquareBracketEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether 提取 【XX】 形式的标签.
    /// </summary>
    public bool TagChineseBracketEnabled { get; set; } = true;
}
