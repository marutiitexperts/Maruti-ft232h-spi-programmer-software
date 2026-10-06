namespace FTFlash;

public sealed class WriteStats
{
    public bool NothingToDo;
    public int Pages;
    public int SectorsErased;
    public int Retries;
    public double FinalDelayUs;
}

/// <summary>Generic JEDEC SPI NOR flash operations (25-series: Winbond, Macronix, GigaDevice, Micron, ...).</summary>
public sealed class SpiFlash
{
    public const int PageSize = 256;
    public const int SectorSize = 4096;

    private const int MaxChunk = 65536;        // max bytes per MPSSE read command
    private const int BatchChunks = 8;         // read commands queued per USB round trip (512 KB)
    private const int TurboWindow = 65536;     // turbo write: program this much, then verify it
    private const int TurboPagesPerUsb = 32;   // pages queued per USB write in turbo mode
    private const double StartDelayUs = 700;   // initial per-page wait in turbo mode (auto-increases)
    private const double MaxDelayUs = 6000;

    private readonly Ft232hSpi _spi;

    /// <summary>Flash size in bytes. Chips above 16 MB use 4-byte address opcodes automatically.</summary>
    public int Capacity { get; set; }

    private bool FourByte => Capacity > 16 * 1024 * 1024;

    public SpiFlash(Ft232hSpi spi) => _spi = spi;

    // ---- helpers ---------------------------------------------------------------------
    private byte[] Cmd(byte op3, byte op4, int addr, byte[]? data = null, int off = 0, int len = 0)
    {
        var l = new List<byte>(8 + len) { FourByte ? op4 : op3 };
        if (FourByte) l.Add((byte)(addr >> 24));
        l.Add((byte)(addr >> 16));
        l.Add((byte)(addr >> 8));
        l.Add((byte)addr);
        if (data != null && len > 0)
            l.AddRange(new ArraySegment<byte>(data, off, len));
        return l.ToArray();
    }

    private static readonly byte[] WriteEnable = { 0x06 };

    // ---- identification / status -----------------------------------------------------
    public (byte Manufacturer, byte Type, byte Capacity) ReadId()
    {
        var r = _spi.Execute(new SpiBatch().Add(new byte[] { 0x9F }, 3));
        return (r[0], r[1], r[2]);
    }

    public byte ReadStatus() => _spi.Execute(new SpiBatch().Add(new byte[] { 0x05 }, 1))[0];

    /// <summary>Polls the WIP bit. Each poll is one USB round trip, so no extra sleeping is needed for short waits.</summary>
    public void WaitReady(int timeoutMs, CancellationToken ct, int pollSleepMs = 0)
    {
        long start = Environment.TickCount64;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if ((ReadStatus() & 0x01) == 0) return;
            if (Environment.TickCount64 - start > timeoutMs)
                throw new TimeoutException("Timed out waiting for flash to finish (WIP stuck).");
            if (pollSleepMs > 0) Thread.Sleep(pollSleepMs);
        }
    }

    /// <summary>Writes the status register (e.g. 0x00 clears block-protect bits).</summary>
    public void WriteStatus(byte value, CancellationToken ct)
    {
        _spi.Execute(new SpiBatch().Add(WriteEnable).Add(new byte[] { 0x01, value }));
        WaitReady(2000, ct, 5);
    }

    // ---- read ------------------------------------------------------------------------
    public byte[] Read(int addr, int length, IProgress<int>? progress, CancellationToken ct)
    {
        var result = new byte[length];
        ReadInto(addr, result, 0, length, progress, ct);
        return result;
    }

    /// <summary>
    /// Streams flash data into dest. Eight 64 KB read commands are queued per USB write (the commands are tiny),
    /// so the SPI bus runs back-to-back and the transfer is limited only by the SCK clock.
    /// </summary>
    public void ReadInto(int addr, byte[] dest, int destOff, int length, IProgress<int>? progress, CancellationToken ct)
    {
        int done = 0, lastPct = -1;
        while (done < length)
        {
            ct.ThrowIfCancellationRequested();
            var batch = new SpiBatch();
            int queued = 0;
            for (int i = 0; i < BatchChunks && done + queued < length; i++)
            {
                int n = Math.Min(MaxChunk, length - done - queued);
                batch.Add(Cmd(0x03, 0x13, addr + done + queued), n);
                queued += n;
            }
            _spi.ExecuteInto(batch, dest, destOff + done);
            done += queued;

            int pct = (int)(done * 100L / length);
            if (pct != lastPct) { progress?.Report(pct); lastPct = pct; }
        }
    }

    // ---- verify ----------------------------------------------------------------------
    /// <summary>Streams the flash against expected data (from address 0) and stops at the first mismatch.</summary>
    public void Verify(byte[] expected, IProgress<int>? progress, CancellationToken ct)
    {
        var buf = new byte[BatchChunks * MaxChunk];
        int done = 0, lastPct = -1;
        while (done < expected.Length)
        {
            int n = Math.Min(buf.Length, expected.Length - done);
            ReadInto(done, buf, 0, n, null, ct);

            int idx = new ReadOnlySpan<byte>(expected, done, n)
                .CommonPrefixLength(new ReadOnlySpan<byte>(buf, 0, n));
            if (idx < n)
                throw new InvalidDataException(
                    $"Verify FAILED at 0x{done + idx:X6}: file {expected[done + idx]:X2}, flash {buf[idx]:X2}.");

            done += n;
            int pct = (int)(done * 100L / expected.Length);
            if (pct != lastPct) { progress?.Report(pct); lastPct = pct; }
        }
    }

    // ---- erase -----------------------------------------------------------------------
    private void EraseCmd(byte op3, byte op4, int addr, CancellationToken ct)
    {
        _spi.Execute(new SpiBatch().Add(WriteEnable).Add(Cmd(op3, op4, addr)));
        WaitReady(30_000, ct);
    }

    public void EraseChip(CancellationToken ct)
    {
        _spi.Execute(new SpiBatch().Add(WriteEnable).Add(new byte[] { 0xC7 }));
        WaitReady(10 * 60 * 1000, ct, 100);   // large chips can take minutes
    }

    private static bool AllSet(bool[] a, int start, int count)
    {
        for (int i = start; i < start + count; i++) if (!a[i]) return false;
        return true;
    }

    // ---- program ---------------------------------------------------------------------
    private void ProgramPageSafe(byte[] image, int page, CancellationToken ct)
    {
        int off = page * PageSize, n = Math.Min(PageSize, image.Length - off);
        _spi.Execute(new SpiBatch().Add(WriteEnable).Add(Cmd(0x02, 0x12, off, image, off, n)));
        WaitReady(2000, ct);
    }

    /// <summary>
    /// Queues WREN + page program + a hardware-timed wait for pages[from..to), many pages per USB write.
    /// No status polling, so there are no USB round trips while programming.
    /// </summary>
    private void ProgramPagesTimed(List<int> pages, int from, int to, byte[] image, int delayBytes, CancellationToken ct)
    {
        var batch = new SpiBatch();
        int queued = 0;
        for (int k = from; k < to; k++)
        {
            int off = pages[k] * PageSize, n = Math.Min(PageSize, image.Length - off);
            batch.Add(WriteEnable).Add(Cmd(0x02, 0x12, off, image, off, n)).Delay(delayBytes);
            if (++queued == TurboPagesPerUsb || k == to - 1)
            {
                _spi.Execute(batch);
                batch = new SpiBatch();
                queued = 0;
                ct.ThrowIfCancellationRequested();
            }
        }
    }

    private void TurboProgram(List<int> pages, byte[] image, WriteStats stats, Action<string> log,
                              IProgress<int> progress, CancellationToken ct)
    {
        double delayUs = StartDelayUs;
        int pagesPerWindow = TurboWindow / PageSize;
        var buf = new byte[TurboWindow];
        int i = 0, done = 0, lastPct = -1;

        while (i < pages.Count)
        {
            ct.ThrowIfCancellationRequested();

            int window = pages[i] / pagesPerWindow;
            int j = i;
            while (j < pages.Count && pages[j] / pagesPerWindow == window) j++;

            int delayBytes = Math.Max(1, (int)Math.Ceiling(delayUs * 1e-6 * _spi.ClockHz / 8.0));
            ProgramPagesTimed(pages, i, j, image, delayBytes, ct);

            // Read back the span covering these pages and find any the chip skipped (still busy when the next page arrived)
            int spanStart = pages[i] * PageSize;
            int spanEnd = Math.Min(image.Length, (pages[j - 1] + 1) * PageSize);
            ReadInto(spanStart, buf, 0, spanEnd - spanStart, null, ct);

            List<int>? bad = null;
            for (int k = i; k < j; k++)
            {
                int off = pages[k] * PageSize, n = Math.Min(PageSize, image.Length - off);
                if (!new ReadOnlySpan<byte>(image, off, n).SequenceEqual(new ReadOnlySpan<byte>(buf, off - spanStart, n)))
                    (bad ??= new List<int>()).Add(pages[k]);
            }

            if (bad != null)
            {
                stats.Retries += bad.Count;
                delayUs = Math.Min(MaxDelayUs, Math.Max(delayUs * 1.6, delayUs + 150));
                foreach (int p in bad)
                {
                    ProgramPageSafe(image, p, ct);
                    int off = p * PageSize, n = Math.Min(PageSize, image.Length - off);
                    var rb = Read(off, n, null, ct);
                    if (!new ReadOnlySpan<byte>(image, off, n).SequenceEqual(rb))
                        throw new InvalidDataException(
                            $"Programming failed at 0x{off:X6}. Chip may be write-protected (try Unlock) or the wiring is marginal (try a lower clock).");
                }
            }

            done += j - i;
            i = j;
            int pct = (int)(done * 100L / pages.Count);
            if (pct != lastPct) { progress.Report(pct); lastPct = pct; }
        }

        stats.FinalDelayUs = delayUs;
    }

    // ---- smart write -----------------------------------------------------------------
    /// <summary>
    /// 1) Reads the chip (fast), 2) compares with the file per 4 KB sector,
    /// 3) erases only sectors that need a 0-&gt;1 bit change (merged into 64 KB blocks where possible),
    /// 4) programs only pages that differ. Bytes in the last sector beyond the file end are preserved.
    /// </summary>
    public WriteStats SmartWrite(byte[] data, bool turbo, Action<string> status, Action<string> log,
                                 IProgress<int> progress, CancellationToken ct)
    {
        var stats = new WriteStats();
        if (data.Length > Capacity) throw new ArgumentException("File is larger than the flash.");

        int len = Math.Min((data.Length + SectorSize - 1) / SectorSize * SectorSize, Capacity);

        status("Reading current flash contents...");
        var cur = Read(0, len, progress, ct);
        var target = (byte[])cur.Clone();
        Buffer.BlockCopy(data, 0, target, 0, data.Length);

        int sectors = (len + SectorSize - 1) / SectorSize;
        var needErase = new bool[sectors];
        int changed = 0, toErase = 0;
        for (int s = 0; s < sectors; s++)
        {
            int off = s * SectorSize, n = Math.Min(SectorSize, len - off);
            var c = new ReadOnlySpan<byte>(cur, off, n);
            var t = new ReadOnlySpan<byte>(target, off, n);
            if (c.SequenceEqual(t)) continue;
            changed++;
            for (int i = 0; i < n; i++)
            {
                // Programming can only clear bits; if the target needs a bit set that is currently 0, erase is required
                if ((c[i] & t[i]) != t[i]) { needErase[s] = true; toErase++; break; }
            }
        }

        if (changed == 0)
        {
            stats.NothingToDo = true;
            progress.Report(100);
            log("Flash already matches the file - nothing to write.");
            return stats;
        }
        log($"{changed} of {sectors} sectors differ, {toErase} need erase.");

        // ---- erase
        if (toErase > 0)
        {
            status("Erasing...");
            int s = 0, done = 0;
            while (s < sectors)
            {
                ct.ThrowIfCancellationRequested();
                if (!needErase[s]) { s++; continue; }

                int span;
                if (s % 16 == 0 && s + 16 <= sectors && (s + 16) * SectorSize <= Capacity && AllSet(needErase, s, 16))
                {
                    EraseCmd(0xD8, 0xDC, s * SectorSize, ct);   // 64 KB block
                    span = 16;
                }
                else
                {
                    EraseCmd(0x20, 0x21, s * SectorSize, ct);   // 4 KB sector
                    span = 1;
                }
                int fillLen = Math.Min(span * SectorSize, len - s * SectorSize);
                Array.Fill(cur, (byte)0xFF, s * SectorSize, fillLen);   // mirror the erase in our copy
                s += span;
                done += span;
                progress.Report(Math.Min(100, done * 100 / toErase));
            }
            stats.SectorsErased = toErase;
        }

        // ---- pages that still differ
        var pages = new List<int>();
        for (int p = 0; p * PageSize < len; p++)
        {
            int off = p * PageSize, n = Math.Min(PageSize, len - off);
            if (!new ReadOnlySpan<byte>(cur, off, n).SequenceEqual(new ReadOnlySpan<byte>(target, off, n)))
                pages.Add(p);
        }
        stats.Pages = pages.Count;
        if (pages.Count == 0) { progress.Report(100); return stats; }

        // ---- program
        if (turbo)
        {
            status("Programming (turbo)...");
            TurboProgram(pages, target, stats, log, progress, ct);
        }
        else
        {
            status("Programming...");
            int lastPct = -1;
            for (int k = 0; k < pages.Count; k++)
            {
                ct.ThrowIfCancellationRequested();
                ProgramPageSafe(target, pages[k], ct);
                int pct = (int)((k + 1) * 100L / pages.Count);
                if (pct != lastPct) { progress.Report(pct); lastPct = pct; }
            }
        }
        return stats;
    }

    // ---- misc ------------------------------------------------------------------------
    public static string ManufacturerName(byte id) => id switch
    {
        0xEF => "Winbond",
        0xC2 => "Macronix",
        0xC8 => "GigaDevice",
        0x20 => "Micron / ST",
        0x01 => "Spansion / Cypress",
        0x1F => "Adesto / Atmel",
        0xBF => "SST / Microchip",
        0x9D => "ISSI",
        0x85 => "Puya",
        0x0B => "XTX",
        0x68 => "Boya",
        _ => "Unknown"
    };

    /// <summary>Most JEDEC parts encode capacity as log2(bytes) in the third ID byte.</summary>
    public static int CapacityFromId(byte capByte) =>
        capByte >= 0x10 && capByte <= 0x1A ? 1 << capByte : 0;
}
