using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Orbbec;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// 相机标定「阶段三：同步采集工具」。
///
/// 让固连在一起的手机 AR 相机与 Orbbec 彩色相机同时观测同一块 ChArUco 板，
/// 静止配对采集多组数据（手机图 + Orbbec 彩色图 + 两套内参 + AR 世界位姿），
/// 结束时落盘为单个 JSON 文件（按时间命名），再上传到服务器供 PC 端离线求解外参。
///
/// UI：开始采集 / 结束采集 / 上传采集 三个按钮 + 一个状态文本。
/// </summary>
public class CalibrationCapture : MonoBehaviour
{
    [Header("数据源")]
    [Tooltip("AR 相机管理器（取手机相机图与内参）。留空则自动在场景查找。")]
    public ARCameraManager cameraManager;

    [Tooltip("AR 相机（取世界位姿）。留空则用 cameraManager 所在相机或 Camera.main。")]
    public Camera arCamera;

    [Tooltip("Orbbec 点云流（取彩色图与彩色内参）。留空则自动在场景查找。")]
    public PointCloudStream pointCloudStream;

    [Header("UI")]
    public Button startButton;
    public Button stopButton;
    public Button uploadButton;
    [Tooltip("状态文本：保存/上传结果与失败原因都会显示在这里。")]
    public Text statusText;

    [Header("ChArUco 标定板参数（写入文件，供离线求解使用）")]
    public int boardSquaresX = 5;
    public int boardSquaresY = 7;
    [Tooltip("方格边长（米）")]
    public float boardSquareLenM = 0.04f;
    [Tooltip("ArUco 标记边长（米）")]
    public float boardMarkerLenM = 0.03f;
    [Tooltip("ArUco 字典名（与离线脚本一致），如 DICT_5X5_1000")]
    public string boardDictionary = "DICT_5X5_1000";

    [Header("硬件 / 版本标记")]
    public string rigVersion = "v1";

    [Header("自动采集策略（静止配对）")]
    [Tooltip("两次采集的最小时间间隔（秒）")]
    public float minCaptureInterval = 0.5f;
    [Tooltip("仅在设备静止时采集")]
    public bool requireStill = true;
    [Tooltip("判定静止的最大平移速度（米/秒）")]
    public float stillLinearSpeed = 0.03f;
    [Tooltip("判定静止的最大角速度（度/秒）")]
    public float stillAngularSpeed = 3f;
    [Tooltip("相对上一组采集，相机至少移动这么多（米）才采下一组，避免同一位姿重复采集")]
    public float minMovePosition = 0.04f;
    [Tooltip("相对上一组采集，相机至少旋转这么多（度）才采下一组")]
    public float minMoveRotation = 4f;

    [Header("图像编码")]
    [Range(1, 100)]
    public int jpgQuality = 90;

    [Header("上传")]
    [Tooltip("服务器地址。文件以 multipart/form-data 形式 POST，字段名为 file。")]
    public string uploadUrl = "http://192.168.2.12:9090";

    // ---- 运行时状态 ----
    private bool _isCapturing;
    private string _lastSavedPath;
    private bool _uploading;

    private readonly List<Sample> _samples = new List<Sample>();

    // Orbbec 最新彩色图缓存（主线程，OnColorFrameUpdated 回调中拷贝）
    private byte[] _orbbecColor;
    private int _orbbecColorW, _orbbecColorH;
    private bool _hasOrbbecColor;

    // 运动估计
    private Vector3 _prevPos;
    private Quaternion _prevRot;
    private bool _hasPrevPose;
    private float _curLinearSpeed;
    private float _curAngularSpeed;

    // 距离上次采集
    private float _lastCaptureTime = -999f;
    private Vector3 _lastCapturePos;
    private Quaternion _lastCaptureRot;
    private bool _hasLastCapturePose;

    // 采集中内参状态定时刷新（Orbbec 内参可能晚于首帧才就绪）
    private float _nextIntrinsicsStatusRefreshTime;

    // ---------------- 生命周期 ----------------

    private void Awake()
    {
        if (cameraManager == null)
        {
            cameraManager = FindObjectOfType<ARCameraManager>();
        }
        if (pointCloudStream == null)
        {
            pointCloudStream = FindObjectOfType<PointCloudStream>();
        }
        if (arCamera == null)
        {
            if (cameraManager != null)
            {
                arCamera = cameraManager.GetComponent<Camera>();
            }
            if (arCamera == null)
            {
                arCamera = Camera.main;
            }
        }

        if (startButton != null) startButton.onClick.AddListener(StartCapture);
        if (stopButton != null) stopButton.onClick.AddListener(StopCapture);
        if (uploadButton != null) uploadButton.onClick.AddListener(UploadCapture);
    }

    private void OnEnable()
    {
        if (pointCloudStream != null)
        {
            pointCloudStream.OnColorFrameUpdated += OnOrbbecColor;
        }
    }

    private void OnDisable()
    {
        if (pointCloudStream != null)
        {
            pointCloudStream.OnColorFrameUpdated -= OnOrbbecColor;
        }
    }

    private void Start()
    {
        if (cameraManager == null || pointCloudStream == null || arCamera == null)
        {
            SetStatus("初始化警告：未找到 " +
                      (cameraManager == null ? "ARCameraManager " : "") +
                      (pointCloudStream == null ? "PointCloudStream " : "") +
                      (arCamera == null ? "AR Camera " : "") +
                      "，请在 Inspector 中手动指定。");
        }
        else
        {
            SetStatus("就绪。点击「开始采集」。\n" + BuildIntrinsicsStatusBlock());
        }
        RefreshButtons();
    }

    // ---------------- Orbbec 彩色回调 ----------------

    private void OnOrbbecColor(byte[] rgb, int w, int h)
    {
        if (rgb == null || w <= 0 || h <= 0)
        {
            return;
        }

        int size = w * h * 3;
        if (rgb.Length < size)
        {
            return;
        }

        if (_orbbecColor == null || _orbbecColor.Length != size)
        {
            _orbbecColor = new byte[size];
        }
        Array.Copy(rgb, _orbbecColor, size);
        _orbbecColorW = w;
        _orbbecColorH = h;
        _hasOrbbecColor = true;
    }

    // ---------------- 主循环：自动静止配对采集 ----------------

    private void Update()
    {
        if (arCamera != null)
        {
            UpdateMotion();
        }

        if (!_isCapturing)
        {
            return;
        }

        if (Time.unscaledTime >= _nextIntrinsicsStatusRefreshTime)
        {
            _nextIntrinsicsStatusRefreshTime = Time.unscaledTime + 1f;
            SetStatus($"采集中... 已采集 {_samples.Count} 组\n{BuildIntrinsicsStatusBlock()}");
        }

        if (Time.unscaledTime - _lastCaptureTime < minCaptureInterval)
        {
            return;
        }

        if (requireStill && _hasPrevPose &&
            (_curLinearSpeed > stillLinearSpeed || _curAngularSpeed > stillAngularSpeed))
        {
            return;
        }

        // 距离上一组采集要有足够的位姿变化，避免静止时重复采集同一视角
        if (_hasLastCapturePose)
        {
            float moved = Vector3.Distance(arCamera.transform.position, _lastCapturePos);
            float rotated = Quaternion.Angle(arCamera.transform.rotation, _lastCaptureRot);
            if (moved < minMovePosition && rotated < minMoveRotation)
            {
                return;
            }
        }

        TryCaptureOne();
    }

    private void UpdateMotion()
    {
        Vector3 pos = arCamera.transform.position;
        Quaternion rot = arCamera.transform.rotation;
        float dt = Time.unscaledDeltaTime;
        if (_hasPrevPose && dt > 1e-4f)
        {
            _curLinearSpeed = Vector3.Distance(pos, _prevPos) / dt;
            _curAngularSpeed = Quaternion.Angle(rot, _prevRot) / dt;
        }
        _prevPos = pos;
        _prevRot = rot;
        _hasPrevPose = true;
    }

    // ---------------- 单次采集 ----------------

    private void TryCaptureOne()
    {
        if (cameraManager == null || pointCloudStream == null || arCamera == null)
        {
            return;
        }

        // 1) AR 内参
        if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics arIntr))
        {
            SetStatus($"采集中... 已采集 {_samples.Count} 组\n{BuildIntrinsicsStatusBlock()}");
            return;
        }

        // 2) Orbbec 彩色图 + 内参
        if (!_hasOrbbecColor)
        {
            SetStatus($"采集中... 已采集 {_samples.Count} 组（等待 Orbbec 彩色帧）\n{BuildIntrinsicsStatusBlock()}");
            return;
        }
        // Orbbec 彩色内参为「尽力获取」：取不到也不阻塞采集，离线可用 calibrateCameraCharuco
        // 从采集到的彩色图自标定内参。避免因取内参失败而完全无法采集。
        bool hasObIntr = pointCloudStream.TryGetColorIntrinsic(out CameraIntrinsic obIntr);

        // 3) AR 手机相机图（CPU 图）
        byte[] phoneJpg = TryEncodePhoneImage(out int phoneW, out int phoneH);
        if (phoneJpg == null)
        {
            SetStatus($"采集中... 已采集 {_samples.Count} 组（手机相机图获取失败，稍后重试）\n{BuildIntrinsicsStatusBlock()}");
            return;
        }

        // 4) Orbbec 彩色图编码
        byte[] obJpg = EncodeTopLeftToJpg(_orbbecColor, _orbbecColorW, _orbbecColorH, 3, jpgQuality);
        if (obJpg == null)
        {
            SetStatus($"采集中... 已采集 {_samples.Count} 组（Orbbec 图编码失败）\n{BuildIntrinsicsStatusBlock()}");
            return;
        }

        Vector3 pos = arCamera.transform.position;
        Quaternion rot = arCamera.transform.rotation;

        var sample = new Sample
        {
            index = _samples.Count,
            timestamp_ms = NowMs(),
            phone_intr = new Intr
            {
                fx = arIntr.focalLength.x,
                fy = arIntr.focalLength.y,
                cx = arIntr.principalPoint.x,
                cy = arIntr.principalPoint.y,
                w = arIntr.resolution.x,
                h = arIntr.resolution.y
            },
            orbbec_intr = new Intr
            {
                fx = hasObIntr ? obIntr.fx : 0f,
                fy = hasObIntr ? obIntr.fy : 0f,
                cx = hasObIntr ? obIntr.cx : 0f,
                cy = hasObIntr ? obIntr.cy : 0f,
                w = hasObIntr ? obIntr.width : _orbbecColorW,
                h = hasObIntr ? obIntr.height : _orbbecColorH
            },
            orbbec_intr_valid = hasObIntr,
            ar_world_pose = new Pose
            {
                pos = new[] { pos.x, pos.y, pos.z },
                rot = new[] { rot.x, rot.y, rot.z, rot.w }
            },
            phone_image_jpg_base64 = Convert.ToBase64String(phoneJpg),
            orbbec_image_jpg_base64 = Convert.ToBase64String(obJpg)
        };

        _samples.Add(sample);
        _lastCaptureTime = Time.unscaledTime;
        _lastCapturePos = pos;
        _lastCaptureRot = rot;
        _hasLastCapturePose = true;

        SetStatus($"采集中... 已采集 {_samples.Count} 组（手机 {phoneW}x{phoneH} / Orbbec {_orbbecColorW}x{_orbbecColorH}）。移动到新位姿继续。\n{BuildIntrinsicsStatusBlock()}");
    }

    // 采集手机相机 CPU 图并编码为「左上原点」的 JPG（与 AR 内参方向一致）
    private byte[] TryEncodePhoneImage(out int outW, out int outH)
    {
        outW = 0;
        outH = 0;
        if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            return null;
        }

        try
        {
            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width, image.height),
                outputFormat = TextureFormat.RGBA32,
                // 不做镜像/翻转：保持原生左上原点，与 TryGetIntrinsics 的主点/分辨率一致
                transformation = XRCpuImage.Transformation.None
            };

            int size = image.GetConvertedDataSize(conversionParams);
            var buffer = new NativeArray<byte>(size, Allocator.Temp);
            try
            {
                image.Convert(conversionParams, buffer);
                byte[] managed = buffer.ToArray();
                outW = image.width;
                outH = image.height;
                return EncodeTopLeftToJpg(managed, image.width, image.height, 4, jpgQuality);
            }
            finally
            {
                if (buffer.IsCreated)
                {
                    buffer.Dispose();
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CalibrationCapture] 手机相机图转换失败: {e.Message}");
            return null;
        }
        finally
        {
            image.Dispose();
        }
    }

    // ---------------- 结束采集：落盘 ----------------

    public void StartCapture()
    {
        if (_isCapturing)
        {
            return;
        }
        if (_uploading)
        {
            SetStatus("正在上传中，请稍候再开始新一轮采集。");
            return;
        }

        _samples.Clear();
        _lastSavedPath = null;
        _hasLastCapturePose = false;
        _lastCaptureTime = -999f;
        _isCapturing = true;
        SetStatus("采集中... 已采集 0 组。保持设备静止对准标定板，缓慢更换距离/角度。\n" + BuildIntrinsicsStatusBlock());
        RefreshButtons();
    }

    public void StopCapture()
    {
        if (!_isCapturing)
        {
            return;
        }
        _isCapturing = false;
        RefreshButtons();

        if (_samples.Count == 0)
        {
            SetStatus("结束采集：未采集到任何数据，未保存。请检查 AR/Orbbec 是否出图后重试。");
            return;
        }

        SaveToFile();
    }

    private void SaveToFile()
    {
        string fileName = DateTime.Now.ToString("MM_dd_HH_mm_ss") + ".json";
        string dir = Application.persistentDataPath;
        string path = Path.Combine(dir, fileName);

        try
        {
            var file = new CalibrationFile
            {
                created_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                rig_version = rigVersion,
                board = new BoardInfo
                {
                    squares_x = boardSquaresX,
                    squares_y = boardSquaresY,
                    square_len_m = boardSquareLenM,
                    marker_len_m = boardMarkerLenM,
                    dictionary = boardDictionary
                },
                samples = _samples
            };

            string json = JsonUtility.ToJson(file);
            File.WriteAllText(path, json);

            _lastSavedPath = path;
            long sizeKb = new FileInfo(path).Length / 1024;
            SetStatus($"保存成功：{fileName}\n共 {_samples.Count} 组，大小 {sizeKb} KB\n路径：{path}\n可点击「上传采集」上传到服务器。");
        }
        catch (Exception e)
        {
            _lastSavedPath = null;
            SetStatus($"保存失败：{e.GetType().Name}\n原因：{e.Message}\n目标路径：{path}");
            Debug.LogError($"[CalibrationCapture] 保存失败: {e}");
        }
        finally
        {
            RefreshButtons();
        }
    }

    // ---------------- 上传 ----------------

    public void UploadCapture()
    {
        if (_uploading)
        {
            return;
        }
        if (string.IsNullOrEmpty(_lastSavedPath) || !File.Exists(_lastSavedPath))
        {
            SetStatus("上传失败：没有可上传的文件。请先「结束采集」生成文件。");
            return;
        }
        if (string.IsNullOrEmpty(uploadUrl))
        {
            SetStatus("上传失败：上传地址 uploadUrl 为空。");
            return;
        }

        StartCoroutine(UploadCoroutine(_lastSavedPath));
    }

    private IEnumerator UploadCoroutine(string path)
    {
        _uploading = true;
        RefreshButtons();

        string fileName = Path.GetFileName(path);
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception e)
        {
            SetStatus($"上传失败：读取文件出错\n原因：{e.Message}");
            _uploading = false;
            RefreshButtons();
            yield break;
        }

        SetStatus($"上传中... {fileName}（{data.Length / 1024} KB）\n目标：{uploadUrl}");

        var form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("file", data, fileName, "application/json")
        };

        using (UnityWebRequest req = UnityWebRequest.Post(uploadUrl, form))
        {
            req.timeout = 60;
            yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            bool ok = req.result == UnityWebRequest.Result.Success;
#else
            bool ok = !req.isNetworkError && !req.isHttpError;
#endif
            if (ok)
            {
                string resp = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (resp != null && resp.Length > 200)
                {
                    resp = resp.Substring(0, 200) + "...";
                }
                SetStatus($"上传成功：{fileName}\nHTTP {(long)req.responseCode}\n服务器响应：{resp}");
            }
            else
            {
                string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (body != null && body.Length > 200)
                {
                    body = body.Substring(0, 200) + "...";
                }
                SetStatus($"上传失败：{fileName}\n错误类型：{req.result}\nHTTP {(long)req.responseCode}\n原因：{req.error}\n响应：{body}\n目标：{uploadUrl}");
                Debug.LogError($"[CalibrationCapture] 上传失败: {req.result} {req.responseCode} {req.error}");
            }
        }

        _uploading = false;
        RefreshButtons();
    }

    // ---------------- 工具 ----------------

    // 把「左上原点」的像素缓冲编码为方向正确（左上原点）的 JPG。
    // EncodeToJPG 将纹理首行视为图像底行，故按行翻转后再写入纹理。
    private static byte[] EncodeTopLeftToJpg(byte[] src, int w, int h, int channels, int quality)
    {
        if (src == null || w <= 0 || h <= 0)
        {
            return null;
        }

        int stride = w * channels;
        if (src.Length < stride * h)
        {
            return null;
        }

        TextureFormat fmt = channels == 4 ? TextureFormat.RGBA32 : TextureFormat.RGB24;
        Texture2D tex = null;
        try
        {
            tex = new Texture2D(w, h, fmt, false);
            byte[] flipped = new byte[stride * h];
            for (int y = 0; y < h; y++)
            {
                Buffer.BlockCopy(src, y * stride, flipped, (h - 1 - y) * stride, stride);
            }
            tex.LoadRawTextureData(flipped);
            tex.Apply(false);
            return tex.EncodeToJPG(quality);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CalibrationCapture] JPG 编码失败: {e.Message}");
            return null;
        }
        finally
        {
            if (tex != null)
            {
                Destroy(tex);
            }
        }
    }

    private static long NowMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private void RefreshButtons()
    {
        if (startButton != null) startButton.interactable = !_isCapturing && !_uploading;
        if (stopButton != null) stopButton.interactable = _isCapturing && !_uploading;
        if (uploadButton != null)
        {
            uploadButton.interactable = !_isCapturing && !_uploading &&
                                        !string.IsNullOrEmpty(_lastSavedPath);
        }
    }

    private void SetStatus(string msg)
    {
        if (statusText != null)
        {
            statusText.text = msg;
        }
        Debug.Log($"[CalibrationCapture] {msg}");
    }

    /// <summary>组装 AR + Orbbec 内参状态块：成功显示数值，失败显示原因。</summary>
    private string BuildIntrinsicsStatusBlock()
    {
        return FormatArIntrinsicsStatus() + "\n" + FormatOrbbecIntrinsicsStatus();
    }

    private string FormatArIntrinsicsStatus()
    {
        if (cameraManager == null)
        {
            return "AR 内参：失败 — 未找到 ARCameraManager";
        }

        if (cameraManager.TryGetIntrinsics(out XRCameraIntrinsics arIntr))
        {
            return $"AR 内参：fx={arIntr.focalLength.x:F2} fy={arIntr.focalLength.y:F2} " +
                   $"cx={arIntr.principalPoint.x:F2} cy={arIntr.principalPoint.y:F2} " +
                   $"{arIntr.resolution.x}x{arIntr.resolution.y}";
        }

        return "AR 内参：失败 — AR 会话未就绪或相机未授权，请等待初始化完成";
    }

    private string FormatOrbbecIntrinsicsStatus()
    {
        if (pointCloudStream == null)
        {
            return "Orbbec 内参：失败 — 未找到 PointCloudStream";
        }

        if (pointCloudStream.TryGetColorIntrinsic(out CameraIntrinsic obIntr))
        {
            return $"Orbbec 内参：fx={obIntr.fx:F2} fy={obIntr.fy:F2} " +
                   $"cx={obIntr.cx:F2} cy={obIntr.cy:F2} {obIntr.width}x{obIntr.height}";
        }

        if (!_hasOrbbecColor)
        {
            return "Orbbec 内参：失败 — 等待彩色帧以确定分辨率";
        }

        return "Orbbec 内参：失败 — profiles 与 camera_param 均未返回有效值（持续重试，可离线标定）";
    }

    // ---------------- 落盘数据模型 ----------------

    [Serializable]
    public class Intr
    {
        public float fx;
        public float fy;
        public float cx;
        public float cy;
        public int w;
        public int h;
    }

    [Serializable]
    public class Pose
    {
        public float[] pos; // [x, y, z]
        public float[] rot; // [qx, qy, qz, qw]
    }

    [Serializable]
    public class BoardInfo
    {
        public int squares_x;
        public int squares_y;
        public float square_len_m;
        public float marker_len_m;
        public string dictionary;
    }

    [Serializable]
    public class Sample
    {
        public int index;
        public long timestamp_ms;
        public Intr phone_intr;
        public Intr orbbec_intr;
        public bool orbbec_intr_valid;
        public Pose ar_world_pose;
        public string phone_image_jpg_base64;
        public string orbbec_image_jpg_base64;
    }

    [Serializable]
    public class CalibrationFile
    {
        public string created_at;
        public string rig_version;
        public BoardInfo board;
        public List<Sample> samples;
    }
}
