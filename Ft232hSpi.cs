using System.Runtime.InteropServices;
using System.Text;

namespace FTFlash;

public sealed class DeviceInfo
{
    public int Index;
    public string Serial = "";
    public string Description = "";
    public uint Type;

    public override string ToString() => $"{Description}  [{Serial}]";
}

/// <summary>A group of SPI transactions sent to the FT232H in one USB transfer.</summary>
public sealed class SpiBatch
{
    internal readonly List<byte> Cmd = new();
    internal int Expect;

    /// <summary>Adds one chip-select-framed transaction: write tx, then read rx bytes.</summary>
    public SpiBatch Add(byte[] tx, int rx = 0)
    {
        if (tx.Length > 65536 || rx > 65536)
            throw new ArgumentException("Transfer too large for one MPSSE command (max 65536 bytes).");

        Cmd.AddRange(new byte[] { 0x80, 0x00, 0x0B });            // CS low  (ADBUS3 = 0)
        if (tx.Length > 0)
        {
            int n = tx.Length - 1;
            Cmd.Add(0x11);                                          // write bytes, -ve edge, MSB first (mode 0)
            Cmd.Add((byte)n);
            Cmd.Add((byte)(n >> 8));
            Cmd.AddRange(tx);
        }
        if (rx > 0)
        {
            int n = rx - 1;
            Cmd.Add(0x20);                                          // read bytes, +ve edge, MSB first (mode 0)
            Cmd.Add((byte)n);
            Cmd.Add((byte)(n >> 8));
            Expect += rx;
        }
        Cmd.AddRange(new byte[] { 0x80, 0x08, 0x0B });            // CS high (ADBUS3 = 1)
        return this;
    }

    /// <summary>
    /// Inserts a hardware-timed pause (CS stays high): clocks SCK for clockBytes * 8 cycles with no data.
    /// Lets the MPSSE wait for the flash without a USB round trip.
    /// </summary>
    public SpiBatch Delay(int clockBytes)
    {
        while (clockBytes > 0)
        {
            int n = Math.Min(clockBytes, 65536) - 1;
            Cmd.Add(0x8F);
            Cmd.Add((byte)n);
            Cmd.Add((byte)(n >> 8));
            clockBytes -= n + 1;
        }
        return this;
    }
}

/// <summary>
/// SPI master (mode 0) on an FT232H using MPSSE.
/// Pins: AD0 = SCK, AD1 = MOSI, AD2 = MISO, AD3 = CS.
/// </summary>
public sealed class Ft232hSpi : IDisposable
{
    // ---- D2XX native API -------------------------------------------------------------
    private const string Dll = "ftd2xx.dll";

    [DllImport(Dll)] private static extern uint FT_CreateDeviceInfoList(out uint numDevs);
    [DllImport(Dll)] private static extern uint FT_GetDeviceInfoDetail(uint index, out uint flags, out uint type,
        out uint id, out uint locId, byte[] serial, byte[] description, out IntPtr handle);
    [DllImport(Dll)] private static extern uint FT_Open(int index, out IntPtr handle);
    [DllImport(Dll)] private static extern uint FT_Close(IntPtr h);
    [DllImport(Dll)] private static extern uint FT_ResetDevice(IntPtr h);
    [DllImport(Dll)] private static extern uint FT_SetBitMode(IntPtr h, byte mask, byte mode);
    [DllImport(Dll)] private static extern uint FT_SetTimeouts(IntPtr h, uint readMs, uint writeMs);
    [DllImport(Dll)] private static extern uint FT_SetLatencyTimer(IntPtr h, byte ms);
    [DllImport(Dll)] private static extern uint FT_SetUSBParameters(IntPtr h, uint inSize, uint outSize);
    [DllImport(Dll)] private static extern uint FT_SetChars(IntPtr h, byte ev, byte evEn, byte err, byte errEn);
    [DllImport(Dll)] private static extern uint FT_SetFlowControl(IntPtr h, ushort flow, byte xon, byte xoff);
    [DllImport(Dll)] private static extern uint FT_Purge(IntPtr h, uint mask);
    [DllImport(Dll)] private static extern uint FT_Read(IntPtr h, ref byte buf, uint count, out uint got);
    [DllImport(Dll)] private static extern uint FT_Write(IntPtr h, byte[] buf, uint count, out uint written);

    private const uint FT_DEVICE_232H = 8;
    private IntPtr _h = IntPtr.Zero;

    public bool IsOpen => _h != IntPtr.Zero;

    /// <summary>Actual SCK frequency in Hz.</summary>
    public double ClockHz { get; private set; } = 1e6;

    private static string Describe(uint st) => st switch
    {
        1 => "invalid handle",
        2 => "device not found",
        3 => "device not opened (in use by another program?)",
        4 => "I/O error",
        5 => "insufficient resources",
        6 => "invalid parameter",
        _ => "error"
    };

    private static void Check(uint st, string what)
    {
        if (st != 0) throw new IOException($"{what} failed: FT_STATUS {st} ({Describe(st)})");
    }

    /// <summary>Lists connected FT232H / FT2232H / FT4232H devices.</summary>
    public static List<DeviceInfo> Enumerate()
    {
        var list = new List<DeviceInfo>();
        try
        {
            Check(FT_CreateDeviceInfoList(out uint n), "FT_CreateDeviceInfoList");
            for (uint i = 0; i < n; i++)
            {
                var serial = new byte[16];
                var desc = new byte[64];
                if (FT_GetDeviceInfoDetail(i, out _, out uint type, out _, out _, serial, desc, out _) != 0)
                    continue;
                if (type != FT_DEVICE_232H && type != 6 && type != 7) continue;   // MPSSE-capable "H" devices
                list.Add(new DeviceInfo
                {
                    Index = (int)i,
                    Type = type,
                    Serial = Encoding.ASCII.GetString(serial).Split('\0')[0],
                    Description = Encoding.ASCII.GetString(desc).Split('\0')[0]
                });
            }
        }
        catch (DllNotFoundException)
        {
            throw new IOException("ftd2xx.dll not found. Install the FTDI D2XX driver (https://ftdichip.com/drivers/d2xx-drivers/).");
        }
        return list;
    }

    /// <summary>Opens the device and configures MPSSE for SPI mode 0 at the requested clock.</summary>
    public void Open(int deviceIndex, double clockHz)
    {
        if (IsOpen) Close();

        int div = (int)Math.Round(30_000_000.0 / clockHz) - 1;      // SCK = 60 MHz / ((1 + div) * 2)
        div = Math.Clamp(div, 0, 65535);
        ClockHz = 30_000_000.0 / (div + 1);

        Check(FT_Open(deviceIndex, out _h), "FT_Open");
        try
        {
            Check(FT_ResetDevice(_h), "FT_ResetDevice");
            Check(FT_Purge(_h, 3), "FT_Purge");
            Check(FT_SetUSBParameters(_h, 65536, 65536), "FT_SetUSBParameters");
            Check(FT_SetChars(_h, 0, 0, 0, 0), "FT_SetChars");
            Check(FT_SetTimeouts(_h, 10000, 10000), "FT_SetTimeouts");
            Check(FT_SetLatencyTimer(_h, 1), "FT_SetLatencyTimer");
            Check(FT_SetFlowControl(_h, 0x0100, 0, 0), "FT_SetFlowControl");   // RTS/CTS
            Check(FT_SetBitMode(_h, 0x00, 0x00), "FT_SetBitMode(reset)");
            Thread.Sleep(20);
            Check(FT_SetBitMode(_h, 0x00, 0x02), "FT_SetBitMode(MPSSE)");
            Thread.Sleep(50);
            Check(FT_Purge(_h, 3), "FT_Purge");

            // Sync check: a bogus opcode (0xAB) must be echoed as 0xFA 0xAB
            WriteAll(new byte[] { 0xAB });
            var sync = new byte[2];
            ReadInto(sync, 0, 2);
            if (sync[0] != 0xFA || sync[1] != 0xAB)
                throw new IOException("MPSSE synchronisation failed.");

            WriteAll(new byte[]
            {
                0x8A,                       // disable clock divide-by-5 (60 MHz base)
                0x97,                       // disable adaptive clocking
                0x8D,                       // disable 3-phase clocking
                0x86, (byte)div, (byte)(div >> 8),   // clock divisor
                0x85,                       // loopback off
                0x80, 0x08, 0x0B,           // low byte: CS high; SCK, MOSI, CS = outputs
                0x82, 0x00, 0x00            // high byte: all inputs
            });
        }
        catch
        {
            Close();
            throw;
        }
    }

    public void Close()
    {
        if (_h == IntPtr.Zero) return;
        try
        {
            // Tri-state all pins so the target chip is released
            WriteAll(new byte[] { 0x80, 0x00, 0x00, 0x82, 0x00, 0x00 });
            FT_SetBitMode(_h, 0x00, 0x00);
        }
        catch { /* ignore */ }
        FT_Close(_h);
        _h = IntPtr.Zero;
    }

    public void Dispose() => Close();

    // ---- Low-level I/O ---------------------------------------------------------------
    private void WriteAll(byte[] data)
    {
        Check(FT_Write(_h, data, (uint)data.Length, out uint written), "FT_Write");
        if (written != data.Length) throw new IOException("Short write to FT232H.");
    }

    /// <summary>Blocking read straight into the destination array (no intermediate copies, no polling loop).</summary>
    private void ReadInto(byte[] dest, int offset, int count)
    {
        int got = 0;
        while (got < count)
        {
            int take = Math.Min(count - got, 65536);
            Check(FT_Read(_h, ref dest[offset + got], (uint)take, out uint r), "FT_Read");
            if (r == 0) throw new TimeoutException("Timed out waiting for data from FT232H.");
            got += (int)r;
        }
    }

    /// <summary>Sends the batch and reads all returned bytes into dest at offset.</summary>
    public void ExecuteInto(SpiBatch batch, byte[] dest, int offset)
    {
        if (!IsOpen) throw new InvalidOperationException("Not connected.");
        var cmd = new byte[batch.Cmd.Count + 1];
        batch.Cmd.CopyTo(cmd);
        cmd[^1] = 0x87;                                  // send immediate
        WriteAll(cmd);
        if (batch.Expect > 0) ReadInto(dest, offset, batch.Expect);
    }

    /// <summary>Sends the batch and returns all bytes read (concatenated, in order).</summary>
    public byte[] Execute(SpiBatch batch)
    {
        var result = new byte[batch.Expect];
        ExecuteInto(batch, result, 0);
        return result;
    }
}
