namespace Zeus;

/// <summary>
/// Mitsubishi MC 虚拟 PLC。可直接交给 <c>AddVirtualChannel</c>。
/// </summary>
public sealed class McSlaveResponder : IVirtualResponder
{
    private const ushort Success = 0x0000;
    private const ushort UnsupportedCommand = 0xC059;
    private const ushort InvalidDevice = 0xC051;
    private readonly McSlaveMemory _memory;

    /// <summary>
    /// 创建虚拟 PLC。
    /// </summary>
    /// <param name="memory">软元件映像。为 <c>null</c> 时使用默认容量。</param>
    public McSlaveResponder(McSlaveMemory? memory = null)
    {
        _memory = memory ?? new McSlaveMemory();
    }

    /// <summary>可在测试中预置或断言的映像。</summary>
    public McSlaveMemory Memory => _memory;

    /// <inheritdoc />
    public ReadOnlyMemory<byte>? Respond(ReadOnlyMemory<byte> request)
    {
        if (!McCodec.TryDecodeRequest(request.Span, out var context, out var command, out var subcommand, out var data))
        {
            return null;
        }

        try
        {
            var response = Handle(command, subcommand, data);
            return McCodec.EncodeResponse(context, Success, response);
        }
        catch (McException ex)
        {
            return McCodec.EncodeResponse(context, ex.EndCode, []);
        }
    }

    private byte[] Handle(ushort command, ushort subcommand, byte[] data)
    {
        return command switch
        {
            McCodec.BatchReadCommand when subcommand == McCodec.WordSubcommand => ReadWords(data),
            McCodec.BatchWriteCommand when subcommand == McCodec.WordSubcommand => WriteWords(data),
            McCodec.BatchReadCommand when subcommand == McCodec.BitSubcommand => ReadBits(data),
            McCodec.BatchWriteCommand when subcommand == McCodec.BitSubcommand => WriteBits(data),
            McCodec.RandomReadCommand when subcommand == McCodec.WordSubcommand => RandomRead(data),
            McCodec.RandomWriteCommand when subcommand == McCodec.WordSubcommand => RandomWriteWords(data),
            McCodec.RandomWriteCommand when subcommand == McCodec.BitSubcommand => RandomWriteBits(data),
            McCodec.MultipleBlockReadCommand when subcommand == McCodec.WordSubcommand => MultipleBlockRead(data),
            McCodec.RemoteRunCommand when subcommand == McCodec.WordSubcommand => RemoteControl(McRemoteControlMode.Run, running: true),
            McCodec.RemoteStopCommand when subcommand == McCodec.WordSubcommand => RemoteControl(McRemoteControlMode.Stop, running: false),
            McCodec.RemotePauseCommand when subcommand == McCodec.WordSubcommand => RemoteControl(McRemoteControlMode.Pause, running: false),
            McCodec.RemoteLatchClearCommand when subcommand == McCodec.WordSubcommand => RemoteControl(McRemoteControlMode.LatchClear, _memory.IsRunning),
            McCodec.RemoteResetCommand when subcommand == McCodec.WordSubcommand => RemoteControl(McRemoteControlMode.Reset, running: true),
            _ => throw new McException(UnsupportedCommand)
        };
    }

    private byte[] ReadWords(byte[] data)
    {
        var (address, deviceCode, points) = McCodec.ReadDeviceRequest(data);
        var table = GetWordTable(deviceCode);
        EnsureRange(address, points, table.Length);
        var response = new byte[points * 2];
        for (var i = 0; i < points; i++)
        {
            McCodec.WriteUInt16LittleEndian(response.AsSpan(i * 2, 2), table[address + i]);
        }

        return response;
    }

    private byte[] WriteWords(byte[] data)
    {
        var (address, deviceCode, points) = McCodec.ReadDeviceRequest(data);
        var table = GetWordTable(deviceCode);
        if (data.Length < 6 + (points * 2))
        {
            throw new McException(InvalidDevice);
        }

        EnsureRange(address, points, table.Length);
        for (var i = 0; i < points; i++)
        {
            table[address + i] = McCodec.ReadUInt16LittleEndian(data.AsSpan(6 + (i * 2), 2));
        }

        return [];
    }

    private byte[] ReadBits(byte[] data)
    {
        var (address, deviceCode, points) = McCodec.ReadDeviceRequest(data);
        var table = GetBitTable(deviceCode);
        EnsureRange(address, points, table.Length);
        var response = new byte[McCodec.BitByteCount(points)];
        for (var i = 0; i < points; i++)
        {
            McCodec.SetPackedBit(response, i, table[address + i]);
        }

        return response;
    }

    private byte[] WriteBits(byte[] data)
    {
        var (address, deviceCode, points) = McCodec.ReadDeviceRequest(data);
        var table = GetBitTable(deviceCode);
        if (data.Length < 6 + McCodec.BitByteCount(points))
        {
            throw new McException(InvalidDevice);
        }

        EnsureRange(address, points, table.Length);
        var payload = data.AsSpan(6);
        for (var i = 0; i < points; i++)
        {
            table[address + i] = McCodec.GetPackedBit(payload, i);
        }

        return [];
    }

    private byte[] RandomRead(byte[] data)
    {
        var (wordDevices, doubleWordDevices) = McCodec.ReadRandomReadRequest(data);
        var response = new byte[(wordDevices.Length * 2) + (doubleWordDevices.Length * 4)];
        var offset = 0;
        foreach (var device in wordDevices)
        {
            var value = ReadWord(device);
            McCodec.WriteUInt16LittleEndian(response.AsSpan(offset, 2), value);
            offset += 2;
        }

        foreach (var device in doubleWordDevices)
        {
            var table = GetWordTable(device.DeviceCode);
            EnsureRange(device.Address, 2, table.Length);
            McCodec.WriteUInt16LittleEndian(response.AsSpan(offset, 2), table[device.Address]);
            McCodec.WriteUInt16LittleEndian(response.AsSpan(offset + 2, 2), table[device.Address + 1]);
            offset += 4;
        }

        return response;
    }

    private byte[] RandomWriteWords(byte[] data)
    {
        var (wordValues, doubleWordValues) = McCodec.ReadRandomWriteWordsRequest(data);
        foreach (var item in wordValues)
        {
            var table = GetWordTable(item.DeviceCode);
            EnsureRange(item.Address, 1, table.Length);
            table[item.Address] = item.Value;
        }

        foreach (var item in doubleWordValues)
        {
            var table = GetWordTable(item.DeviceCode);
            EnsureRange(item.Address, 2, table.Length);
            table[item.Address] = (ushort)(item.Value & 0xFFFF);
            table[item.Address + 1] = (ushort)(item.Value >> 16);
        }

        return [];
    }

    private byte[] RandomWriteBits(byte[] data)
    {
        var values = McCodec.ReadRandomWriteBitsRequest(data);
        foreach (var item in values)
        {
            var table = GetBitTable(item.DeviceCode);
            EnsureRange(item.Address, 1, table.Length);
            table[item.Address] = item.Value;
        }

        return [];
    }

    private byte[] MultipleBlockRead(byte[] data)
    {
        var (wordBlocks, bitBlocks) = McCodec.ReadMultipleBlockReadRequest(data);
        var wordBytes = wordBlocks.Sum(block => block.Points * 2);
        var bitBytes = bitBlocks.Sum(block => McCodec.BitByteCount(block.Points));
        var response = new byte[wordBytes + bitBytes];
        var offset = 0;
        foreach (var block in wordBlocks)
        {
            var table = GetWordTable(block.DeviceCode);
            EnsureRange(block.Address, block.Points, table.Length);
            for (var i = 0; i < block.Points; i++)
            {
                McCodec.WriteUInt16LittleEndian(response.AsSpan(offset, 2), table[block.Address + i]);
                offset += 2;
            }
        }

        foreach (var block in bitBlocks)
        {
            var table = GetBitTable(block.DeviceCode);
            EnsureRange(block.Address, block.Points, table.Length);
            var packed = response.AsSpan(offset, McCodec.BitByteCount(block.Points));
            packed.Clear();
            for (var i = 0; i < block.Points; i++)
            {
                McCodec.SetPackedBit(packed, i, table[block.Address + i]);
            }

            offset += packed.Length;
        }

        return response;
    }

    private byte[] RemoteControl(McRemoteControlMode mode, bool running)
    {
        _memory.LastRemoteControl = mode;
        _memory.IsRunning = running;
        return [];
    }

    private ushort ReadWord(McDeviceAddress device)
    {
        var table = GetWordTable(device.DeviceCode);
        EnsureRange(device.Address, 1, table.Length);
        return table[device.Address];
    }

    private ushort[] GetWordTable(McDeviceCode deviceCode)
    {
        if (deviceCode == McDeviceCode.DataRegister)
        {
            return _memory.DataRegisters;
        }

        if (deviceCode == McDeviceCode.LinkRegister)
        {
            return _memory.LinkRegisters;
        }

        if (deviceCode == McDeviceCode.FileRegister)
        {
            return _memory.FileRegisters;
        }

        if (deviceCode == McDeviceCode.ExtendedFileRegister)
        {
            return _memory.ExtendedFileRegisters;
        }

        throw new McException(InvalidDevice);
    }

    private bool[] GetBitTable(McDeviceCode deviceCode)
    {
        if (deviceCode == McDeviceCode.InternalRelay)
        {
            return _memory.InternalRelays;
        }

        if (deviceCode == McDeviceCode.InputRelay)
        {
            return _memory.InputRelays;
        }

        if (deviceCode == McDeviceCode.OutputRelay)
        {
            return _memory.OutputRelays;
        }

        throw new McException(InvalidDevice);
    }

    private static void EnsureRange(int address, int points, int length)
    {
        if (points <= 0 || address < 0 || address + points > length)
        {
            throw new McException(InvalidDevice);
        }
    }
}
