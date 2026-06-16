using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PointCloudViewer : MonoBehaviour
{
    [Tooltip("点大小（像素）")]
    [Range(0.5f, 20f)]
    public float pointSize = 2f;

    [Tooltip("若不指定，将自动使用 Orbbec/PointCloudPoints 着色器创建材质")]
    public Material pointCloudMaterial;

    [Tooltip("点云包围盒半边长（米）。设得足够大以避免被视锥剔除，从而跳过每帧 bounds 重算。")]
    public float boundsExtent = 25f;

    private Mesh _mesh;
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private int[] _indices;
    // 已经填充为恒等序列 (0,1,2,...) 的索引个数；索引值不随帧变化，只需在增长时补齐。
    private int _filledIndexCount;
    private static readonly int PointSizeId = Shader.PropertyToID("_PointSize");

    public PointCloudStream pointCloudStream;
    
  

    void Start()
    {
        _mesh = new Mesh { name = "PointCloudMesh" };
        // 默认 16 位索引最多 65535 个点，满分辨率点云（如 1920x1080=2073600）会被截断，
        // 只渲染最上面约 34 行。改用 32 位索引以支持百万级点。
        _mesh.indexFormat = IndexFormat.UInt32;
        // 标记为动态网格：点云每帧更新，提示引擎使用便于频繁写入的 GPU 缓冲。
        _mesh.MarkDynamic();
        _meshFilter = this.gameObject.GetComponent<MeshFilter>();
        _meshRenderer = this.gameObject.GetComponent<MeshRenderer>();
        _meshFilter.sharedMesh = _mesh;

         if (pointCloudMaterial == null)
        {
            var shader = Shader.Find("Orbbec/PointCloudPoints");
            if (shader != null)
                pointCloudMaterial = new Material(shader);
            else
                Debug.LogWarning("PointCloudStream: 未找到 Orbbec/PointCloudPoints 着色器");
        }
        if (pointCloudMaterial != null)
            _meshRenderer.sharedMaterial = pointCloudMaterial;

        pointCloudStream.OnPointCloudUpdated += UpdateViewer;
    }
    void Update()
    {
       Material mat = _meshRenderer.sharedMaterial;
        if (mat != null && mat.HasProperty(PointSizeId))
            mat.SetFloat(PointSizeId, pointSize);
    }

    // Update is called once per frame
    public void UpdateViewer(Vector3[] PendingVertices, Color[] PendingColors, int PendingCount)
    {
        if (PendingCount <= 0)
        {
            _mesh.Clear();
            _filledIndexCount = 0;
            return;
        }

        // 先把索引收缩到本帧点数，避免设置更少的顶点时旧索引越界（顶点数 < 索引引用）。
        _mesh.SetIndices(System.Array.Empty<int>(), MeshTopology.Points, 0, false);

        // DontRecalculateBounds：跳过对百万级顶点重算包围盒，改用下方手动设置的大包围盒。
        _mesh.SetVertices(PendingVertices, 0, PendingCount, MeshUpdateFlags.DontRecalculateBounds);
        if (PendingColors != null)
            _mesh.SetColors(PendingColors, 0, PendingCount, MeshUpdateFlags.DontRecalculateBounds);

        // 复用索引数组：索引恒为 0,1,2,...，只在数组增长时补齐新增部分，避免每帧重填与 GC。
        if (_indices == null || _indices.Length < PendingCount)
        {
            var newIndices = new int[PendingCount];
            if (_indices != null)
                System.Array.Copy(_indices, newIndices, _filledIndexCount);
            _indices = newIndices;
        }
        for (int i = _filledIndexCount; i < PendingCount; i++) _indices[i] = i;
        if (PendingCount > _filledIndexCount)
            _filledIndexCount = PendingCount;

        // calculateBounds:false 同样跳过 bounds 重算。
        _mesh.SetIndices(_indices, 0, PendingCount, MeshTopology.Points, 0, false);
        _mesh.bounds = new Bounds(Vector3.zero, new Vector3(boundsExtent * 2f, boundsExtent * 2f, boundsExtent * 2f));
        _mesh.UploadMeshData(false);
    }
    private void OnDestroy()
    {
        if(pointCloudStream)
        {
            pointCloudStream.OnPointCloudUpdated -= UpdateViewer;
        }
        if (_mesh != null)
        {
            if (Application.isPlaying)
                Destroy(_mesh);
            else
                DestroyImmediate(_mesh);
        }
    }
   
}
