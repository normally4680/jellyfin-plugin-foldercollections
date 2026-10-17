using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

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
    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    [JsonPropertyName("libraryName")]
    public string LibraryName { get; set; } = string.Empty;

    [JsonPropertyName("libraryIndex")]
    public int LibraryIndex { get; set; }

    [JsonPropertyName("libraryTotal")]
    public int LibraryTotal { get; set; }

    [JsonPropertyName("collectionIndex")]
    public int CollectionIndex { get; set; }

    [JsonPropertyName("collectionTotal")]
    public int CollectionTotal { get; set; }

    [JsonPropertyName("created")]
    public int Created { get; set; }

    [JsonPropertyName("updated")]
    public int Updated { get; set; }

    [JsonPropertyName("removed")]
    public int Removed { get; set; }

    [JsonPropertyName("unchanged")]
    public int Unchanged { get; set; }

    [JsonPropertyName("mediaItemCount")]
    public int MediaItemCount { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; }

    [JsonPropertyName("endedAt")]
    public DateTime? EndedAt { get; set; }

    [JsonPropertyName("lastMessage")]
    public string LastMessage { get; set; } = string.Empty;

    [JsonPropertyName("cancelled")]
    public bool Cancelled { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets 最近的内存日志，用于前端控制台展示.
    /// </summary>
    [JsonPropertyName("recentLogs")]
    public List<string> RecentLogs { get; set; } = new();

    /// <summary>
    /// 生成一份深拷贝快照.
    /// </summary>
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
            ErrorMessage = ErrorMessage,
            // 深拷贝 List
            RecentLogs = new List<string>(RecentLogs)
        };
    }
}
