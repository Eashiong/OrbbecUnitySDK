using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PointCloudViewer : MonoBehaviour
{
    [Tooltip("点大小（像素），即 billboard 四边形的屏幕边长")]
    [Range(0.5f, 40f)]
    public float pointSize = 8f;

    [Tooltip("使用深度伪彩（近=红，远=蓝），关闭则使用相机真彩色。AR 下伪彩更易分辨。")]
    public bool useDepthColor = true;

    [Tooltip("深度伪彩近端距离（米）")]
    public float depthMin = 0.2f;

    [Tooltip("深度伪彩远端距离（米）")]
    public float depthMax = 4.0f;

    [Tooltip("点云始终绘制在最前（ZTest Always），避免被 AR 背景/其他物体遮挡。")]
    public bool alwaysOnTop = true;

    [Tooltip("若不指定，将自动使用 Orbbec/PointCloudPoints 着色器创建材质")]
    public Material pointCloudMaterial;

    [Tooltip("点云包围盒半边长（米）。设得足够大以避免被视锥剔除，从而跳过每帧 bounds 重算。")]
    public float boundsExtent = 25f;

    [Header("均匀采样（提升渲染效率）")]
    [Tooltip("开启后对点云进行均匀抽稀，仅渲染部分点以降低顶点/填充压力。")]
    public bool enableDownsample = true;

    [Tooltip("可视化的最大点数。实际点数超过该值时，按固定步长均匀抽取到不超过此数量。")]
    [Min(1)]
    public int maxRenderPoints = 100000;

    public PointCloudStream pointCloudStream;

    private Mesh _mesh;
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;

    // 每个点扩展为 4 个共位顶点 + 6 个三角形索引。
    private Vector3[] _verts;     // 容量 = 点数 * 4，每帧重填
    private Color[] _colors;      // 容量 = 点数 * 4，每帧重填
    private Vector2[] _corners;   // 容量 = 点数 * 4，静态四角偏移，仅在增长时补齐
    private int[] _indices;       // 容量 = 点数 * 6，静态三角形索引，仅在增长时补齐
    // 已经填充静态 corner / 索引的点数（这些数据不随帧变化）。
    private int _filledPoints;

    private static readonly int PointSizeId = Shader.PropertyToID("_PointSize");
    private static readonly int UseDepthColorId = Shader.PropertyToID("_UseDepthColor");
    private static readonly int DepthMinId = Shader.PropertyToID("_DepthMin");
    private static readonly int DepthMaxId = Shader.PropertyToID("_DepthMax");
    private static readonly int ZTestId = Shader.PropertyToID("_ZTestMode");

    // 四角偏移：左下、右下、左上、右上。
    private static readonly Vector2[] CornerOffsets =
    {
        new Vector2(-1f, -1f),
        new Vector2( 1f, -1f),
        new Vector2(-1f,  1f),
        new Vector2( 1f,  1f),
    };

    void Start()
    {
        _mesh = new Mesh { name = "PointCloudBillboardMesh" };
        // 32 位索引支持百万级顶点（点数 * 4 很容易超过 65535）。
        _mesh.indexFormat = IndexFormat.UInt32;
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
                Debug.LogWarning("PointCloudViewer: 未找到 Orbbec/PointCloudPoints 着色器");
        }
        if (pointCloudMaterial != null)
            _meshRenderer.sharedMaterial = pointCloudMaterial;

        pointCloudStream.OnPointCloudUpdated += UpdateViewer;
    }

    void Update()
    {
        Material mat = _meshRenderer.sharedMaterial;
        if (mat == null)
            return;

        if (mat.HasProperty(PointSizeId))
            mat.SetFloat(PointSizeId, pointSize);
        if (mat.HasProperty(UseDepthColorId))
            mat.SetFloat(UseDepthColorId, useDepthColor ? 1f : 0f);
        if (mat.HasProperty(DepthMinId))
            mat.SetFloat(DepthMinId, depthMin);
        if (mat.HasProperty(DepthMaxId))
            mat.SetFloat(DepthMaxId, depthMax);
        if (mat.HasProperty(ZTestId))
            mat.SetFloat(ZTestId, (float)(alwaysOnTop ? CompareFunction.Always : CompareFunction.LessEqual));
    }

    public void UpdateViewer(Vector3[] PendingVertices, Color[] PendingColors, int PendingCount)
    {
        if (PendingCount <= 0)
        {
            _mesh.Clear();
            _filledPoints = 0;
            return;
        }

        // 均匀采样：当点数超过上限时，用固定步长抽稀，保证空间分布均匀。
        // step = ceil(PendingCount / maxRenderPoints)，抽出的点数 renderCount <= maxRenderPoints。
        int step = 1;
        if (enableDownsample && maxRenderPoints > 0 && PendingCount > maxRenderPoints)
            step = (PendingCount + maxRenderPoints - 1) / maxRenderPoints;
        int renderCount = (PendingCount + step - 1) / step;

        EnsureCapacity(renderCount);

        int vertCount = renderCount * 4;
        int idxCount = renderCount * 6;
        bool hasColor = PendingColors != null;

        // 把每个（抽样后的）点复制到 4 个共位顶点（corner 偏移在顶点着色器里完成扩展）。
        int dst = 0;
        for (int src = 0; src < PendingCount; src += step, dst++)
        {
            int v = dst << 2;
            Vector3 p = PendingVertices[src];
            _verts[v] = p;
            _verts[v + 1] = p;
            _verts[v + 2] = p;
            _verts[v + 3] = p;

            if (hasColor)
            {
                Color c = PendingColors[src];
                _colors[v] = c;
                _colors[v + 1] = c;
                _colors[v + 2] = c;
                _colors[v + 3] = c;
            }
        }

        // 先把索引收缩到 0，避免设置更少的顶点时旧索引越界（顶点数 < 索引引用）。
        _mesh.SetIndices(System.Array.Empty<int>(), MeshTopology.Triangles, 0, false);

        // DontRecalculateBounds：跳过对大量顶点重算包围盒，改用下方手动设置的大包围盒。
        _mesh.SetVertices(_verts, 0, vertCount, MeshUpdateFlags.DontRecalculateBounds);
        _mesh.SetUVs(0, _corners, 0, vertCount);
        if (hasColor)
            _mesh.SetColors(_colors, 0, vertCount, MeshUpdateFlags.DontRecalculateBounds);

        _mesh.SetIndices(_indices, 0, idxCount, MeshTopology.Triangles, 0, false);
        _mesh.bounds = new Bounds(Vector3.zero, new Vector3(boundsExtent * 2f, boundsExtent * 2f, boundsExtent * 2f));
        _mesh.UploadMeshData(false);
    }

    // 确保各缓冲容量足够，并补齐新增点的静态 corner 偏移与三角形索引。
    private void EnsureCapacity(int pointCount)
    {
        int vertCap = pointCount * 4;
        if (_verts == null || _verts.Length < vertCap)
        {
            int newCap = Mathf.Max(vertCap, (_verts?.Length ?? 0) * 2);
            _verts = new Vector3[newCap];
            _colors = new Color[newCap];

            var newCorners = new Vector2[newCap];
            if (_corners != null)
                System.Array.Copy(_corners, newCorners, _filledPoints * 4);
            _corners = newCorners;
        }

        int idxCap = pointCount * 6;
        if (_indices == null || _indices.Length < idxCap)
        {
            int newCap = Mathf.Max(idxCap, (_indices?.Length ?? 0) * 2);
            var newIndices = new int[newCap];
            if (_indices != null)
                System.Array.Copy(_indices, newIndices, _filledPoints * 6);
            _indices = newIndices;
        }

        for (int i = _filledPoints; i < pointCount; i++)
        {
            int v = i << 2;
            _corners[v] = CornerOffsets[0];
            _corners[v + 1] = CornerOffsets[1];
            _corners[v + 2] = CornerOffsets[2];
            _corners[v + 3] = CornerOffsets[3];

            int t = i * 6;
            // 两个三角形组成一个四边形（Cull Off，绕序无关）。
            _indices[t] = v;
            _indices[t + 1] = v + 1;
            _indices[t + 2] = v + 2;
            _indices[t + 3] = v + 2;
            _indices[t + 4] = v + 1;
            _indices[t + 5] = v + 3;
        }

        if (pointCount > _filledPoints)
            _filledPoints = pointCount;
    }

    private void OnDestroy()
    {
        if (pointCloudStream)
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
