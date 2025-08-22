using System;
using System.IO;
using System.Linq;
using UnityEngine;

public static class PotreeBinReader
{
    public struct PointBlock
    {
        public Vector3[] positions;
        public Color32[] colors;          // 可能为空
        public ushort[] intensities;      // 可能为空
        public byte[] classifications;    // 可能为空
    }

    public static PointBlock ReadBin(string binPath, PotreeMetadata md, int? maxPointsToRead = null)
    {
        int stride = md.TotalStride();
        if (stride <= 0) throw new Exception("Invalid stride computed from attributes.");

        using var fs = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long totalBytes = fs.Length;
        int pointCount = (int)(totalBytes / stride);
        if (maxPointsToRead.HasValue) pointCount = Mathf.Min(pointCount, maxPointsToRead.Value);

        var positions = new Vector3[pointCount];
        Color32[] colors = null;
        ushort[] intensities = null;
        byte[] classifications = null;

        // 检查是否含颜色/强度/分类
        bool hasRGB = md.attributes.Any(a => a.name == PotreeAttributeName.RGB);
        bool hasPacked = md.attributes.Any(a => a.name == PotreeAttributeName.COLOR_PACKED);
        bool hasIntensity = md.attributes.Any(a => a.name == PotreeAttributeName.INTENSITY);
        bool hasClassif = md.attributes.Any(a => a.name == PotreeAttributeName.CLASSIFICATION);

        if (hasRGB || hasPacked) colors = new Color32[pointCount];
        if (hasIntensity) intensities = new ushort[pointCount];
        if (hasClassif) classifications = new byte[pointCount];

        using var br = new BinaryReader(fs);

        byte[] buffer = new byte[stride];

        for (int i = 0; i < pointCount; i++)
        {
            int read = br.Read(buffer, 0, stride);
            if (read < stride) throw new EndOfStreamException();

            int offset = 0;
            Color32 col = new Color32(255, 255, 255, 255);

            foreach (var attr in md.attributes)
            {
                switch (attr.name)
                {
                    case PotreeAttributeName.POSITION_CARTESIAN:
                    {
                        // 两种常见存法：
                        // A) 3*float32  直接读float
                        // B) 3*int32    world = offset + scale * int
                        if (attr.type == PotreeComponentType.FLOAT)
                        {
                            float x = BitConverter.ToSingle(buffer, offset + 0);
                            float y = BitConverter.ToSingle(buffer, offset + 4);
                            float z = BitConverter.ToSingle(buffer, offset + 8);
                            positions[i] = new Vector3(x, y, z);
                        }
                        else
                        {
                            int xi = BitConverter.ToInt32(buffer, offset + 0);
                            int yi = BitConverter.ToInt32(buffer, offset + 4);
                            int zi = BitConverter.ToInt32(buffer, offset + 8);
                            // 按 Potree 常规：位置是量化相对 offset 的 int32
                            positions[i] = md.offset + Vector3.Scale(md.scale, new Vector3(xi, yi, zi));
                        }
                        break;
                    }
                    case PotreeAttributeName.RGB:
                    {
                        // 3*uint8
                        byte r = buffer[offset + 0];
                        byte g = buffer[offset + 1];
                        byte b = buffer[offset + 2];
                        col = new Color32(r, g, b, 255);
                        break;
                    }
                    case PotreeAttributeName.COLOR_PACKED:
                    {
                        // 通常为 RGBA 4*uint8
                        byte r = buffer[offset + 0];
                        byte g = buffer[offset + 1];
                        byte b = buffer[offset + 2];
                        byte a = buffer[offset + 3];
                        col = new Color32(r, g, b, a);
                        break;
                    }
                    // case PotreeAttributeName.INTENSITY:
                    // {
                    //     ushort inten = BitConverter.ToUInt16(buffer, offset);
                    //     if (intensities != null) intensities[i] = inten;
                    //     break;
                    // }
                    // case PotreeAttributeName.CLASSIFICATION:
                    // {
                    //     byte cls = buffer[offset];
                    //     if (classifications != null) classifications[i] = cls;
                    //     break;
                    // }
                    default:
                        // 其他属性你可以按需求扩展解析
                        break;
                }

                offset += attr.sizeBytes;
            }

            if (colors != null) colors[i] = col;
        }

        Debug.Log($"Read {pointCount} points from {binPath}.");
        return new PointBlock { positions = positions, colors = colors };
    }
}