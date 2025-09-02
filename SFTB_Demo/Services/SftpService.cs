using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Fpe;
using Renci.SshNet;
using SFTB_Demo.Interfaces;
using SFTB_Demo.Models;
using SFTB_Demo.Settings;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace SFTB_Demo.Services;

public class SftpService : ISftpService, IDisposable
{
    private readonly SftpConfig _config;
    private readonly ILogger<SftpService> _logger;
    private SftpClient? _sftpClient;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly object _lockObject = new object();

    public SftpService(IOptions<SftpConfig> config, ILogger<SftpService> logger)
    {
        _config = config.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionSemaphore = new SemaphoreSlim(1, 1);

        ValidateConfiguration();
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(_config.Host))
            throw new ArgumentException("SFTP Host is required", nameof(_config.Host));

        if (string.IsNullOrWhiteSpace(_config.Username))
            throw new ArgumentException("SFTP Username is required", nameof(_config.Username));

        if (string.IsNullOrWhiteSpace(_config.PrivateKeyPath) && string.IsNullOrWhiteSpace(_config.Password))
            throw new ArgumentException("Either PrivateKeyPath or Password must be provided");
    }

    private async Task<SftpClient> GetConnectedClientAsync()
    {
        await _connectionSemaphore.WaitAsync();

        try
        {
            lock (_lockObject)
            {
                if (_sftpClient != null && _sftpClient.IsConnected)
                {
                    return _sftpClient;
                }

                _sftpClient?.Dispose();
                _sftpClient = CreateSftpClient();
            }

            await Task.Run(() => _sftpClient.Connect());
            _logger.LogInformation("SFTP connection established to {Host}:{Port}", _config.Host, _config.Port);

            return _sftpClient;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to establish SFTP connection to {Host}:{Port}", _config.Host, _config.Port);
            throw;
        }
        finally
        {
            _connectionSemaphore.Release();
        }
    }

    private SftpClient CreateSftpClient()
    {
        ConnectionInfo connectionInfo;

        if (!string.IsNullOrEmpty(_config.PrivateKeyPath))
        {
            var privateKeyFile = string.IsNullOrEmpty(_config.PrivateKeyPassphrase)
                ? new PrivateKeyFile(_config.PrivateKeyPath)
                : new PrivateKeyFile(_config.PrivateKeyPath, _config.PrivateKeyPassphrase);

            var privateKeyAuth = new PrivateKeyAuthenticationMethod(_config.Username, privateKeyFile);
            connectionInfo = new ConnectionInfo(_config.Host, _config.Port, _config.Username, privateKeyAuth);
        }
        else
        {
            var passwordAuth = new PasswordAuthenticationMethod(_config.Username, _config.Password);
            connectionInfo = new ConnectionInfo(_config.Host, _config.Port, _config.Username, passwordAuth);
        }

        connectionInfo.Timeout = TimeSpan.FromMilliseconds(_config.ConnectionTimeout);
        return new SftpClient(connectionInfo);
    }

    //private string BuildRemotePath("0",params string[] pathParts)
    //{
    //    var cleanParts = pathParts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();

    //    if (cleanParts.Length == 0) return _config.RootDirectory;


    //    var combined = Path.Combine(cleanParts).Replace('\\', '/');
    //    return combined.StartsWith('/') ? combined : $"/{combined}";
    //}
    private string BuildRemotePath(params string[] pathParts)
    {
        var cleanParts = pathParts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (cleanParts.Length == 0)
            return string.Empty;

        var combined = Path.Combine(cleanParts).Replace('\\', '/');
        return combined.StartsWith("/") ? combined : "/" + combined;
    }

    public static string GenerateFileName(string moduleName, int seriesNumber, string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName);
        var randomName = Path.GetRandomFileName().Replace(".", "");
        string series = seriesNumber.ToString("D4");

        // Final format
        return $"{moduleName}_{series}_{randomName}{extension}";
    }
    public async Task<FileOperationResponse> UploadFileAsync(IFormFile file,string module,int rowNumber, string? remotePath = null)
    {
        if (file == null || file.Length == 0)
        {
            return new FileOperationResponse
            {
                Success = false,
                Message = "File is empty or null"
            };
        }

        try
        {
            var client = await GetConnectedClientAsync();
            var fileName = Path.GetFileName(file.FileName);
            //var fileName = GenerateFileName(module, rowNumber, file.FileName);
            var tenantId = "0";
            var rootDir = _config.RootDirectory.Replace("{tenantId}", tenantId);

            var fullRemotePath = BuildRemotePath(rootDir, remotePath ?? "", fileName);

            // Create directory if it doesn't exist
            var directory = Path.GetDirectoryName(fullRemotePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory) && !await DirectoryExistsAsync(client, directory))
            {
                await CreateDirectoryRecursiveAsync(client, directory);
            }

            using var stream = file.OpenReadStream();
            await Task.Run(() => client.UploadFile(stream, fullRemotePath, true));

            _logger.LogInformation("File uploaded successfully: {FileName} to {RemotePath}", fileName, fullRemotePath);

            return new FileOperationResponse
            {
                Success = true,
                Message = "File uploaded successfully",
                Data = new { FileName = fileName, RemotePath = fullRemotePath, Size = file.Length }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file: {FileName}", file.FileName);
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Upload failed: {ex.Message}"
            };
        }
    }

    public async Task<FileOperationResponse> DownloadFileAsync(string remoteFilePath)
    {
        try
        {
            var client = await GetConnectedClientAsync();
            var tenantId = "0";
            var rootDir = _config.RootDirectory.Replace("{tenantId}", tenantId);

            var fullRemotePath = BuildRemotePath(rootDir, remoteFilePath ??"");

            if (!await Task.Run(() => client.Exists(fullRemotePath)))
            {
                return new FileOperationResponse
                {
                    Success = false,
                    Message = "File not found"
                };
            }

            using var memoryStream = new MemoryStream();
            await Task.Run(() => client.DownloadFile(fullRemotePath, memoryStream));

            var fileName = Path.GetFileName(remoteFilePath);
            var fileBytes = memoryStream.ToArray();

            _logger.LogInformation("File downloaded successfully: {FileName}", fileName);

            return new FileOperationResponse
            {
                Success = true,
                Message = "File downloaded successfully",
                Data = new { FileName = fileName, Content = fileBytes, Size = fileBytes.Length }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading file: {FilePath}", remoteFilePath);
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Download failed: {ex.Message}"
            };
        }
    }

    public async Task<FileOperationResponse> DeleteFileAsync(string remoteFilePath)
    {
        try
        {
            var client = await GetConnectedClientAsync();
            var tenantId = "0";
            var rootDir = _config.RootDirectory.Replace("{tenantId}", tenantId);

            var fullRemotePath = BuildRemotePath(rootDir, remoteFilePath ?? "");


            if (!await Task.Run(() => client.Exists(fullRemotePath)))
            {
                return new FileOperationResponse
                {
                    Success = false,
                    Message = "File not found"
                };
            }

            await Task.Run(() => client.DeleteFile(fullRemotePath));

            _logger.LogInformation("File deleted successfully: {FilePath}", fullRemotePath);

            return new FileOperationResponse
            {
                Success = true,
                Message = "File deleted successfully",
                Data = new { DeletedFile = remoteFilePath }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file: {FilePath}", remoteFilePath);
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Delete failed: {ex.Message}"
            };
        }
    }

    public async Task<FileOperationResponse> GetFileListAsync(string? remotePath = null)
    {
        try
        {
            var tenantId = "0";
            var rootDir = _config.RootDirectory.Replace("{tenantId}", tenantId);

            var path = BuildRemotePath(rootDir, remotePath ?? "");

            var client = await GetConnectedClientAsync();
            var fullRemotePath = string.IsNullOrEmpty(remotePath)
                ? _config.RootDirectory
                : path;

            if (!await Task.Run(() => client.Exists(fullRemotePath)))
            {
                return new FileOperationResponse
                {
                    Success = false,
                    Message = "Directory not found"
                };
            }

            var files = await Task.Run(() => client.ListDirectory(fullRemotePath));
            var fileList = files
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => new FileDto
                {
                    Name = f.Name,
                    Size = f.Length,
                    LastModified = f.LastWriteTime,
                    Path = f.FullName.Replace(_config.RootDirectory, "").TrimStart('/'),
                    IsDirectory = f.IsDirectory
                })
                .OrderBy(f => f.IsDirectory ? 0 : 1)
                .ThenBy(f => f.Name)
                .ToList();

            _logger.LogInformation("Retrieved file list for directory: {Directory}", fullRemotePath);

            return new FileOperationResponse
            {
                Success = true,
                Message = "File list retrieved successfully",
                Data = fileList
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving file list for path: {Path}", remotePath);
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Failed to retrieve file list: {ex.Message}"
            };
        }
    }

    public async Task<FileOperationResponse> CreateDirectoryAsync(string remotePath)
    {
        try
        {
            var client = await GetConnectedClientAsync();
            var tenantId = "0";
            var rootDir = _config.RootDirectory.Replace("{tenantId}", tenantId);

            var fullRemotePath = BuildRemotePath(rootDir, remotePath ?? "");


            await CreateDirectoryRecursiveAsync(client, fullRemotePath);

            _logger.LogInformation("Directory created successfully: {Directory}", fullRemotePath);

            return new FileOperationResponse
            {
                Success = true,
                Message = "Directory created successfully",
                Data = new { DirectoryPath = remotePath }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating directory: {Directory}", remotePath);
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Directory creation failed: {ex.Message}"
            };
        }
    }

    public async Task<bool> FileExistsAsync(string remoteFilePath)
    {
        try
        {
            var client = await GetConnectedClientAsync();
            var tenantId = "0";
            var rootDir = _config.RootDirectory.Replace("{tenantId}", tenantId);

            var fullRemotePath = BuildRemotePath(rootDir, remoteFilePath ?? "");

            return await Task.Run(() => client.Exists(fullRemotePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking file existence: {FilePath}", remoteFilePath);
            return false;
        }
    }

    public async Task<FileOperationResponse> MoveFileAsync(string sourceFile, string destinationFile)
    {
        try
        {
            var client = await GetConnectedClientAsync();
            var tenantId = "0";
            var rootDir = _config.RootDirectory.Replace("{tenantId}", tenantId);

            var fullSourcePath = BuildRemotePath(rootDir, sourceFile ?? "");

            var fullDestinationPath = BuildRemotePath(rootDir, destinationFile??"");

            if (!await Task.Run(() => client.Exists(fullSourcePath)))
            {
                return new FileOperationResponse
                {
                    Success = false,
                    Message = "Source file not found"
                };
            }

            // Create destination directory if it doesn't exist
            var destinationDir = Path.GetDirectoryName(fullDestinationPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(destinationDir) && !await DirectoryExistsAsync(client, destinationDir))
            {
                await CreateDirectoryRecursiveAsync(client, destinationDir);
            }

            await Task.Run(() => client.RenameFile(fullSourcePath, fullDestinationPath));

            _logger.LogInformation("File moved successfully from {Source} to {Destination}", sourceFile, destinationFile);

            return new FileOperationResponse
            {
                Success = true,
                Message = "File moved successfully",
                Data = new { SourceFile = sourceFile, DestinationFile = destinationFile }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error moving file from {Source} to {Destination}", sourceFile, destinationFile);
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Move failed: {ex.Message}"
            };
        }
    }

    private async Task<bool> DirectoryExistsAsync(SftpClient client, string path)
    {
        try
        {
            return await Task.Run(() =>
            {
                var attributes = client.GetAttributes(path);
                return attributes.IsDirectory;
            });
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            // Path does not exist
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking directory existence: {Path}", path);
            return false;
        }
    }


    private async Task CreateDirectoryRecursiveAsync(SftpClient client, string path)
    {
        var directories = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = "";

        foreach (var directory in directories)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? $"/{directory}" : $"{currentPath}/{directory}";

            if (!await DirectoryExistsAsync(client, currentPath))
            {
                await Task.Run(() => client.CreateDirectory(currentPath));
            }
        }
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            _sftpClient?.Dispose();
            _sftpClient = null;
        }
        _connectionSemaphore?.Dispose();
    }
}
