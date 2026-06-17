using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 相机标定「阶段五：Unity 运行时融合集成」。
///
/// 启动时读取阶段四导出的常量外参 <c>T_arCam←orbbec</c>（Unity 左手系，单位米），
/// 运行时每帧把 Orbbec 点云从「Orbbec 彩色相机坐标系」变换到「AR 世界坐标系」：
///
/// <code>
/// P_world = T_world←arCam(t) · T_arCam←orbbec · P_orbbec
/// </code>
///
/// - <c>T_arCam←orbbec</c>：本组件加载的常量外参（来自 orbbec_ar_extrinsic.json）。
/// - <c>T_world←arCam(t)</c>：ARFoundation 每帧实时提供（<c>arCamera.transform.localToWorldMatrix</c>），
///   已包含 VIO + IMU 融合的跟踪结果。
///
/// 实现要点：<see cref="PointCloudStream"/> / <see cref="PointCloudViewer"/> 输出的 mesh 顶点是
/// **Orbbec 相机坐标系下的原始点**。因此只需每帧把点云根对象的世界变换设为
/// <c>worldFromOrbbec = worldFromArCam · T_arCam←orbbec</c>，GPU 在常规渲染管线里即完成
/// 逐点矩阵乘法，无需 CPU 逐点循环（对应方案 5.2 的 GPU 批量变换建议）。
/// </summary>
public class OrbbecArFusion : MonoBehaviour
{
    [Header("外参文件")]
    [Tooltip("StreamingAssets 下的相对路径，指向阶段四导出的 orbbec_ar_extrinsic.json。")]
    public string extrinsicRelativePath = "T_arCam_from_orbbec/orbbec_ar_extrinsic.json";

    [Header("引用")]
    [Tooltip("AR 相机（提供每帧 T_world←arCam）。留空则用 Camera.main。")]
    public Camera arCamera;

    [Tooltip("点云渲染根对象（其 mesh 顶点位于 Orbbec 相机坐标系）。留空则自动查找 PointCloudViewer 所在对象。")]
    public Transform pointCloudRoot;

    [Header("调试")]
    [Tooltip("加载/状态信息输出到 Console。")]
    public bool verboseLog = true;

    /// <summary>外参是否已成功加载。</summary>
    public bool IsExtrinsicLoaded { get; private set; }

    /// <summary>常量外参 T_arCam←orbbec（Unity 左手系，米）。</summary>
    public Matrix4x4 ArCamFromOrbbec => _arCamFromOrbbec;

    private Matrix4x4 _arCamFromOrbbec = Matrix4x4.identity;

    public bool UseFusion;
    public void SetDisableFusion(bool v)
    {
        UseFusion = v;
    }

    private void Awake()
    {
        if (arCamera == null)
        {
            arCamera = Camera.main;
        }

        if (pointCloudRoot == null)
        {
            var viewer = FindObjectOfType<PointCloudViewer>();
            if (viewer != null)
            {
                pointCloudRoot = viewer.transform;
            }
        }
    }

    private void Start()
    {
        StartCoroutine(LoadExtrinsicCoroutine());
    }

    // ---------------- 5.1 加载外参 ----------------

    private IEnumerator LoadExtrinsicCoroutine()
    {
        string url = $"{Application.streamingAssetsPath}/{extrinsicRelativePath}".Replace('\\', '/');
        // 桌面/编辑器下 streamingAssetsPath 是普通路径，需补 file:// 协议；
        // Android 下已是 jar:file://，原样使用。
        if (!url.Contains("://"))
        {
            url = "file://" + url;
        }

        string json = null;
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            yield return request.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            bool ok = request.result == UnityWebRequest.Result.Success;
#else
            bool ok = !request.isNetworkError && !request.isHttpError;
#endif
            if (ok)
            {
                json = request.downloadHandler != null ? request.downloadHandler.text : null;
            }
            else
            {
                Debug.LogError($"[OrbbecArFusion] 读取外参失败：{request.error}\nURL：{url}");
            }
        }

        if (string.IsNullOrEmpty(json))
        {
            yield break;
        }

        if (TryParseExtrinsic(json, out _arCamFromOrbbec))
        {
            IsExtrinsicLoaded = true;
            if (verboseLog)
            {
                Vector3 t = _arCamFromOrbbec.GetColumn(3);
                Debug.Log($"[OrbbecArFusion] 外参加载成功 |t|={t.magnitude * 100f:F1}cm " +
                          $"t=({t.x:F3},{t.y:F3},{t.z:F3})m\n来源：{url}");
            }
        }
        else
        {
            Debug.LogError($"[OrbbecArFusion] 外参解析失败，JSON 中缺少有效的 T_arCam_from_orbbec（需 16 个元素）。");
        }
    }

    /// <summary>
    /// 解析 orbbec_ar_extrinsic.json，按行主序 16 元素填充 Matrix4x4。
    /// 行主序布局：[r00,r01,r02,tx, r10,r11,r12,ty, r20,r21,r22,tz, 0,0,0,1]。
    /// </summary>
    public static bool TryParseExtrinsic(string json, out Matrix4x4 matrix)
    {
        matrix = Matrix4x4.identity;
        if (string.IsNullOrEmpty(json))
        {
            return false;
        }

        try
        {
            ExtrinsicFile parsed = JsonUtility.FromJson<ExtrinsicFile>(json);
            if (parsed == null || parsed.T_arCam_from_orbbec == null || parsed.T_arCam_from_orbbec.Length < 16)
            {
                return false;
            }

            float[] a = parsed.T_arCam_from_orbbec;
            // 行主序数组 a[row*4 + col] 直接映射到 Matrix4x4.mRC。
            matrix.m00 = a[0];  matrix.m01 = a[1];  matrix.m02 = a[2];  matrix.m03 = a[3];
            matrix.m10 = a[4];  matrix.m11 = a[5];  matrix.m12 = a[6];  matrix.m13 = a[7];
            matrix.m20 = a[8];  matrix.m21 = a[9];  matrix.m22 = a[10]; matrix.m23 = a[11];
            matrix.m30 = a[12]; matrix.m31 = a[13]; matrix.m32 = a[14]; matrix.m33 = a[15];
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[OrbbecArFusion] 外参 JSON 解析异常：{e.Message}");
            return false;
        }
    }

    // ---------------- 5.2 把 Orbbec 点变换到 AR 世界系 ----------------

    // 用 LateUpdate：在 ARFoundation 当帧更新完相机位姿之后再放置点云，避免落后一帧。
    private void LateUpdate()
    {
        if(!UseFusion)
        {
            pointCloudRoot.SetPositionAndRotation(arCamera.transform.position, arCamera.transform.rotation);
            pointCloudRoot.localScale = Vector3.one;
            return;
        }
        if (!IsExtrinsicLoaded || arCamera == null || pointCloudRoot == null)
        {
            return;
        }

        Matrix4x4 worldFromArCam = arCamera.transform.localToWorldMatrix;
        Matrix4x4 worldFromOrbbec = worldFromArCam * _arCamFromOrbbec;

        // 外参 + AR 位姿均为刚体变换（无缩放），直接取平移与旋转放到点云根对象的世界变换上。
        // 这样 mesh（Orbbec 坐标系顶点）经标准渲染管线即被变换到 AR 世界系，逐点乘法在 GPU 完成。
        Vector3 position = worldFromOrbbec.GetColumn(3);
        Quaternion rotation = worldFromOrbbec.rotation;
        pointCloudRoot.SetPositionAndRotation(position, rotation);
        pointCloudRoot.localScale = Vector3.one;
    }

    // ---------------- 数据模型 ----------------

    [Serializable]
    private class ExtrinsicFile
    {
        public float[] T_arCam_from_orbbec;
    }
}
