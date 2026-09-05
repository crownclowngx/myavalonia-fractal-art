using System.Buffers.Binary;
using System.IO.Compression;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using FractalArtPlugin.Application;
using MyAvaloniaManagement.PluginSdk.UI;

namespace FractalArtPlugin.Infrastructure;

/// <summary>只实现 G0003 需要的无隔行 RGBA8888 PNG，避免让领域层依赖某个图像 UI 框架。</summary>
internal sealed class PngEncoder : IPngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public byte[] Encode(ImageSurface image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header[..4], image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.Slice(4, 4), image.Height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);

        // 明确写入标准 sRGB 色彩解释，避免不同查看器把相同 RGBA 字节当成不同设备空间。
        // gAMA 的整数值 45455 表示约 1/2.2；它与 sRGB 块保持一致，并便于只识别 gAMA 的旧解码器。
        WriteChunk(output, "sRGB"u8, [0]);
        Span<byte> gamma = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(gamma, 45455);
        WriteChunk(output, "gAMA"u8, gamma);

        using var raw = new MemoryStream();
        using (var compressor = new ZLibStream(raw, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var rowBytes = checked(image.Width * 4);
            var row = new byte[rowBytes];
            for (var y = 0; y < image.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                compressor.WriteByte(0); // 过滤类型 0：保持实现简单、确定且易于验证。
                image.Pixels.Span.Slice(y * rowBytes, rowBytes).CopyTo(row);
                // straight Alpha 下完全透明像素的 RGB 不参与显示。导出时仍归零隐藏颜色，避免后续缩放、
                // 合成或错误的预乘处理把画布底色重新带回边缘。
                for (var offset = 0; offset < row.Length; offset += 4)
                {
                    if (row[offset + 3] == 0)
                    {
                        row.AsSpan(offset, 3).Clear();
                    }
                }

                compressor.Write(row);
            }
        }

        WriteChunk(output, "IDAT"u8, raw.ToArray());
        WriteChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);

        var crc = Crc32(type, data);
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type)
        {
            crc = Update(crc, value);
        }

        foreach (var value in data)
        {
            crc = Update(crc, value);
        }

        return ~crc;
    }

    private static uint Update(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB88320u;
        }

        return crc;
    }
}

/// <summary>同目录临时文件成功刷新后再替换目标，取消和失败都不会报告半成品为成功。</summary>
internal sealed class AtomicFileWriter : IAtomicFileWriter
{
    public async Task WriteAsync(string path, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new InvalidOperationException("导出路径没有父目录。");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"导出目录不存在：{directory}");
        }

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed class AvaloniaPreviewImageFactory(IPngEncoder encoder) : IPreviewImageFactory
{
    public Bitmap Create(ImageSurface image, CancellationToken cancellationToken)
    {
        var bytes = encoder.Encode(image, cancellationToken);
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }
}

/// <summary>把 SDK 的宿主窗口端口收窄为“选择 PNG 输出位置”这一项产品意图。</summary>
internal sealed class ArtworkExportDialog(IPluginWindowInteraction interaction) : IArtworkExportDialog
{
    public Task<string?> PickPngPathAsync(string suggestedName, CancellationToken cancellationToken) =>
        interaction.PickSaveFileAsync(
            new FilePickerSaveOptions
            {
                Title = "导出分形作品 PNG",
                SuggestedFileName = suggestedName,
                FileTypeChoices = [new FilePickerFileType("PNG 图片") { Patterns = ["*.png"] }]
            },
            cancellationToken);
}
