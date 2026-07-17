using System.IO;
using System.Security.Cryptography;

namespace OpenShell.Updates;

/// <summary>
/// 简化二进制补丁应用器。Per ADR-0037 §8.
/// <para>
/// 补丁文件格式 (length-prefixed diff, big-endian):
/// <list type="bullet">
///   <item>魔数: 4 字节 ASCII <c>OSPD</c> (OpenShell Patch Data)。</item>
///   <item>版本: 1 字节 (当前为 1)。</item>
///   <item>记录数: 4 字节 uint32 (big-endian)。</item>
///   <item>记录序列: 每条 = offset(8 字节 int64 BE) + length(4 字节 uint32 BE) + data(length 字节)。
///     应用时把 data 直接覆盖到目标文件的 offset 位置 (不改变文件大小)。</item>
/// </list>
/// </para>
/// <para>
/// 该格式是简化但功能完整的 diff 格式: 仅支持 in-place 覆盖, 不支持插入/删除。
/// 适合 OpenShell 单文件可执行体的"小段代码替换"场景; 不适用大范围重构的发布。
/// 失败时调用方应回退到 <see cref="GitHubReleasesUpdateService.DownloadAsync"/> 全量下载。
/// </para>
/// </summary>
public static class BinaryPatcher
{
    private static readonly byte[] Magic = System.Text.Encoding.ASCII.GetBytes("OSPD");
    private const byte FormatVersion = 1;

    /// <summary>
    /// 把 baseFile 应用 patchPath 描述的补丁, 输出到 outputPath。Per ADR-0037 §8.
    /// </summary>
    /// <param name="baseFile">源版本文件绝对路径 (必须存在)。</param>
    /// <param name="patchFile">补丁文件绝对路径。</param>
    /// <param name="outputPath">输出文件绝对路径 (会被覆盖)。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidDataException">补丁文件格式非法 / offset 超出 base 文件范围。</exception>
    public static async Task ApplyAsync(string baseFile, string patchFile, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(baseFile);
        ArgumentNullException.ThrowIfNull(patchFile);
        ArgumentNullException.ThrowIfNull(outputPath);
        if (!File.Exists(baseFile)) throw new FileNotFoundException("Base file not found.", baseFile);
        if (!File.Exists(patchFile)) throw new FileNotFoundException("Patch file not found.", patchFile);

        // 1) 拷贝 base → output (补丁为 in-place 覆盖语义)。
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.Copy(baseFile, outputPath, overwrite: true);

        // 2) 解析补丁文件并逐条覆盖。
        await using var patchStream = new FileStream(patchFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var br = new BinaryReader(patchStream);

        // 魔数 + 版本。
        var magic = br.ReadBytes(Magic.Length);
        if (magic.Length != Magic.Length || !magic.SequenceEqual(Magic))
            throw new InvalidDataException("Patch file magic mismatch; expected 'OSPD'.");
        var ver = br.ReadByte();
        if (ver != FormatVersion)
            throw new InvalidDataException($"Unsupported patch format version: {ver} (expected {FormatVersion}).");

        // 记录数 (big-endian)。
        var recordCount = ReadUInt32BE(br);

        // 用 RandomAccess 在 output 上做散射覆盖 (避免读整文件到内存)。
        await using var outStream = new FileStream(outputPath, FileMode.Open, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        var outLen = outStream.Length;

        for (uint i = 0; i < recordCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = ReadInt64BE(br);
            var length = ReadUInt32BE(br);
            if (length == 0) continue;
            if (offset < 0 || offset + length > outLen)
                throw new InvalidDataException(
                    $"Patch record {i}: offset/length ({offset}, {length}) out of range (file size {outLen}).");

            var data = br.ReadBytes((int)length);
            if (data.Length != length)
                throw new InvalidDataException($"Patch record {i}: truncated data (expected {length}, got {data.Length}).");

            outStream.Position = offset;
            await outStream.WriteAsync(data.AsMemory(0, data.Length), ct).ConfigureAwait(false);
        }
        await outStream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>计算文件的 SHA256 (hex 小写), 用于补丁应用后校验最终产物。</summary>
    public static async Task<string> ComputeSha256HexAsync(string filePath, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
        var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static uint ReadUInt32BE(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        if (b.Length != 4) throw new EndOfStreamException("Patch file truncated reading uint32.");
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static long ReadInt64BE(BinaryReader br)
    {
        var b = br.ReadBytes(8);
        if (b.Length != 8) throw new EndOfStreamException("Patch file truncated reading int64.");
        long v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[i];
        return v;
    }
}
