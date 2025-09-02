namespace SFTB_Demo.Settings;
public class SftpConfig
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyPassphrase { get; set; }
    public int ConnectionTimeout { get; set; } = 30000; // 30 seconds
    public string RootDirectory { get; set; } = "/";
}