using Microsoft.AspNetCore.Mvc;
using SFTB_Demo.Interfaces;
using SFTB_Demo.Models;
using System.ComponentModel.DataAnnotations;

namespace SFTB_Demo.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AdvancedSftpController : ControllerBase
{
    private readonly IEnhancedSftpService _sftpService;

    public AdvancedSftpController(IEnhancedSftpService sftpService)
    {
        _sftpService = sftpService;
    }
    // Upload single file
    [HttpPost("upload")]
    public async Task<IActionResult> Upload([FromForm] UploadWithProgressDto uploadWithProgress)
    {
        var result = await _sftpService.UploadFileAsync(uploadWithProgress.File, uploadWithProgress.RemotePath);
        return Ok(result);
    }
    public class UploadWithProgressDto
    {
        public string? RemotePath { get; set; }
        [Required]
        public IFormFile? File { get; set; }
    }
    public class BatchUploadDto
    {
        public string? RemotePath { get; set; }
        [Required]
        public List<IFormFile>? Files { get; set; }
    }
    // Upload with progress
    [HttpPost("upload-with-progress")]
    public async Task<IActionResult> UploadWithProgress([FromForm]UploadWithProgressDto uploadWithProgress)
    {
        var progress = new Progress<TransferProgress>(p =>
        {
            Console.WriteLine($"Progress: {p.ProgressPercentage:F2}% ({p.BytesTransferred}/{p.TotalBytes})");
        });

        var result = await _sftpService.UploadFileWithProgressAsync(uploadWithProgress.File, uploadWithProgress.RemotePath, progress);
        return Ok(result);
    }

    // Batch upload
    [HttpPost("batch-upload")]
    public async Task<IActionResult> BatchUpload([FromForm] BatchUploadDto batchUpload)
    {
        var progress = new Progress<BatchTransferProgress>(p =>
        {
            Console.WriteLine($"Batch Progress: {p.ProgressPercentage:F2}% ({p.ProcessedFiles}/{p.TotalFiles})");
        });

        var result = await _sftpService.BatchUploadAsync(batchUpload.Files, batchUpload.RemotePath, progress);
        return Ok(result);
    }

    // Download file
    [HttpGet("download")]
    public async Task<IActionResult> Download([FromQuery] string remoteFilePath)
    {
        var result = await _sftpService.DownloadFileAsync(remoteFilePath);
        if (!result.Success) return BadRequest(result);

        var fileData = (dynamic)result.Data!;
        return File((byte[])fileData.Content, "application/octet-stream", fileData.FileName);
    }

    // Delete file
    [HttpDelete("delete")]
    public async Task<IActionResult> Delete([FromQuery] string remoteFilePath)
    {
        var result = await _sftpService.DeleteFileAsync(remoteFilePath);
        return Ok(result);
    }

    // Move file
    [HttpPost("move")]
    public async Task<IActionResult> Move([FromQuery] string sourceFile, [FromQuery] string destinationFile)
    {
        var result = await _sftpService.MoveFileAsync(sourceFile, destinationFile);
        return Ok(result);
    }

    // Copy file
    [HttpPost("copy")]
    public async Task<IActionResult> Copy([FromQuery] string sourceFile, [FromQuery] string destinationFile)
    {
        var result = await _sftpService.CopyFileAsync(sourceFile, destinationFile);
        return Ok(result);
    }

    // File exists check
    [HttpGet("exists")]
    public async Task<IActionResult> Exists([FromQuery] string remoteFilePath)
    {
        var exists = await _sftpService.FileExistsAsync(remoteFilePath);
        return Ok(new { File = remoteFilePath, Exists = exists });
    }

    // File list
    [HttpGet("list")]
    public async Task<IActionResult> List([FromQuery] string? remotePath = null)
    {
        var result = await _sftpService.GetFileListAsync(remotePath);
        return Ok(result);
    }

    // Create directory
    [HttpPost("create-dir")]
    public async Task<IActionResult> CreateDir([FromQuery] string remotePath)
    {
        var result = await _sftpService.CreateDirectoryAsync(remotePath);
        return Ok(result);
    }

    // Get checksum
    [HttpGet("checksum")]
    public async Task<IActionResult> Checksum([FromQuery] string remoteFilePath, [FromQuery] string algorithm = "SHA256")
    {
        var result = await _sftpService.GetFileChecksumAsync(remoteFilePath, algorithm);
        return Ok(result);
    }

    // Sync directory (local to remote only)
    [HttpPost("sync")]
    public async Task<IActionResult> Sync([FromQuery] string localPath, [FromQuery] string remotePath)
    {
        var progress = new Progress<SyncProgress>(p =>
        {
            Console.WriteLine($"Sync {p.ProgressPercentage:F2}% - {p.CurrentFile}");
        });

        var result = await _sftpService.SyncDirectoryAsync(localPath, remotePath, SyncDirection.LocalToRemote, progress);
        return Ok(result);
    }

    // Get server info
    [HttpGet("server-info")]
    public async Task<IActionResult> ServerInfo()
    {
        var result = await _sftpService.GetServerInfoAsync();
        return Ok(result);
    }
}