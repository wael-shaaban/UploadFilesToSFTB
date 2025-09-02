using System.ComponentModel.DataAnnotations;

namespace SFTB_Demo.Models;

public class UploadFileRequest
{
    [Required]
    public IFormFile File { get; set; }

    public string? RemotePath { get; set; }
}
