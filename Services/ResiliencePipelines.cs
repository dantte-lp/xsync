using System.Net.Sockets;
using Polly;
using Polly.Retry;
using Renci.SshNet.Common;

namespace Xsync.Services;

public static class ResiliencePipelines
{
    public static ResiliencePipeline CreateSftpPipeline()
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<SshException>()
                    .Handle<SshConnectionException>()
                    .Handle<SocketException>()
                    .Handle<IOException>(),
                MaxRetryAttempts = 5,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
            })
            .AddTimeout(TimeSpan.FromMinutes(30))
            .Build();
    }
}
