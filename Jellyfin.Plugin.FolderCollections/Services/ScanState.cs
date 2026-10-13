using System;

namespace Jellyfin.Plugin.FolderCollections.Services;

/// <summary>
/// 扫描运行结果.
/// </summary>
public enum ScanRunResult
{
    /// <summary>扫描正常完成.</summary>
    Completed,

    /// <summary>扫描被用户取消.</summary>
    Cancelled,

    /// <summary>已有扫描在运行，本次被跳过.</summary>
    AlreadyRunning,

    /// <summary>扫描过程中发生未处理异常.</summary>
    Failed
}

/// <summary>
/// 扫描运行状态的快照，用于 REST /Status 端点和计划任务.
/// </summary>
public sealed class ScanState
{
    /// <summary>Gets or sets 是否正在运行.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Gets or sets 总进度（0~100）.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets 当前阶段名，如 "4/5 创建集合".</summary>
    public string Phase { get; set; } = string.Empty;

    /// <summary>Gets or sets 当前媒体库名.</summary>
    public string LibraryName { get; set; } = string.Empty;

    /// <summary>Gets or sets 当前媒体库序号（从 1 开始）.</summary>
    public int LibraryIndex { get; set; }

    /// <summary>Gets or sets 媒体库总数.</summary>
    public int LibraryTotal { get; set; }

    /// <summary>Gets or sets 当前媒体库内已处理集合数.</summary>
    public int CollectionIndex { get; set; }

    /// <summary>Gets or sets 当前媒体库内集合总数.</summary>
    public int CollectionTotal { get; set; }

    /// <summary>Gets or sets 新增集合数.</summary>
    public int Created { get; set; }

    /// <summary>Gets or sets 更新集合数.</summary>
    public int Updated { get; set; }

    /// <summary>Gets or sets 删除集合数.</summary>
    public int Removed { get; set; }

    /// <summary>Gets or sets 未变化集合数.</summary>
    public int Unchanged { get; set; }

    /// <summary>Gets or sets 累计媒体项数.</summary>
    public int MediaItemCount { get; set; }

    /// <summary>Gets or sets 扫描开始时间（UTC）.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Gets or sets 扫描结束时间（UTC）.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Gets or sets 最近一条状态文字.</summary>
    public string LastMessage { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether 是否被取消.</summary>
    public bool Cancelled { get; set; }

    /// <summary>Gets or sets 错误信息（如果有）.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// 生成一份深拷贝快照.
    /// </summary>
    /// <returns>新的 ScanState 实例.</returns>
    public ScanState Clone()
    {
        return new ScanState
        {
            IsRunning = IsRunning,
            Progress = Progress,
            Phase = Phase,
            LibraryName = LibraryName,
            LibraryIndex = LibraryIndex,
            LibraryTotal = LibraryTotal,
            CollectionIndex = CollectionIndex,
            CollectionTotal = CollectionTotal,
            Created = Created,
            Updated = Updated,
            Removed = Removed,
            Unchanged = Unchanged,
            MediaItemCount = MediaItemCount,
            StartedAt = StartedAt,
            EndedAt = EndedAt,
            LastMessage = LastMessage,
            Cancelled = Cancelled,
            ErrorMessage = ErrorMessage
        };
    }
}
