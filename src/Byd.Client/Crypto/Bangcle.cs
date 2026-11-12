// Ported from pyBYD 0.0.79 (MIT). See THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Byd.Client.Crypto;

internal sealed class Bangcle
{
    private readonly byte[][] tables;
    internal Bangcle()
    {
        using var stream = typeof(Bangcle).Assembly.GetManifestResourceStream("Byd.Client.Crypto.bangcle_tables.bin")
            ?? throw new InvalidOperationException("Protocol tables are missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var data = memory.ToArray();
        int[] sizes = [0x28000, 0x3c000, 0x1000, 0x28000, 0x3c000, 0x1000, 8, 8];
        if (data.Length < 72 || Encoding.ASCII.GetString(data, 0, 4) != "BGTB" ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(4)) != 1 ||
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6)) != 8)
            throw new InvalidOperationException("Invalid protocol tables.");
        tables = new byte[8][];
        for (var i = 0; i < 8; i++)
        {
            var offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8 + i * 8)));
            var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12 + i * 8)));
            if (length != sizes[i] || offset < 72 || offset > data.Length - length)
                throw new InvalidOperationException("Invalid protocol table bounds.");
            tables[i] = data.AsSpan(offset, length).ToArray();
        }
    }

    private byte[] Block(ReadOnlySpan<byte> input, bool decrypt)
    {
        var state = new byte[32];
        var temp = new byte[64];
        var roundTable = tables[decrypt ? 0 : 3];
        var xorTable = tables[decrypt ? 1 : 4];
        var finalTable = tables[decrypt ? 2 : 5];
        var permutation = tables[decrypt ? 6 : 7];
        for (var col = 0; col < 4; col++)
            for (var row = 0; row < 4; row++) state[col * 8 + row] = input[col + row * 4];
        for (var step = 0; step < 9; step++)
        {
            var round = decrypt ? 9 - step : step;
            for (var i = 0; i < 4; i++)
                for (var j = 0; j < 4; j++)
                {
                    var p = (permutation[i * 2] + j) & 3;
                    var index = (state[i * 8 + p] + (i + (round * 4 + p) * 4) * 256) * 4;
                    roundTable.AsSpan(index, 4).CopyTo(temp.AsSpan(i * 16 + j * 4));
                }
            for (var col = 0; col < 4; col++)
                for (var row = 0; row < 4; row++)
                {
                    var offset = col + row * 4;
                    var low = temp[offset] & 15;
                    var high = temp[offset] & 240;
                    var group = row * 24 + round * 96;
                    var tableIndex = 1 + col * 6;
                    for (var k = 0; k < 3; k++, tableIndex += 2)
                    {
                        var next = temp[offset + (k + 1) * 16];
                        low = xorTable[(group + tableIndex - 1) * 256 + (low | ((next << 4) & 255))] & 15;
                        high = (xorTable[(group + tableIndex) * 256 + ((high >> 4) | (next & 240))] & 15) << 4;
                    }
                    state[row + col * 8] = (byte)(high | low);
                }
        }
        var previous = (byte[])state.Clone();
        for (var col = 0; col < 4; col++)
            for (var row = 0; row < 4; row++)
            {
                var p = (row + (decrypt ? (4 - col) : col)) & 3;
                state[col * 8 + row] = finalTable[previous[col * 8 + p] + p * 0x400 + col * 0x100];
            }
        var output = new byte[16];
        for (var col = 0; col < 4; col++)
            for (var row = 0; row < 4; row++) output[col + row * 4] = state[col * 8 + row];
        return output;
    }

    internal string Encode(string text)
    {
        var raw = Encoding.UTF8.GetBytes(text);
        var padding = 16 - raw.Length % 16;
        var data = new byte[raw.Length + padding];
        raw.CopyTo(data, 0);
        data.AsSpan(raw.Length).Fill((byte)padding);
        var result = new byte[data.Length];
        var previous = new byte[16];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            for (var i = 0; i < 16; i++) data[offset + i] ^= previous[i];
            previous = Block(data.AsSpan(offset, 16), false);
            previous.CopyTo(result, offset);
        }
        CryptographicOperations.ZeroMemory(raw);
        CryptographicOperations.ZeroMemory(data);
        return "F" + Convert.ToBase64String(result);
    }

    internal string Decode(string envelope)
    {
        var value = string.Concat(envelope.Where(c => !char.IsWhiteSpace(c)));
        if (!value.StartsWith('F')) throw new FormatException("Invalid envelope.");
        value = value[1..].Replace('-', '+').Replace('_', '/');
        value = value.PadRight((value.Length + 3) / 4 * 4, '=');
        var data = Convert.FromBase64String(value);
        if (data.Length == 0 || data.Length % 16 != 0) throw new FormatException("Invalid envelope size.");
        var output = new byte[data.Length];
        var previous = new byte[16];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var block = Block(data.AsSpan(offset, 16), true);
            for (var i = 0; i < 16; i++) output[offset + i] = (byte)(block[i] ^ previous[i]);
            data.AsSpan(offset, 16).CopyTo(previous);
        }
        var padding = output[^1];
        if (padding is < 1 or > 16 || output.AsSpan(output.Length - padding).ContainsAnyExcept(padding))
            throw new FormatException("Invalid envelope padding.");
        var text = new UTF8Encoding(false, true).GetString(output, 0, output.Length - padding);
        CryptographicOperations.ZeroMemory(output);
        return text;
    }
}
