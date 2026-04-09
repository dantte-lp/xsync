using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Xsync.Services;

public sealed class RemoteCommandService : IDisposable
{
    private readonly SshClient _ssh;
    private readonly ILogger _logger;

    public RemoteCommandService(SyncConfig config, ILogger<RemoteCommandService> logger)
    {
        _logger = logger;
        var keyFile = new PrivateKeyFile(config.SshKeyPath);
        var connInfo = new ConnectionInfo(
            config.Hostname, config.Port, config.Username,
            new PrivateKeyAuthenticationMethod(config.Username, keyFile));

        PreferAesGcm(connInfo);
        _ssh = new SshClient(connInfo);
    }

    public void Connect()
    {
        _ssh.Connect();
        _logger.LogDebug("SSH command channel connected");
    }

    public (int ExitCode, string Output) Exec(string command, TimeSpan? timeout = null)
    {
        _logger.LogDebug("SSH exec: {Command}", command);
        if (!_ssh.IsConnected)
        {
            _ssh.Connect();
        }

        using var cmd = _ssh.CreateCommand(command);
        cmd.CommandTimeout = timeout ?? TimeSpan.FromMinutes(10);
        cmd.Execute();
        return (cmd.ExitStatus ?? -1, cmd.Result.Trim());
    }

    public byte[]? ComputeRemoteHash(string remotePath)
    {
        var (code, output) = Exec($"xxh128sum '{remotePath}'", TimeSpan.FromHours(1));
        if (code != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var hex = output.Split(' ')[0];
        return Convert.FromHexString(hex);
    }

    public (bool Exists, long Size) FileExists(string remotePath)
    {
        var (code, output) = Exec($"stat -c '%s' '{remotePath}' 2>/dev/null");
        if (code == 0 && long.TryParse(output, out var size))
        {
            return (true, size);
        }

        return (false, 0);
    }

    public void Rename(string from, string to) =>
        Exec($"mv '{from}' '{to}'");

    public void Mkdir(string path) =>
        Exec($"mkdir -p '{path}'");

    public void Dispose()
    {
        if (_ssh.IsConnected)
        {
            _ssh.Disconnect();
        }

        _ssh.Dispose();
    }

    private static void PreferAesGcm(ConnectionInfo connInfo)
    {
        if (!connInfo.Encryptions.TryGetValue("aes256-gcm@openssh.com", out var aesGcm))
        {
            return;
        }
        var others = connInfo.Encryptions
            .Where(kv => !string.Equals(kv.Key, "aes256-gcm@openssh.com", StringComparison.Ordinal))
            .ToList();
        connInfo.Encryptions.Clear();
        connInfo.Encryptions.Add("aes256-gcm@openssh.com", aesGcm);
        foreach (var kv in others)
        {
            connInfo.Encryptions.Add(kv.Key, kv.Value);
        }
    }
}
