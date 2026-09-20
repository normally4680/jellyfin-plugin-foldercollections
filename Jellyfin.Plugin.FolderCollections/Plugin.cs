using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.FolderCollections.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.FolderCollections;

/// <summary>
/// 插件主类.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// 获取插件的静态 ID.
    /// </summary>
    public static readonly Guid StaticId = Guid.Parse("3f2504e0-4f89-11d3-9a0c-0305e82c3301");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">应用程序路径.</param>
    /// <param name="xmlSerializer">XML 序列化器.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    /// <inheritdoc />
    public override string Name => "Folder Collections";

    /// <inheritdoc />
    public override Guid Id => StaticId;

    /// <inheritdoc />
    public override string Description => "根据物理文件夹结构自动生成媒体集合，且跟随媒体库扫描自动更新.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace)
            }
        };
    }
}
