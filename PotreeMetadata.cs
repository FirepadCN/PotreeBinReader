using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public enum PotreeComponentType { UCHAR, CHAR, USHORT, SHORT, UINT, INT, FLOAT, DOUBLE }
public enum PotreeAttributeName {
    POSITION_CARTESIAN, COLOR_PACKED, RGB, INTENSITY, CLASSIFICATION, NORMAL, GPSTIME, RETURN_NUMBER,
    NUMBER_OF_RETURNS, SOURCE_ID, POINT_SOURCE_ID, UNKNOWN
}

[Serializable]
public class PotreeAttribute
{
    public PotreeAttributeName name;
    public PotreeComponentType type;
    public int sizeBytes;
    public int numElements; // e.g., POSITION 3, RGB 3, COLOR_PACKED 4

    public int StrideBytes => sizeBytes; // Potree schema里每个attribute自带size（已含elements）
}

[Serializable]
public class PotreeMetadata
{
    public string octreeDir;
    public long points;
    public Vector3 bboxMin;
    public Vector3 bboxMax;
    public Vector3 scale;   // 对 POSITION_INT32 量化还原: world = offset + scale * intPos
    public Vector3 offset;  // 注意：部分版本字段叫 "offset" 或 "boundingBox" 里的 "min"
    public int hierarchyStepSize;
    public List<PotreeAttribute> attributes = new();

    // 解析入口：兼容 cloud.js / metadata.json
    public static PotreeMetadata Load(string metadataPath)
    {
        var text = File.ReadAllText(metadataPath);
        var jo = JObject.Parse(text);

        var md = new PotreeMetadata();

        // 1) octreeDir / hierarchy
        md.octreeDir = jo["octreeDir"]?.Value<string>() 
                       ?? jo["octreeDirPath"]?.Value<string>() 
                       ?? jo["octreeDirName"]?.Value<string>() 
                       ?? "octree";

        md.hierarchyStepSize = jo["hierarchyStepSize"]?.Value<int?>() ?? 5;
        md.points = jo["points"]?.Value<long?>() ?? jo["pointCount"]?.Value<long?>() ?? 0;

        // 2) bbox / offset / scale
        if (jo["boundingBox"] != null)
        {
            var bb = jo["boundingBox"];
            md.bboxMin = ReadVec3(bb["min"]);
            md.bboxMax = ReadVec3(bb["max"]);
        }
        else
        {
            // 有些版本叫 "tightBoundingBox" 或单独 min/max；这里做下兜底
            if (jo["tightBoundingBox"] != null)
            {
                var bb = jo["tightBoundingBox"];
                md.bboxMin = ReadVec3(bb["min"]);
                md.bboxMax = ReadVec3(bb["max"]);
            }
        }

        if (jo["scale"] != null)
        {
            // 可能是标量或 vec3
            if (jo["scale"].Type == JTokenType.Float || jo["scale"].Type == JTokenType.Integer)
            {
                float s = jo["scale"].Value<float>();
                md.scale = new Vector3(s, s, s);
            }
            else
            {
                md.scale = ReadVec3(jo["scale"]);
            }
        }
        else md.scale = Vector3.one * 0.001f; // 兜底

        if (jo["offset"] != null)
        {
            md.offset = ReadVec3(jo["offset"]);
        }
        else
        {
            // 常见做法：offset = bboxMin
            md.offset = md.bboxMin;
        }

        // 3) pointAttributes 可能是字符串或数组
        var pa = jo["pointAttributes"];
        if (pa == null)
        {
            // 新格式可能叫 "attributes" 或 "schema"
            pa = jo["attributes"] ?? jo["schema"];
        }

        if (pa == null)
            throw new Exception("metadata: missing pointAttributes/schema");

        if (pa.Type == JTokenType.String)
        {
            // 旧格式（别名）：例如 "LASRGB"
            var alias = pa.Value<string>().ToUpperInvariant();
            ExpandAlias(md, alias);
        }
        else if (pa.Type == JTokenType.Array)
        {
            foreach (var a in (JArray)pa)
            {
                // 新格式通常是对象：{ "name":"POSITION_CARTESIAN", "size":12, "elements":3, "type":"FLOAT" }
                var attr = new PotreeAttribute();
                attr.name = ParseAttrName(a["name"]?.Value<string>());
                attr.sizeBytes = a["size"]?.Value<int?>() ?? GuessSize(attr.name);
                attr.numElements = a["elements"]?.Value<int?>() ?? GuessElements(attr.name);
                attr.type = ParseComponentType(a["type"]?.Value<string>(), attr.sizeBytes, attr.numElements);
                md.attributes.Add(attr);
            }
        }
        else
        {
            throw new Exception("metadata: unsupported pointAttributes type");
        }

        return md;
    }
    
    public static Vector3 ReadVec3(JToken t, Vector3 fallback = default)
    {
        if (t == null) return fallback;

        if (t.Type == JTokenType.Array)
        {
            var a = (JArray)t;
            if (a.Count >= 3)
                return new Vector3((float)a[0], (float)a[1], (float)a[2]);
            return fallback;
        }
        // 对象格式：{x:..., y:..., z:...}
        return new Vector3((float)(t["x"] ?? 0), (float)(t["y"] ?? 0), (float)(t["z"] ?? 0));
    }

    public static float[] ReadVec(JToken t)
    {
        if (t == null) return Array.Empty<float>();
        if (t.Type == JTokenType.Array)
        {
            var a = (JArray)t;
            var r = new float[a.Count];
            for (int i = 0; i < a.Count; i++) r[i] = (float)a[i];
            return r;
        }
        // 对象或标量兜底
        if (t.Type == JTokenType.Object)
        {
            return new[] {
                (float)(t["x"] ?? 0),
                (float)(t["y"] ?? 0),
                (float)(t["z"] ?? 0),
            };
        }
        return new[] { (float)t };
    }

    static PotreeAttributeName ParseAttrName(string s)
    {
        if (string.IsNullOrEmpty(s)) return PotreeAttributeName.UNKNOWN;
        s = s.ToUpperInvariant();
        if (s.Contains("POSITION")) return PotreeAttributeName.POSITION_CARTESIAN;
        if (s.Contains("COLOR_PACKED")) return PotreeAttributeName.COLOR_PACKED;
        if (s == "RGB" || s.Contains("COLOR") || s.Contains("RGB_")) return PotreeAttributeName.RGB;
        if (s.Contains("INTENSITY")) return PotreeAttributeName.INTENSITY;
        if (s.Contains("CLASSIF")) return PotreeAttributeName.CLASSIFICATION;
        if (s.Contains("NORMAL")) return PotreeAttributeName.NORMAL;
        if (s.Contains("GPSTIME")) return PotreeAttributeName.GPSTIME;
        if (s.Contains("RETURN_NUMBER")) return PotreeAttributeName.RETURN_NUMBER;
        if (s.Contains("NUMBER_OF_RETURNS")) return PotreeAttributeName.NUMBER_OF_RETURNS;
        if (s.Contains("SOURCE_ID") || s.Contains("POINT_SOURCE_ID")) return PotreeAttributeName.POINT_SOURCE_ID;
        return PotreeAttributeName.UNKNOWN;
    }

    static PotreeComponentType ParseComponentType(string t, int sizeBytes, int elems)
    {
        if (!string.IsNullOrEmpty(t))
        {
            t = t.ToUpperInvariant();
            if (t.Contains("FLOAT")) return PotreeComponentType.FLOAT;
            if (t.Contains("DOUBLE")) return PotreeComponentType.DOUBLE;
            if (t.Contains("INT32")) return PotreeComponentType.INT;
            if (t.Contains("UINT32")) return PotreeComponentType.UINT;
            if (t.Contains("INT16")) return PotreeComponentType.SHORT;
            if (t.Contains("UINT16")) return PotreeComponentType.USHORT;
            if (t.Contains("INT8")) return PotreeComponentType.CHAR;
            if (t.Contains("UINT8")) return PotreeComponentType.UCHAR;
        }
        // 根据 size 猜（保底）
        if (sizeBytes == elems * 4) return PotreeComponentType.FLOAT;
        return PotreeComponentType.UINT;
    }

    static int GuessSize(PotreeAttributeName name)
    {
        switch (name)
        {
            case PotreeAttributeName.POSITION_CARTESIAN: return 12; // 3*float 或 3*int32
            case PotreeAttributeName.RGB: return 3;      // 3*uchar
            case PotreeAttributeName.COLOR_PACKED: return 4; // rgba
            case PotreeAttributeName.INTENSITY: return 2; // uint16
            case PotreeAttributeName.CLASSIFICATION: return 1;
            default: return 4;
        }
    }

    static int GuessElements(PotreeAttributeName name)
    {
        switch (name)
        {
            case PotreeAttributeName.POSITION_CARTESIAN: return 3;
            case PotreeAttributeName.RGB: return 3;
            case PotreeAttributeName.COLOR_PACKED: return 4;
            default: return 1;
        }
    }

    static void ExpandAlias(PotreeMetadata md, string alias)
    {
        // 常见别名：LAS, LASRGB, RGB
        var list = new List<PotreeAttribute>
        {
            new PotreeAttribute{ name=PotreeAttributeName.POSITION_CARTESIAN, type=PotreeComponentType.FLOAT, sizeBytes=12, numElements=3 },
        };
        if (alias.Contains("RGB"))
        {
            list.Add(new PotreeAttribute{ name=PotreeAttributeName.RGB, type=PotreeComponentType.UCHAR, sizeBytes=3, numElements=3 });
        }
        if (alias.Contains("LAS"))
        {
            list.Add(new PotreeAttribute{ name=PotreeAttributeName.INTENSITY, type=PotreeComponentType.USHORT, sizeBytes=2, numElements=1 });
            list.Add(new PotreeAttribute{ name=PotreeAttributeName.CLASSIFICATION, type=PotreeComponentType.UCHAR, sizeBytes=1, numElements=1 });
        }
        md.attributes = list;
    }

    public int TotalStride()
    {
        // Potree 的 .bin 是按 attributes 顺序平铺，sizeBytes 已是该属性的总字节数（含 elements）
        return attributes.Sum(a => a.sizeBytes);
    }
}
