using SFTB_Demo.Models;

namespace SFTB_Demo.Interfaces;
public interface ISftpService
{
    Task<FileOperationResponse> UploadFileAsync(IFormFile file,string module,int rowNumber, string? remotePath = null);
    Task<FileOperationResponse> DownloadFileAsync(string remoteFilePath);
    Task<FileOperationResponse> DeleteFileAsync(string remoteFilePath);
    Task<FileOperationResponse> GetFileListAsync(string? remotePath = null);
    Task<FileOperationResponse> CreateDirectoryAsync(string remotePath);
    Task<bool> FileExistsAsync(string remoteFilePath);
    Task<FileOperationResponse> MoveFileAsync(string sourceFile, string destinationFile);
}
public interface IEnhancedSftpService
{
    // Basic file operations
    Task<FileOperationResponse> UploadFileAsync(IFormFile file, string? remotePath = null);
    Task<FileOperationResponse> DownloadFileAsync(string remoteFilePath);
    Task<FileOperationResponse> DeleteFileAsync(string remoteFilePath);
    Task<FileOperationResponse> GetFileListAsync(string? remotePath = null);
    Task<FileOperationResponse> CreateDirectoryAsync(string remotePath);
    Task<bool> FileExistsAsync(string remoteFilePath);
    Task<FileOperationResponse> MoveFileAsync(string sourceFile, string destinationFile);

    // Enhanced operations
    Task<FileOperationResponse> UploadFileWithProgressAsync(
        IFormFile file,
        string? remotePath = null,
        IProgress<TransferProgress>? progress = null);

    Task<FileOperationResponse> BatchUploadAsync(
        IEnumerable<IFormFile> files,
        string? remotePath = null,
        IProgress<BatchTransferProgress>? progress = null);

    Task<FileOperationResponse> CopyFileAsync(string sourceFile, string destinationFile);

    Task<FileOperationResponse> GetFileChecksumAsync(string remoteFilePath, string algorithm = "SHA256");

    Task<FileOperationResponse> SyncDirectoryAsync(
        string localPath,
        string remotePath,
        SyncDirection direction = SyncDirection.LocalToRemote,
        IProgress<SyncProgress>? progress = null);

    Task<FileOperationResponse> GetServerInfoAsync();
}