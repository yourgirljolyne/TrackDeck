using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TrackDeck;

public sealed class BragiException(string message) : Exception(message);

/// <summary>
/// Corsair "Bragi" v1.5 protocol client, mirroring Corsair Web Hub's HidClient.
/// Header: [subDevice, 1, sessionId, commandId], response: [?, error, ?, commandId, status, data...].
/// Only the commands needed for the screen are implemented. Factory reset is deliberately absent.
/// </summary>
public sealed class Bragi : IDisposable
{
    public const ushort Vid = 0x1B1C;
    public const ushort PidAir99Wired = 0x2B21;
    public const ushort UsagePage = 0xFF42;
    public const ushort UsageId = 0x01;
    /// <summary>SLIPSTREAM and SLIPSTREAM v2 wireless receivers (only used to explain why the screen can't be reached).</summary>
    public static readonly ushort[] ReceiverIds = [0x2B00, 0x2B1B];

    // Command ids (Web Hub constants)
    private const byte CmdSet = 1, CmdGet = 2, CmdUnbind = 5, CmdWriteBegin = 6, CmdWriteCont = 7,
        CmdRead = 8, CmdDescribe = 9, CmdCreate = 11, CmdDelete = 12, CmdOpen = 13, CmdSession = 27;

    // Property ids
    public const ushort PropOperatingMode = 3, PropFreeStorage = 62, PropScreenPresent = 230,
        PropScreenWidth = 242, PropScreenHeight = 243, PropScreenIndex = 263;

    public const uint ModeSelfOperated = 1, ModeHostControlled = 2;

    private const byte StatusSuccess = 0, StatusInvalidState = 3;

    private readonly HidDevice _dev;
    private readonly byte _subDevice;
    private byte _session;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
    public Action<string>? Log { get; set; }

    public int ReportLength => _dev.OutputReportLength - 1;

    private Bragi(HidDevice dev, byte subDevice) { _dev = dev; _subDevice = subDevice; }

    public static Bragi? Open(ushort pid = PidAir99Wired)
    {
        var dev = HidDevice.Open(Vid, pid, UsagePage, UsageId);
        return dev == null ? null : new Bragi(dev, 0);
    }

    public string DevicePath => _dev.Path;

    // ---------------- session ----------------

    public async Task StartSessionAsync()
    {
        var payload = new byte[6];
        payload[0] = 1;
        RandomNumberGenerator.Fill(payload.AsSpan(1, 4));
        var resp = await RawAsync(Header(CmdSession, 0), payload);
        _session = resp.Length > 9 ? resp[9] : (byte)0;
        Log?.Invoke($"session started, id={_session}");
    }

    public async Task StopSessionAsync()
    {
        var payload = new byte[] { 2, 0, 0, 0, 0, _session };
        try { await RawAsync(Header(CmdSession, _session), payload); } catch { /* best effort */ }
    }

    // ---------------- properties ----------------

    public async Task<uint> GetPropertyAsync(ushort id)
    {
        var data = await CommandAsync(CmdGet, Le16(id));
        return data.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(data) : 0;
    }

    public async Task SetPropertyAsync(ushort id, uint value)
    {
        var p = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(p, id);
        BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(2), value);
        await CommandAsync(CmdSet, p);
    }

    // ---------------- files ----------------

    private const byte Buffer0 = 0;

    public async Task<byte[]?> ReadFileAsync(ushort fileId)
    {
        try
        {
            if (!await OpenFileAsync(fileId)) return null;
            var d = await CommandAsync(CmdDescribe, [Buffer0]);
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(2, 4));
            var result = new List<byte>(size);
            while (result.Count < size)
            {
                var chunk = await CommandAsync(CmdRead, [Buffer0]);
                if (chunk.Length == 0) break;
                result.AddRange(chunk.Take(Math.Min(chunk.Length, size - result.Count)));
            }
            return result.ToArray();
        }
        finally { await UnbindAsync(); }
    }

    public async Task WriteFileAsync(ushort fileId, byte[] content)
    {
        try
        {
            if (!await OpenFileAsync(fileId))
            {
                await UnbindAsync();
                await CommandAsync(CmdCreate, Le16(fileId));
                if (!await OpenFileAsync(fileId)) throw new BragiException($"cannot open file {fileId} after create");
            }

            int headerSize = 4;
            int first = ReportLength - (headerSize + 5);
            var begin = new byte[5 + Math.Min(first, content.Length)];
            begin[0] = Buffer0;
            BinaryPrimitives.WriteUInt32LittleEndian(begin.AsSpan(1), (uint)content.Length);
            content.AsSpan(0, begin.Length - 5).CopyTo(begin.AsSpan(5));
            await CommandAsync(CmdWriteBegin, begin);

            int step = first + 4;
            for (int off = first; off < content.Length; off += step)
            {
                int n = Math.Min(step, content.Length - off);
                var cont = new byte[1 + n];
                cont[0] = Buffer0;
                content.AsSpan(off, n).CopyTo(cont.AsSpan(1));
                await CommandAsync(CmdWriteCont, cont);
            }
        }
        finally { await UnbindAsync(); }
    }

    public async Task DeleteFileAsync(ushort fileId) => await CommandAsync(CmdDelete, Le16(fileId), throwOnStatus: false);

    private async Task<bool> OpenFileAsync(ushort fileId)
    {
        var p = new byte[3];
        p[0] = Buffer0;
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(1), fileId);
        var (status, _) = await CommandWithStatusAsync(CmdOpen, p);
        if (status == StatusSuccess) return true;
        if (status != StatusInvalidState) return false;
        await UnbindAsync();
        (status, _) = await CommandWithStatusAsync(CmdOpen, p);
        return status == StatusSuccess;
    }

    private async Task UnbindAsync()
    {
        try { await CommandWithStatusAsync(CmdUnbind, [1, Buffer0]); } catch { /* best effort */ }
    }

    // ---------------- transport ----------------

    private byte[] Header(byte cmd, byte session) => [_subDevice, 1, session, cmd];

    private async Task<byte[]> CommandAsync(byte cmd, byte[] payload, bool throwOnStatus = true)
    {
        var (status, data) = await CommandWithStatusAsync(cmd, payload);
        if (throwOnStatus && status != StatusSuccess)
            throw new BragiException($"command {cmd} failed with status {status}");
        return data;
    }

    /// <summary>Sends a command, restarting the session once if it expired. Returns (status, data).</summary>
    private async Task<(byte status, byte[] data)> CommandWithStatusAsync(byte cmd, byte[] payload)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var resp = await RawAsync(Header(cmd, _session), payload);
            bool expired = resp.Length > 1 && resp[1] == 15;
            bool invalidState = resp.Length > 4 && resp[4] == StatusInvalidState && cmd != CmdOpen;
            if ((expired || invalidState) && attempt == 0)
            {
                Log?.Invoke($"cmd {cmd}: session {(expired ? "expired" : "invalid state")}, restarting");
                await StartSessionAsync();
                continue;
            }
            if (resp.Length > 1 && resp[1] != 0)
                throw new BragiException($"command {cmd}: protocol error {resp[1]}");
            if (resp.Length < 5) throw new BragiException($"command {cmd}: short response");
            return (resp[4], resp.AsSpan(5).ToArray());
        }
        throw new BragiException($"command {cmd}: session could not be restored");
    }

    private async Task<byte[]> RawAsync(byte[] header, byte[] payload)
    {
        await _lock.WaitAsync();
        try
        {
            var pkt = new byte[header.Length + payload.Length];
            header.CopyTo(pkt, 0);
            payload.CopyTo(pkt, header.Length);

            using var cts = new CancellationTokenSource(Timeout);
            await _dev.WriteAsync(pkt, cts.Token);
            // Skip unsolicited notifications until the reply with our command id arrives.
            while (true)
            {
                var resp = await _dev.ReadAsync(cts.Token);
                if (resp.Length > 3 && resp[3] == header[3]) return resp;
                Log?.Invoke($"ignored report: {Convert.ToHexString(resp.AsSpan(0, Math.Min(12, resp.Length)))}");
            }
        }
        catch (OperationCanceledException)
        {
            throw new BragiException($"command {header[3]} timed out");
        }
        finally { _lock.Release(); }
    }

    private static byte[] Le16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }

    public void Dispose() { _dev.Dispose(); _lock.Dispose(); }
}
