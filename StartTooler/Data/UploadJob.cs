using System;
using System.Collections.Generic;

namespace StartTooler.Data;

/// <summary>
/// 上传任务。一个文件对应一个未完成的 job。
/// 落库到 upload_jobs 表，跨进程持久化，崩溃/重启后可恢复。
///
/// 生命周期：
///   1) InitiateMultipart → 创建 job
///   2) UploadPart × N   → 每片成功 UpsertAsync（parts_uploaded 累加）
///   3) Complete          → DeleteAsync
///   4) Cancel            → 保留 job（不 Abort），用户后续点上传可续
///   5) App 重启 + 有残留  → 弹窗询问「是否恢复」，确认后走续传
/// </summary>
public class UploadJob
{
    public long Id { get; set; }
    public string ProjectPath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ObjectKey { get; set; } = "";
    public string UploadId { get; set; } = "";
    public long FileSize { get; set; }
    public int PartSize { get; set; }
    public List<UploadedPart> PartsUploaded { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 已成功上传的分片信息。
/// </summary>
public class UploadedPart
{
    public int PartNumber { get; set; }
    public string ETag { get; set; } = "";
}
