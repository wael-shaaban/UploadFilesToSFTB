namespace SFTB_Demo.Models;

public class FileDto
{
    public string Name { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public string Path { get; set; }
    public bool IsDirectory { get; set; }
}
// Models/FileOperationResponse.cs

public class FileOperationResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
}

// Supporting classes for new features
public class TransferProgress
{
    public string FileName { get; set; } = string.Empty;
    public long BytesTransferred { get; set; }
    public long TotalBytes { get; set; }
    public double ProgressPercentage { get; set; }
}

public class BatchTransferProgress
{
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public int FailedFiles { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public double ProgressPercentage { get; set; }
}

public class SyncProgress
{
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public SyncDirection Direction { get; set; }
    public double ProgressPercentage { get; set; }
}

public enum SyncDirection
{
    LocalToRemote,
    RemoteToLocal,
    Bidirectional
}