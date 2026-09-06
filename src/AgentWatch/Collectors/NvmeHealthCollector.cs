using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using AgentWatch.Windows;

namespace AgentWatch.Collectors;

public sealed record NvmeHealth(DateTimeOffset TimestampUtc, int PhysicalDriveNumber,
    double? TemperatureC, byte CriticalWarning, byte AvailableSparePercent, byte AvailableSpareThresholdPercent,
    byte PercentageUsed, string DataUnitsRead, string DataUnitsWritten, string NvmeHostReadBytesLifetime,
    string NvmeHostWriteBytesLifetime, string HostReadCommands, string HostWriteCommands, string ControllerBusyMinutes,
    string PowerCycles, string PowerOnHours, string UnsafeShutdowns, string MediaErrors, string ErrorInfoLogEntries,
    uint WarningTemperatureMinutes, uint CriticalTemperatureMinutes, double?[] TemperatureSensorsC);

public static class NvmeParser
{
    public static UInt128 ReadUInt128(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 16) throw new InvalidDataException("NVMe UInt128 requires 16 bytes.");
        return ((UInt128)BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]) << 64) | BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
    public static BigInteger DataUnitsToBytes(UInt128 units) => (BigInteger)units * 512_000;
    public static double? KelvinToCelsius(ushort kelvin) => kelvin is 0 or ushort.MaxValue ? null : kelvin - 273.15;
    public static NvmeHealth Parse(ReadOnlySpan<byte> log, int drive, DateTimeOffset timestamp)
    {
        if (log.Length < 512) throw new InvalidDataException("Truncated NVMe SMART log; expected 512 bytes.");
        var values = new UInt128[10];
        for (var i = 0; i < values.Length; i++) values[i] = ReadUInt128(log[(32 + 16 * i)..]);
        string V(int i) => values[i].ToString(CultureInfo.InvariantCulture);
        var sensors = new double?[8];
        for (var i = 0; i < sensors.Length; i++) sensors[i] = KelvinToCelsius(BinaryPrimitives.ReadUInt16LittleEndian(log[(200 + i * 2)..]));
        return new(timestamp, drive, KelvinToCelsius(BinaryPrimitives.ReadUInt16LittleEndian(log[1..])), log[0], log[3], log[4], log[5],
            V(0), V(1), DataUnitsToBytes(values[0]).ToString(CultureInfo.InvariantCulture), DataUnitsToBytes(values[1]).ToString(CultureInfo.InvariantCulture),
            V(2), V(3), V(4), V(5), V(6), V(7), V(8), V(9), BinaryPrimitives.ReadUInt32LittleEndian(log[192..]),
            BinaryPrimitives.ReadUInt32LittleEndian(log[196..]), sensors);
    }
    public static ReadOnlySpan<byte> ValidateDescriptor(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 48 || BinaryPrimitives.ReadUInt32LittleEndian(descriptor) != 48
            || BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]) != 48
            || BinaryPrimitives.ReadUInt32LittleEndian(descriptor[8..]) != 3
            || BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]) != 2)
            throw new InvalidDataException("Invalid NVMe storage protocol descriptor.");
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[24..]);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[28..]);
        if (offset < 40 || length < 512 || (ulong)offset + 8 + length > (ulong)descriptor.Length)
            throw new InvalidDataException("NVMe protocol data exceeds returned buffer.");
        return descriptor.Slice(checked((int)offset + 8), 512);
    }
}

public sealed class NvmeHealthCollector(int driveNumber)
{
    public unsafe NvmeHealth Read()
    {
        // Query-only handle; no GENERIC_WRITE, admin commands or device changes.
        using var device = NativeMethods.CreateFile($@"\\.\PhysicalDrive{driveNumber}", 0, 3, 0, 3, 0, 0);
        if (device.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError(), $"PhysicalDrive{driveNumber}: query access denied or device unavailable");
        var buffer = new byte[560];
        var error = 0;
        foreach (var property in new uint[] { 50, 49 })
        {
            buffer.AsSpan().Clear();
            void Write(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset), value);
            Write(0, property); // Device protocol property, then adapter fallback.
            Write(8, 3); Write(12, 2); Write(16, 2); // NVMe, log page, SMART/health.
            Write(24, 40); Write(28, 512);
            fixed (byte* pointer = buffer)
            {
                if (NativeMethods.DeviceIoControl(device, 0x002D1400, pointer, (uint)buffer.Length, pointer, (uint)buffer.Length, out var returned, 0))
                {
                    if (returned > buffer.Length) throw new InvalidDataException("NVMe driver returned an invalid length.");
                    return NvmeParser.Parse(NvmeParser.ValidateDescriptor(buffer.AsSpan(0, (int)returned)), driveNumber, DateTimeOffset.UtcNow);
                }
                error = Marshal.GetLastPInvokeError();
            }
            if (error is not (1 or 50 or 87)) break;
        }
        throw new Win32Exception(error, $"NVMe SMART query for PhysicalDrive{driveNumber} failed; administrative access or driver support may be required");
    }
}
