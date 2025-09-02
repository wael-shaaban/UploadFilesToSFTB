using Microsoft.AspNetCore.Mvc;
using SFTB_Demo.Interfaces;
using SFTB_Demo.Models;

namespace SFTB_Demo.Controllers;


[ApiController]
[Route("api/[controller]")]
public class SFTBController : ControllerBase
{
    private readonly ISftpService _sftpService;
    private readonly ILogger<SFTBController> _logger;

    public SFTBController(ISftpService sftpService, ILogger<SFTBController> logger)
    {
        _sftpService = sftpService;
        _logger = logger;
    }

    /// <summary>
    /// Upload a file to the SFTP server
    /// </summary>
    [HttpPost("upload")]
    public async Task<IActionResult> UploadFile([FromForm] UploadFileRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }
        var moduleName = "Asset";
        var rowNumber = 1;

        var result = await _sftpService.UploadFileAsync(request.File, moduleName, rowNumber, request.RemotePath);

        return result.Success
            ? Ok(result)
            : BadRequest(result);
    }

    /// <summary>
    /// Download a file from the SFTP server
    /// </summary>
    [HttpGet("download")]
    public async Task<IActionResult> DownloadFile([FromQuery] string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest("File path is required");
        }

        var result = await _sftpService.DownloadFileAsync(filePath);

        if (!result.Success)
        {
            return BadRequest(result);
        }

        var fileData = result.Data as dynamic;
        var fileName = fileData?.FileName as string ?? "download";
        var content = fileData?.Content as byte[];

        if (content == null)
        {
            return BadRequest("File content is null");
        }

        return File(content, "application/octet-stream", fileName);
    }

    /// <summary>
    /// Delete a file from the SFTP server
    /// </summary>
    [HttpDelete("delete")]
    public async Task<IActionResult> DeleteFile([FromQuery] string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest("File path is required");
        }

        var result = await _sftpService.DeleteFileAsync(filePath);

        return result.Success
            ? Ok(result)
            : BadRequest(result);
    }

    /// <summary>
    /// Get list of files from the SFTP server
    /// </summary>
    [HttpGet("files")]
    public async Task<IActionResult> GetFileList([FromQuery] string? path = null)
    {
        var result = await _sftpService.GetFileListAsync(path);

        return result.Success
            ? Ok(result)
            : BadRequest(result);
    }

    /// <summary>
    /// Create a directory on the SFTP server
    /// </summary>
    [HttpPost("directory")]
    public async Task<IActionResult> CreateDirectory([FromBody] string directoryPath)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            return BadRequest("Directory path is required");
        }

        var result = await _sftpService.CreateDirectoryAsync(directoryPath);

        return result.Success
            ? Ok(result)
            : BadRequest(result);
    }

    /// <summary>
    /// Check if a file exists on the SFTP server
    /// </summary>
    [HttpGet("exists")]
    public async Task<IActionResult> FileExists([FromQuery] string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return BadRequest("File path is required");
        }

        var exists = await _sftpService.FileExistsAsync(filePath);

        return Ok(new { exists, filePath });
    }

    /// <summary>
    /// Move/rename a file on the SFTP server
    /// </summary>
    [HttpPut("move")]
    public async Task<IActionResult> MoveFile([FromQuery] string sourceFile, [FromQuery] string destinationFile)
    {
        if (string.IsNullOrEmpty(sourceFile) || string.IsNullOrEmpty(destinationFile))
        {
            return BadRequest("Both source and destination file paths are required");
        }

        var result = await _sftpService.MoveFileAsync(sourceFile, destinationFile);

        return result.Success
            ? Ok(result)
            : BadRequest(result);
    }
}
