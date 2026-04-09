using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace Xsync.Services;

public sealed class SftpConnectionPool : IAsyncDisposable
{
    private readonly Channel<SftpClient> _pool;
    private readonly List<SftpClient> _allClients = [];
    private readonly SyncConfig _config;
    private readonly ILogger _logger;

    public SftpConnectionPool(SyncConfig config, ILogger<SftpConnectionPool> logger)
    {
        _config = config;
        _logger = logger;
        _pool = Channel.CreateBounded<SftpClient>(config.Parallelism);
    }

    public async Task InitializeAsync(int poolSize)
    {
        for (var i = 0; i < poolSize; i++)
        {
            var client = CreateClient();
            client.Connect();
            _allClients.Add(client);
            await _pool.Writer.WriteAsync(client).ConfigureAwait(false);
            _logger.LogDebug("SFTP connection {Index}/{Total} established", i + 1, poolSize);
        }

        _logger.LogInformation("SFTP pool initialized: {Count} connections", poolSize);
    }

    public async Task<SftpClient> RentAsync(CancellationToken ct = default)
    {
        var client = await _pool.Reader.ReadAsync(ct).ConfigureAwait(false);
        if (!client.IsConnected)
        {
            _logger.LogWarning("SFTP client disconnected, reconnecting...");
            try
            {
                client.Disconnect();
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Disconnect error (ignored): {Message}", ex.Message);
            }

            client.Connect();
        }

        return client;
    }

    public async Task ReturnAsync(SftpClient client)
    {
        await _pool.Writer.WriteAsync(client).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _pool.Writer.TryComplete();
        foreach (var client in _allClients)
        {
            try
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Cleanup error (ignored): {Message}", ex.Message);
            }

            client.Dispose();
        }

        _allClients.Clear();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private SftpClient CreateClient()
    {
        var keyFile = new PrivateKeyFile(_config.SshKeyPath);
        var connInfo = new ConnectionInfo(
            _config.Hostname, _config.Port, _config.Username,
            new PrivateKeyAuthenticationMethod(_config.Username, keyFile))
        {
            Encoding = System.Text.Encoding.UTF8,
        };

        if (connInfo.Encryptions.TryGetValue("aes256-gcm@openssh.com", out var aesGcm))
        {
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

        return new SftpClient(connInfo)
        {
            BufferSize = 32768,
            OperationTimeout = TimeSpan.FromHours(2),
        };
    }
}
