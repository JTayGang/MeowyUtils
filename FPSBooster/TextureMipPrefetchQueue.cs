using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FPSBooster;

/// <summary>
/// Reads mip payloads from coarse to fine on one background worker and exposes a small bounded
/// queue to the UI thread. The upload scheduler may hold one dequeued mip across a frame boundary
/// when its scheduling policy reaches the current frame budget. With channel capacity two,
/// temporary CPU memory is bounded to two queued payloads, one payload held by the UI scheduler,
/// and the payload currently being read, plus the most recently uploaded payload retained by the
/// GPU probe for optional readback verification. The existing per-mip readers still open their source
/// file independently for each mip.
/// </summary>
internal sealed class TextureMipPrefetchQueue : IDisposable
{
    public const int DefaultCapacity = 2;

    private readonly TextureFileReport _report;
    private readonly string? _looseFilePath;
    private readonly long _loosePayloadOffset;
    private readonly long _looseFileLength;
    private readonly DateTime _looseLastWriteUtc;
    private readonly Channel<TextureMipReadReport> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _producerTask;
    private Exception? _error;
    private int _disposed;

    public TextureMipPrefetchQueue(
        TextureFileReport report,
        string? looseFilePath,
        long loosePayloadOffset,
        long looseFileLength,
        DateTime looseLastWriteUtc,
        int capacity = DefaultCapacity)
    {
        if (report.MipCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(report), "The texture must contain at least one mip.");
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _report = report;
        _looseFilePath = string.IsNullOrWhiteSpace(looseFilePath) ? null : looseFilePath;
        _loosePayloadOffset = loosePayloadOffset;
        _looseFileLength = looseFileLength;
        _looseLastWriteUtc = looseLastWriteUtc;
        _channel = Channel.CreateBounded<TextureMipReadReport>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

        _producerTask = Task.Run(ProduceAsync);
    }

    public Exception? Error => Volatile.Read(ref _error);
    public bool ProducerCompleted => _producerTask.IsCompleted;
    public bool IsDrained => _channel.Reader.Completion.IsCompleted;

    public bool TryDequeue(out TextureMipReadReport? result)
    {
        if (_channel.Reader.TryRead(out var item))
        {
            result = item;
            return true;
        }

        result = null;
        return false;
    }

    private async Task ProduceAsync()
    {
        try
        {
            CancellationToken token = _cancellation.Token;
            for (int mipIndex = _report.MipCount - 1; mipIndex >= 0; mipIndex--)
            {
                token.ThrowIfCancellationRequested();
                TextureMipReadReport mip = _looseFilePath != null
                    ? TextureStreamingService.ReadLooseMipPayload(
                        _looseFilePath,
                        _report,
                        mipIndex,
                        _loosePayloadOffset,
                        _looseFileLength,
                        _looseLastWriteUtc,
                        token)
                    : TextureStreamingService.ReadMipGroup(_report, mipIndex, token);

                await _channel.Writer.WriteAsync(mip, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // The UI stopped the sequence or changed sources. Buffered results are discarded by the owner.
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _error, ex);
        }
        finally
        {
            // Publish errors through the Error property instead of faulting Channel.Reader.Completion;
            // this keeps completion observation non-throwing for the UI polling path.
            _channel.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        // The producer can still be inside a synchronous file read. Dispose its token source only
        // after it exits, rather than racing CancellationToken access on the worker thread.
        if (_producerTask.IsCompleted)
        {
            _cancellation.Dispose();
        }
        else
        {
            _ = _producerTask.ContinueWith(
                static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                _cancellation,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
