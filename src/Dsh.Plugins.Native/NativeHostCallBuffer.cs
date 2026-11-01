using System.Text;
using System.Text.Json;

namespace Dsh.Plugins.Native;

/// <summary>ABI v2 host-call replies retain success or failure until the same thread collects them.</summary>
public sealed unsafe class NativeHostCallBuffer : IDisposable
{
    private readonly NativeResultBuffer _results = new();

    public int Invoke(string operation, string argumentsJson, byte* output, int capacity, Func<string, string, string> call)
        => _results.Invoke(0, operation, argumentsJson, output, capacity, () => Encode(operation, argumentsJson, call));

    private static ReadOnlyMemory<byte> Encode(string operation, string argumentsJson, Func<string, string, string> call)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        try
        {
            var result = call(operation, argumentsJson);
            if (Encoding.UTF8.GetByteCount(result) > DshNativePluginAbi.MaxResultBytes - 128)
                throw new InvalidOperationException("host operation response exceeds the native ABI limit");
            using var document = JsonDocument.Parse(result);
            writer.WritePropertyName("result");
            writer.WriteRawValue(result, skipInputValidation: true);
        }
        catch (Exception error)
        {
            var message = error.Message;
            writer.WriteString("error", message.Length <= 2048 ? message : message[..2048]);
        }
        writer.WriteEndObject();
        writer.Flush();
        return new ReadOnlyMemory<byte>(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    public void Dispose() => _results.Dispose();
}

/// <summary>One reply per thread, at most sixteen replies and four MiB total per plugin boundary.</summary>
internal sealed unsafe class NativeResultBuffer : IDisposable
{
    private readonly Lock _sync = new();
    // Retain the thread object because terminated threads can recycle their numeric IDs.
    private readonly Dictionary<Thread, RetainedResult> _pending = [];
    private int _retainedBytes;
    private bool _disposed;

    public int Invoke(int handle, string operation, string input, byte* output, int capacity, Func<ReadOnlyMemory<byte>> execute)
    {
        lock (_sync)
        {
            if (_disposed || capacity < 0)
                return DshNativePluginAbi.Error;
            var thread = Thread.CurrentThread;
            if (!_pending.TryGetValue(thread, out var retained))
            {
                var inputBytes = Encoding.UTF8.GetByteCount(operation) + Encoding.UTF8.GetByteCount(input);
                if (_pending.Count >= 16 || inputBytes >= DshNativePluginAbi.MaxResultBytes - _retainedBytes)
                    return DshNativePluginAbi.Error;
                var result = execute();
                var length = result.Length;
                if (length > DshNativePluginAbi.MaxResultBytes - _retainedBytes - inputBytes)
                    throw new InvalidOperationException("native retained response budget exceeded");
                retained = new RetainedResult(handle, operation, input, result, length + inputBytes);
                _pending.Add(thread, retained);
                _retainedBytes += retained.Size;
            }
            else if (retained.Handle != handle || retained.Operation != operation || retained.Input != input)
            {
                // Do not replace an uncollected reply or accidentally replay its side effect.
                return DshNativePluginAbi.Error;
            }
            if (output is null || capacity < retained.Bytes.Length)
                return retained.Bytes.Length;
            retained.Bytes.Span.CopyTo(new Span<byte>(output, capacity));
            _pending.Remove(thread);
            _retainedBytes -= retained.Size;
            return retained.Bytes.Length;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _pending.Clear();
            _retainedBytes = 0;
        }
    }

    private sealed record RetainedResult(int Handle, string Operation, string Input, ReadOnlyMemory<byte> Bytes, int Size);
}
