``` csharp

using UnityEngine;

public class TestPotreeReader: MonoBehaviour
{
    [ContextMenu("Test")]
    void Test()
    {
        var metadataPath = "D:\\Work\\Project\\Potree\\examples\\d\\metadata.json";
        var binPath = "D:\\Work\\Project\\Potree\\examples\\d\\octree.bin";
        var metadata = PotreeMetadata.Load(metadataPath);
        var bin = PotreeBinReader.ReadBin(binPath, metadata);
        Debug.Log($"{bin.positions.Length} points loaded.,{metadata.bboxMax.ToString("F3")} max, {metadata.bboxMin.ToString("F3")} min");
    }
}
```
