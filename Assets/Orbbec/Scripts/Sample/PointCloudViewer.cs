using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class PointCloudViewer : MonoBehaviour
{
    [Tooltip("点大小（像素）")]
    [Range(0.5f, 20f)]
    public float pointSize = 2f;

    [Tooltip("若不指定，将自动使用 Orbbec/PointCloudPoints 着色器创建材质")]
    public Material pointCloudMaterial;

    private Mesh _mesh;
    private MeshFilter _meshFilter;
    private MeshRenderer _meshRenderer;
    private static readonly int PointSizeId = Shader.PropertyToID("_PointSize");

    public PointCloudStream pointCloudStream;
    
  

    void Start()
    {
        _mesh = new Mesh { name = "PointCloudMesh" };
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
        
        _mesh.Clear();
        _mesh.SetVertices(PendingVertices, 0, PendingCount);
        if (PendingColors != null)
            _mesh.SetColors(PendingColors, 0, PendingCount);

        int[] indices = new int[PendingCount];
        for (int i = 0; i < PendingCount; i++) indices[i] = i;
        _mesh.SetIndices(indices, MeshTopology.Points, 0);
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
