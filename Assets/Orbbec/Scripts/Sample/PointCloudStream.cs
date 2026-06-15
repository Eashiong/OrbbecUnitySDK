using System;
using Orbbec;
using OrbbecUnity;
using UnityEngine;

/// <summary>
/// 高性能实时点云流：回调线程解析点云，主线程通过事件推送顶点/颜色数据。
/// v2：使用 AlignFilter + PointCloudFilter，内参/外参由帧 StreamProfile 自动获取。
/// </summary>
public class PointCloudStream : MonoBehaviour
{
    [Header("Pipeline")]
    public OrbbecPipeline pipeline;

    [Header("点云设置")]
    [Tooltip("最大显示点数，过大会影响帧率")]
    public int maxPointCount = 50000;

    [Tooltip("是否使用颜色点云（需要深度与彩色对齐）")]
    public bool useColorPointCloud = true;

    private PointCloudFilter filter;
    private AlignFilter alignFilter;
    private Format pointFormat;
    private bool pipelineReady;

    private byte[] rawData;

    // 双缓冲：回调线程写入 _pending*，主线程在 Update 中提交
    private readonly object _bufferLock = new object();
    private Vector3[] _pendingVertices;
    private Color[] _pendingColors;
    private int pendingCount;
    private bool hasPendingData;

    public event Action<Vector3[], Color[], int> OnPointCloudUpdated;

    /// <summary>
    /// 彩色帧更新回调 (RGB 字节数据, 宽, 高)
    /// </summary>
    public event Action<byte[], int, int> OnColorFrameUpdated;

    private const int PointStructSize = 12;
    private const int ColorPointStructSize = 24;

    private FormatConvertFilter _colorConvertFilter;
    private Format _lastColorFmt = Format.OB_FORMAT_UNKNOWN;
    private byte[] _pendingColorBytes;
    private int _pendingColorW, _pendingColorH;
    private bool _hasPendingColor;

    void Start()
    {
        pipeline.SetFramesetCallback(OnFrameset);
        pipeline.onPipelineInit.AddListener(OnPipelineInit);
    }

    void OnDestroy()
    {
        _colorConvertFilter?.Dispose();
        _colorConvertFilter = null;

        alignFilter?.Dispose();
        alignFilter = null;

        filter?.Dispose();
        filter = null;
    }

    private void OnPipelineInit()
    {
        try
        {
            pipeline.Config.SetFrameAggregateOutputMode(
                FrameAggregateOutputMode.OB_FRAME_AGGREGATE_OUTPUT_ALL_TYPE_FRAME_REQUIRE);

            try
            {
                pipeline.Pipeline.EnableFrameSync();
            }
            catch (Exception)
            {
                Debug.LogWarning("[PointCloudStream] Device not support frame sync");
            }

            // v2: PointCloudFilter reads intrinsics/extrinsics from frame StreamProfiles at runtime.
            filter = new PointCloudFilter();
            filter.SetCoordinateSystem(CoordinateSystemType.OB_LEFT_HAND_COORDINATE_SYSTEM);
            alignFilter = new AlignFilter(StreamType.OB_STREAM_COLOR);

            ApplyPointFormat();
            pipeline.StartPipeline();

            pipelineReady = true;
            Debug.Log("[PointCloudStream] Pipeline ready");
        }
        catch (Exception e)
        {
            pipelineReady = false;
            Debug.LogError($"[PointCloudStream] Init failed: {e.Message}");
        }
    }

    /// <summary>
    /// 根据 useColorPointCloud 更新点云格式，可在运行时调用。
    /// </summary>
    public void ApplyPointFormat()
    {
        if (filter == null)
        {
            return;
        }

        pointFormat = useColorPointCloud ? Format.OB_FORMAT_RGB_POINT : Format.OB_FORMAT_POINT;
        filter.SetCreatePointFormat(pointFormat);
        filter.SetColorDataNormalization(useColorPointCloud);
    }

    private void OnFrameset(Frameset frameset)
    {
        if (frameset == null)
        {
            return;
        }

        if (!pipelineReady || filter == null)
        {
            frameset.Dispose();
            return;
        }

        DepthFrame depthFrame = frameset.GetDepthFrame();
        ColorFrame colorFrame = frameset.GetColorFrame();

        if (depthFrame == null)
        {
            frameset.Dispose();
            return;
        }

        if (useColorPointCloud && colorFrame == null)
        {
            frameset.Dispose();
            return;
        }

        if (colorFrame != null)
        {
            ExtractColorFrame(colorFrame);
        }

        Frame alignedFrameset = null;
        Frame pointCloudFrame = null;
        try
        {
            Frame filterInput = frameset;
            if (useColorPointCloud)
            {
                alignedFrameset = alignFilter.Process(frameset);
                if (alignedFrameset != null)
                {
                    filterInput = alignedFrameset;
                }
            }

            pointCloudFrame = filter.Process(filterInput);
            if (pointCloudFrame == null)
            {
                return;
            }

            var pointFrame = pointCloudFrame.As<PointsFrame>();
            int dataSize = (int)pointFrame.GetDataSize();
            if (dataSize <= 0)
            {
                return;
            }

            if (rawData == null || rawData.Length != dataSize)
            {
                rawData = new byte[dataSize];
            }

            pointFrame.CopyData(ref rawData);
            float positionScale = pointFrame.GetPositionValueScale();

            int structSize = useColorPointCloud ? ColorPointStructSize : PointStructSize;
            int totalPoints = dataSize / structSize;
            int count = Mathf.Min(totalPoints, maxPointCount);
            if (count <= 0)
            {
                return;
            }

            lock (_bufferLock)
            {
                if (_pendingVertices == null || _pendingVertices.Length != count)
                {
                    _pendingVertices = new Vector3[count];
                    _pendingColors = useColorPointCloud ? new Color[count] : null;
                }
            }

            if (useColorPointCloud)
            {
                FillColorPointBuffers(rawData, totalPoints, count, positionScale, _pendingVertices, _pendingColors);
            }
            else
            {
                FillPointBuffers(rawData, totalPoints, count, positionScale, _pendingVertices);
            }

            lock (_bufferLock)
            {
                pendingCount = count;
                hasPendingData = true;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PointCloudStream] Process frameset failed: {e.Message}");
        }
        finally
        {
            pointCloudFrame?.Dispose();
            alignedFrameset?.Dispose();
            frameset.Dispose();
        }
    }

    void Update()
    {
        Vector3[] vertices = null;
        Color[] colors = null;
        int count = 0;
        byte[] colorBytes = null;
        int colorW = 0, colorH = 0;

        lock (_bufferLock)
        {
            if (_hasPendingColor)
            {
                _hasPendingColor = false;
                colorBytes = _pendingColorBytes;
                colorW = _pendingColorW;
                colorH = _pendingColorH;
            }

            if (hasPendingData && pendingCount > 0)
            {
                hasPendingData = false;
                vertices = _pendingVertices;
                colors = _pendingColors;
                count = pendingCount;
            }
        }

        if (colorBytes != null)
        {
            OnColorFrameUpdated?.Invoke(colorBytes, colorW, colorH);
        }

        if (vertices != null && count > 0)
        {
            OnPointCloudUpdated?.Invoke(vertices, colors, count);
        }
    }

    private static void FillPointBuffers(byte[] data, int totalPoints, int sampleCount,
        float positionScale, Vector3[] outVertices)
    {
        float meterScale = positionScale * 0.001f;
        for (int i = 0; i < sampleCount; i++)
        {
            float t = sampleCount <= 1 ? 0f : (float)i / (sampleCount - 1);
            int index = Mathf.Clamp(Mathf.RoundToInt(t * (totalPoints - 1)), 0, totalPoints - 1);
            int offset = index * PointStructSize;
            float x = BitConverter.ToSingle(data, offset);
            float y = BitConverter.ToSingle(data, offset + 4);
            float z = BitConverter.ToSingle(data, offset + 8);
            outVertices[i] = new Vector3(x, y, z) * meterScale;
        }
    }

    private static void FillColorPointBuffers(byte[] data, int totalPoints, int sampleCount,
        float positionScale, Vector3[] outVertices, Color[] outColors)
    {
        float meterScale = positionScale * 0.001f;
        for (int i = 0; i < sampleCount; i++)
        {
            float t = sampleCount <= 1 ? 0f : (float)i / (sampleCount - 1);
            int index = Mathf.Clamp(Mathf.RoundToInt(t * (totalPoints - 1)), 0, totalPoints - 1);
            int offset = index * ColorPointStructSize;
            float x = BitConverter.ToSingle(data, offset);
            float y = BitConverter.ToSingle(data, offset + 4);
            float z = BitConverter.ToSingle(data, offset + 8);
            float r = BitConverter.ToSingle(data, offset + 12);
            float g = BitConverter.ToSingle(data, offset + 16);
            float b = BitConverter.ToSingle(data, offset + 20);
            outVertices[i] = new Vector3(x, y, z) * meterScale;
            outColors[i] = new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
        }
    }

    private void ExtractColorFrame(ColorFrame colorFrame)
    {
        try
        {
            Format fmt = colorFrame.GetFormat();
            int w = (int)colorFrame.GetWidth();
            int h = (int)colorFrame.GetHeight();
            if (w == 0 || h == 0)
            {
                return;
            }

            if (fmt == Format.OB_FORMAT_RGB)
            {
                int size = (int)colorFrame.GetDataSize();
                if (_pendingColorBytes == null || _pendingColorBytes.Length != size)
                {
                    _pendingColorBytes = new byte[size];
                }
                colorFrame.CopyData(ref _pendingColorBytes);
            }
            else
            {
                ConvertFormat? cvt = GetConvertFormat(fmt);
                if (!cvt.HasValue)
                {
                    return;
                }

                if (_colorConvertFilter == null || _lastColorFmt != fmt)
                {
                    _colorConvertFilter?.Dispose();
                    _colorConvertFilter = new FormatConvertFilter();
                    _colorConvertFilter.SetConvertFormat(cvt.Value);
                    _lastColorFmt = fmt;
                }

                Frame converted = _colorConvertFilter.Process(colorFrame);
                if (converted == null)
                {
                    return;
                }

                int size = (int)converted.GetDataSize();
                if (_pendingColorBytes == null || _pendingColorBytes.Length != size)
                {
                    _pendingColorBytes = new byte[size];
                }
                converted.CopyData(ref _pendingColorBytes);
                w = (int)((VideoFrame)converted).GetWidth();
                h = (int)((VideoFrame)converted).GetHeight();
                converted.Dispose();
            }

            lock (_bufferLock)
            {
                _pendingColorW = w;
                _pendingColorH = h;
                _hasPendingColor = true;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PointCloudStream] 提取彩色帧失败: {e.Message}");
        }
    }

    private static ConvertFormat? GetConvertFormat(Format fmt)
    {
        switch (fmt)
        {
            case Format.OB_FORMAT_YUYV: return ConvertFormat.FORMAT_YUYV_TO_RGB;
            case Format.OB_FORMAT_MJPG: return ConvertFormat.FORMAT_MJPG_TO_RGB;
            case Format.OB_FORMAT_I420: return ConvertFormat.FORMAT_I420_TO_RGB;
            case Format.OB_FORMAT_NV21: return ConvertFormat.FORMAT_NV21_TO_RGB;
            case Format.OB_FORMAT_NV12: return ConvertFormat.FORMAT_NV12_TO_RGB;
            case Format.OB_FORMAT_UYVY: return ConvertFormat.FORMAT_UYVY_TO_RGB;
            default: return null;
        }
    }
}
