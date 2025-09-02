using Microsoft.Extensions.Options;
using Renci.SshNet;
using SFTB_Demo.Interfaces;
using SFTB_Demo.Models;
using SFTB_Demo.Settings;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;

namespace SFTB_Demo.Services;

public class EnhancedSftpService : IEnhancedSftpService, IDisposable
{
    private readonly SftpConfig _config;
    private readonly ILogger<EnhancedSftpService> _logger;
    private readonly ConcurrentQueue<SftpClient> _connectionPool;
    private readonly SemaphoreSlim _connectionSemaphore;
    private readonly SemaphoreSlim _poolSemaphore;
    private readonly int _maxPoolSize = 5;
    private bool _disposed = false;

    public EnhancedSftpService(IOptions<SftpConfig> config, ILogger<EnhancedSftpService> logger)
    {
        _config = config.Value ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connectionPool = new ConcurrentQueue<SftpClient>();
        _connectionSemaphore = new SemaphoreSlim(1, 1);
        _poolSemaphore = new SemaphoreSlim(_maxPoolSize, _maxPoolSize);

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

    private async Task<SftpClient> GetConnectionAsync()
    {
        await _poolSemaphore.WaitAsync();

        try
        {
            // Try to get a connection from the pool
            if (_connectionPool.TryDequeue(out var pooledClient) && pooledClient.IsConnected)
            {
                return pooledClient;
            }

            // Create new connection if pool is empty or connection is dead
            var newClient = await CreateAndConnectClientAsync();
            return newClient;
        }
        catch
        {
            _poolSemaphore.Release();
            throw;
        }
    }

    private void ReturnConnection(SftpClient client)
    {
        if (client != null && client.IsConnected && !_disposed)
        {
            _connectionPool.Enqueue(client);
        }
        else
        {
            client?.Dispose();
        }

        _poolSemaphore.Release();
    }

    private async Task<SftpClient> CreateAndConnectClientAsync()
    {
        var connectionInfo = CreateConnectionInfo();
        var client = new SftpClient(connectionInfo);

        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await Task.Run(() => client.Connect());
                _logger.LogInformation("SFTP connection established to {Host}:{Port} (attempt {Attempt})",
                    _config.Host, _config.Port, attempt);
                return client;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning(ex, "Failed to connect to SFTP server (attempt {Attempt}/{MaxRetries})",
                    attempt, maxRetries);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))); // Exponential backoff
            }
        }

        throw new InvalidOperationException($"Failed to connect to SFTP server after {maxRetries} attempts");
    }

    private ConnectionInfo CreateConnectionInfo()
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
        return connectionInfo;
    }

    private string BuildRemotePath(params string[] pathParts)
    {
        var cleanParts = pathParts.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (cleanParts.Length == 0) return _config.RootDirectory;

        var combined = Path.Combine(cleanParts).Replace('\\', '/');
        return combined.StartsWith('/') ? combined : $"/{combined}";
    }

    // Progress reporting for file transfers
    public async Task<FileOperationResponse> UploadFileWithProgressAsync(
        IFormFile file,
        string? remotePath = null,
        IProgress<TransferProgress>? progress = null)
    {
        if (file == null || file.Length == 0)
        {
            return new FileOperationResponse
            {
                Success = false,
                Message = "File is empty or null"
            };
        }

        var client = await GetConnectionAsync();
        try
        {
            var fileName = Path.GetFileName(file.FileName);
            var fullRemotePath = BuildRemotePath(_config.RootDirectory, remotePath ?? "", fileName);

            var directory = Path.GetDirectoryName(fullRemotePath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(directory) && !await DirectoryExistsAsync(client, directory))
            {
                await CreateDirectoryRecursiveAsync(client, directory);
            }

            using var stream = file.OpenReadStream();
            var totalBytes = file.Length;
            var buffer = new byte[8192];
            long uploadedBytes = 0;

            using var remoteStream = await Task.Run(() => client.Create(fullRemotePath));

            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await remoteStream.WriteAsync(buffer, 0, bytesRead);
                uploadedBytes += bytesRead;

                progress?.Report(new TransferProgress
                {
                    FileName = fileName,
                    BytesTransferred = uploadedBytes,
                    TotalBytes = totalBytes,
                    ProgressPercentage = (double)uploadedBytes / totalBytes * 100
                });
            }

            _logger.LogInformation("File uploaded with progress tracking: {FileName} to {RemotePath}",
                fileName, fullRemotePath);

            return new FileOperationResponse
            {
                Success = true,
                Message = "File uploaded successfully",
                Data = new { FileName = fileName, RemotePath = fullRemotePath, Size = file.Length }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file with progress: {FileName}", file.FileName);
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Upload failed: {ex.Message}"
            };
        }
        finally
        {
            ReturnConnection(client);
        }
    }

    // Batch upload multiple files
    public async Task<FileOperationResponse> BatchUploadAsync(
        IEnumerable<IFormFile> files,
        string? remotePath = null,
        IProgress<BatchTransferProgress>? progress = null)
    {
        var fileList = files.ToList();
        if (!fileList.Any())
        {
            return new FileOperationResponse
            {
                Success = false,
                Message = "No files provided for batch upload"
            };
        }

        var results = new List<object>();
        var totalFiles = fileList.Count;
        var processedFiles = 0;
        var failedFiles = new List<string>();

        foreach (var file in fileList)
        {
            try
            {
                var result = await UploadFileAsync(file, remotePath);
                if (result.Success)
                {
                    results.Add(new { FileName = file.FileName, Status = "Success" });
                }
                else
                {
                    failedFiles.Add(file.FileName);
                    results.Add(new { FileName = file.FileName, Status = "Failed", Error = result.Message });
                }
            }
            catch (Exception ex)
            {
                failedFiles.Add(file.FileName);
                results.Add(new { FileName = file.FileName, Status = "Failed", Error = ex.Message });
            }

            processedFiles++;
            progress?.Report(new BatchTransferProgress
            {
                TotalFiles = totalFiles,
                ProcessedFiles = processedFiles,
                FailedFiles = failedFiles.Count,
                CurrentFile = file.FileName,
                ProgressPercentage = (double)processedFiles / totalFiles * 100
            });
        }

        var successCount = totalFiles - failedFiles.Count;
        _logger.LogInformation("Batch upload completed: {SuccessCount}/{TotalCount} files uploaded successfully",
            successCount, totalFiles);

        return new FileOperationResponse
        {
            Success = failedFiles.Count == 0,
            Message = $"Batch upload completed: {successCount}/{totalFiles} files uploaded successfully",
            Data = new { Results = results, SuccessCount = successCount, FailedCount = failedFiles.Count }
        };
    }

    // Copy file on remote server
    public async Task<FileOperationResponse> CopyFileAsync(string sourceFile, string destinationFile)
    {
        var client = await GetConnectionAsync();
        try
        {
            var fullSourcePath = BuildRemotePath(_config.RootDirectory, sourceFile);
            var fullDestinationPath = BuildRemotePath(_config.RootDirectory, destinationFile);

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

            // Copy file by downloading and re-uploading
            using var sourceStream = await Task.Run(() => client.OpenRead(fullSourcePath));
            using var destinationStream = await Task.Run(() => client.Create(fullDestinationPath));
            await sourceStream.CopyToAsync(destinationStream);

            _logger.LogInformation("File copied successfully from {Source} to {Destination}",
                sourceFile, destinationFile);

            return new FileOperationResponse
            {
                Success = true,
                Message = "File copied successfully",
                Data = new { SourceFile = sourceFile, DestinationFile = destinationFile }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error copying file from {Source} to {Destination}", sourceFile, destinationFile);
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Copy failed: {ex.Message}"
            };
        }
        finally
        {
            ReturnConnection(client);
        }
    }

    // Calculate file checksum for integrity verification
    public async Task<FileOperationResponse> GetFileChecksumAsync(string remoteFilePath, string algorithm = "SHA256")
    {
        var client = await GetConnectionAsync();
        try
        {
            var fullRemotePath = BuildRemotePath(_config.RootDirectory, remoteFilePath);

            if (!await Task.Run(() => client.Exists(fullRemotePath)))
            {
                return new FileOperationResponse
                {
                    Success = false,
                    Message = "File not found"
                };
            }

            using var stream = await Task.Run(() => client.OpenRead(fullRemotePath));
            using var hasher = CreateHashAlgorithm(algorithm);

            var hashBytes = await hasher.ComputeHashAsync(stream);
            var hashString = Convert.ToHexString(hashBytes).ToLowerInvariant();

            _logger.LogInformation("Calculated {Algorithm} checksum for {FilePath}: {Checksum}",
                algorithm, remoteFilePath, hashString);

            return new FileOperationResponse
            {
                Success = true,
                Message = $"{algorithm} checksum calculated successfully",
                Data = new { FilePath = remoteFilePath, Algorithm = algorithm, Checksum = hashString }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating checksum for file: {FilePath}", remoteFilePath);
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Checksum calculation failed: {ex.Message}"
            };
        }
        finally
        {
            ReturnConnection(client);
        }

        static HashAlgorithm CreateHashAlgorithm(string algorithm)
        {
            return algorithm.ToUpperInvariant() switch
            {
                "MD5" => MD5.Create(),
                "SHA1" => SHA1.Create(),
                "SHA256" => SHA256.Create(),
                "SHA512" => SHA512.Create(),
                _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}")
            };
        }
    }

    // Directory synchronization
    public async Task<FileOperationResponse> SyncDirectoryAsync(
        string localPath,
        string remotePath,
        SyncDirection direction = SyncDirection.LocalToRemote,
        IProgress<SyncProgress>? progress = null)
    {
        var client = await GetConnectionAsync();
        try
        {
            var fullRemotePath = BuildRemotePath(_config.RootDirectory, remotePath);
            var syncResults = new List<object>();

            if (direction == SyncDirection.LocalToRemote)
            {
                // Upload files from local to remote
                if (!Directory.Exists(localPath))
                {
                    return new FileOperationResponse
                    {
                        Success = false,
                        Message = "Local directory not found"
                    };
                }

                var localFiles = Directory.GetFiles(localPath, "*", SearchOption.AllDirectories);
                var totalFiles = localFiles.Length;
                var processedFiles = 0;

                foreach (var localFile in localFiles)
                {
                    var relativePath = Path.GetRelativePath(localPath, localFile);
                    var targetRemotePath = BuildRemotePath(fullRemotePath, relativePath);

                    try
                    {
                        var targetDir = Path.GetDirectoryName(targetRemotePath)?.Replace('\\', '/');
                        if (!string.IsNullOrEmpty(targetDir) && !await DirectoryExistsAsync(client, targetDir))
                        {
                            await CreateDirectoryRecursiveAsync(client, targetDir);
                        }

                        using var localStream = File.OpenRead(localFile);
                        await Task.Run(() => client.UploadFile(localStream, targetRemotePath, true));

                        syncResults.Add(new { File = relativePath, Status = "Uploaded" });
                    }
                    catch (Exception ex)
                    {
                        syncResults.Add(new { File = relativePath, Status = "Failed", Error = ex.Message });
                    }

                    processedFiles++;
                    progress?.Report(new SyncProgress
                    {
                        TotalFiles = totalFiles,
                        ProcessedFiles = processedFiles,
                        CurrentFile = relativePath,
                        Direction = direction,
                        ProgressPercentage = (double)processedFiles / totalFiles * 100
                    });
                }
            }

            _logger.LogInformation("Directory sync completed: {Direction} between {LocalPath} and {RemotePath}",
                direction, localPath, remotePath);

            return new FileOperationResponse
            {
                Success = true,
                Message = "Directory synchronization completed",
                Data = new { Direction = direction, LocalPath = localPath, RemotePath = remotePath, Results = syncResults }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during directory synchronization");
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Synchronization failed: {ex.Message}"
            };
        }
        finally
        {
            ReturnConnection(client);
        }
    }

    // Get server information
    public async Task<FileOperationResponse> GetServerInfoAsync()
    {
        var client = await GetConnectionAsync();
        try
        {
            var serverVersion = client.ConnectionInfo.ServerVersion;
            var protocolVersion = client.ProtocolVersion;
            var isConnected = client.IsConnected;

            // Try to get working directory
            var workingDirectory = await Task.Run(() =>
            {
                try { return client.WorkingDirectory; }
                catch { return "Unknown"; }
            });

            var serverInfo = new
            {
                ServerVersion = serverVersion,
                ProtocolVersion = protocolVersion,
                IsConnected = isConnected,
                WorkingDirectory = workingDirectory,
                Host = _config.Host,
                Port = _config.Port,
                Username = _config.Username
            };

            _logger.LogInformation("Retrieved server information for {Host}:{Port}", _config.Host, _config.Port);

            return new FileOperationResponse
            {
                Success = true,
                Message = "Server information retrieved successfully",
                Data = serverInfo
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving server information");
            return new FileOperationResponse
            {
                Success = false,
                Message = $"Failed to retrieve server information: {ex.Message}"
            };
        }
        finally
        {
            ReturnConnection(client);
        }
    }

    // Original methods with connection pooling
    public async Task<FileOperationResponse> UploadFileAsync(IFormFile file, string? remotePath = null)
    {
        if (file == null || file.Length == 0)
        {
            return new FileOperationResponse
            {
                Success = false,
                Message = "File is empty or null"
            };
        }

        var client = await GetConnectionAsync();
        try
        {
            var fileName = Path.GetFileName(file.FileName);
            var fullRemotePath = BuildRemotePath(_config.RootDirectory, remotePath ?? "", fileName);

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
        finally
        {
            ReturnConnection(client);
        }
    }

    public async Task<FileOperationResponse> DownloadFileAsync(string remoteFilePath)
    {
        var client = await GetConnectionAsync();
        try
        {
            var fullRemotePath = BuildRemotePath(_config.RootDirectory, remoteFilePath);

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
        finally
        {
            ReturnConnection(client);
        }
    }

    public async Task<FileOperationResponse> DeleteFileAsync(string remoteFilePath)
    {
        var client = await GetConnectionAsync();
        try
        {
            var fullRemotePath = BuildRemotePath(_config.RootDirectory, remoteFilePath);

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
        finally
        {
            ReturnConnection(client);
        }
    }

    public async Task<FileOperationResponse> GetFileListAsync(string? remotePath = null)
    {
        var client = await GetConnectionAsync();
        try
        {
            var fullRemotePath = string.IsNullOrEmpty(remotePath)
                ? _config.RootDirectory
                : BuildRemotePath(_config.RootDirectory, remotePath);

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
        finally
        {
            ReturnConnection(client);
        }
    }

    public async Task<FileOperationResponse> CreateDirectoryAsync(string remotePath)
    {
        var client = await GetConnectionAsync();
        try
        {
            var fullRemotePath = BuildRemotePath(_config.RootDirectory, remotePath);

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
        finally
        {
            ReturnConnection(client);
        }
    }

    public async Task<bool> FileExistsAsync(string remoteFilePath)
    {
        var client = await GetConnectionAsync();
        try
        {
            var fullRemotePath = BuildRemotePath(_config.RootDirectory, remoteFilePath);
            return await Task.Run(() => client.Exists(fullRemotePath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking file existence: {FilePath}", remoteFilePath);
            return false;
        }
        finally
        {
            ReturnConnection(client);
        }
    }

    public async Task<FileOperationResponse> MoveFileAsync(string sourceFile, string destinationFile)
    {
        var client = await GetConnectionAsync();
        try
        {
            var fullSourcePath = BuildRemotePath(_config.RootDirectory, sourceFile);
            var fullDestinationPath = BuildRemotePath(_config.RootDirectory, destinationFile);

            if (!await Task.Run(() => client.Exists(fullSourcePath)))
            {
                return new FileOperationResponse
                {
                    Success = false,
                    Message = "Source file not found"
                };
            }

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
        finally
        {
            ReturnConnection(client);
        }
    }

    private async Task<bool> DirectoryExistsAsync(SftpClient client, string path)
    {
        try
        {
            var attributes = await Task.Run(() => client.GetAttributes(path));
            return attributes.IsDirectory;
        }
        catch
        {
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
        if (_disposed) return;

        _disposed = true;

        // Dispose all pooled connections
        while (_connectionPool.TryDequeue(out var client))
        {
            try
            {
                client?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing SFTP client from pool");
            }
        }

        _connectionSemaphore?.Dispose();
        _poolSemaphore?.Dispose();
    }
}
