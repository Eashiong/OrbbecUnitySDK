using System;
using System.Runtime.InteropServices;
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
    [Tooltip("最大显示点数")]
    public int maxPointCount = 50000;

    [Tooltip("是否使用颜色点云（需要深度与彩色对齐）")]
    public bool useColorPointCloud = true;

    [Tooltip("彩色点云使用硬件 D2C 对齐（设备需支持）。开启可省去 CPU 软件对齐开销；若设备/档位不支持会自动回退到软件对齐。")]
    public bool useHardwareD2CAlign = false;

    private PointCloudFilter filter;
    private AlignFilter alignFilter;
    private bool _hwAlignActive;
    private Format pointFormat;
    private bool pipelineReady;

    // 点云原始数据按 12B / 24B 紧凑排列，与下面结构体内存布局一致，可直接 reinterpret。
    [StructLayout(LayoutKind.Sequential)]
    private struct OBPoint
    {
        public float x;
        public float y;
        public float z;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OBColorPoint
    {
        public float x;
        public float y;
        public float z;
        public float r;
        public float g;
        public float b;
    }

    private byte[] rawData;

    // 回调线程与销毁（Dispose 原生对象）互斥，避免 Stop/退出 Play 时的竞态崩溃。
    private readonly object _processLock = new object();
    private volatile bool _disposed;

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
        // 先阻止后续回调进入处理，再在锁内释放原生对象：
        // 若回调线程正处于 ProcessFramesetLocked 中，OnDestroy 会等待其完成后再 Dispose，
        // 之后到达的回调因 _disposed/pipelineReady 为假而直接跳过，杜绝 use-after-free 崩溃。
        pipelineReady = false;

        lock (_processLock)
        {
            _disposed = true;

            _colorConvertFilter?.Dispose();
            _colorConvertFilter = null;

            alignFilter?.Dispose();
            alignFilter = null;

            filter?.Dispose();
            filter = null;
        }
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

            // 彩色点云需要深度对齐到彩色。两种方式：
            // - 硬件 D2C：由设备输出已对齐的深度，主机几乎零开销（需设备支持）；
            // - 软件对齐：用 AlignFilter 在 CPU 上对齐，开销较大。
            _hwAlignActive = false;
            if (useColorPointCloud && useHardwareD2CAlign)
            {
                try
                {
                    pipeline.Config.SetAlignMode(AlignMode.ALIGN_D2C_HW_MODE);
                    _hwAlignActive = true;
                    Debug.Log("[PointCloudStream] Using hardware D2C align");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[PointCloudStream] Enable HW D2C failed, fallback to software align: {e.Message}");
                }
            }

            if (useColorPointCloud && !_hwAlignActive)
            {
                alignFilter = new AlignFilter(StreamType.OB_STREAM_COLOR);
            }

            ApplyPointFormat();

            try
            {
                pipeline.StartPipeline();
            }
            catch (Exception startEx) when (_hwAlignActive)
            {
                // 硬件 D2C 在当前档位不被支持导致启动失败时，回退为软件对齐再启动一次。
                Debug.LogWarning($"[PointCloudStream] Start with HW D2C failed, retry with software align: {startEx.Message}");
                _hwAlignActive = false;
                try { pipeline.Config.SetAlignMode(AlignMode.ALIGN_DISABLE); } catch { }
                if (alignFilter == null)
                {
                    alignFilter = new AlignFilter(StreamType.OB_STREAM_COLOR);
                }
                pipeline.StartPipeline();
            }

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

        lock (_processLock)
        {
            if (_disposed || !pipelineReady || filter == null)
            {
                frameset.Dispose();
                return;
            }

            ProcessFramesetLocked(frameset);
        }
    }

    private void ProcessFramesetLocked(Frameset frameset)
    {
        // 关键：回调拿到的每一个帧都持有帧池里的一块缓冲，必须显式 Dispose。
        // 否则只能等 GC 终结器回收，暂停时 GC 停摆会导致帧池耗尽、原生采集线程崩溃。
        DepthFrame depthFrame = null;
        ColorFrame colorFrame = null;
        Frame alignedFrameset = null;
        Frame pointCloudFrame = null;
        PointsFrame pointFrame = null;
        try
        {
            depthFrame = frameset.GetDepthFrame();
            colorFrame = frameset.GetColorFrame();

            if (depthFrame == null)
            {
                return;
            }

            if (useColorPointCloud && colorFrame == null)
            {
                return;
            }

            if (colorFrame != null)
            {
                ExtractColorFrame(colorFrame);
            }

            Frame filterInput = frameset;
            // 硬件 D2C 时 frameset 已对齐，无需软件 AlignFilter。
            if (useColorPointCloud && alignFilter != null)
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

            pointFrame = pointCloudFrame.As<PointsFrame>();
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
            if (totalPoints <= 0)
            {
                return;
            }

            // 点云是 width×height 的二维网格，必须按二维行列等步长降采样，
            // 否则按一维等距抽取会因步长与行宽不对齐而产生条纹（摩尔纹）。
            int width = (int)pointFrame.GetWidth();
            int height = (int)pointFrame.GetHeight();
            if (width <= 0 || height <= 0 || width * height > totalPoints)
            {
                width = totalPoints;
                height = 1;
            }

            int stride = 1;
            if (maxPointCount > 0 && totalPoints > maxPointCount)
            {
                stride = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt((float)totalPoints / maxPointCount)));
            }

            int gridCols = (width + stride - 1) / stride;
            int gridRows = (height + stride - 1) / stride;
            int capacity = gridCols * gridRows;
            if (capacity <= 0)
            {
                return;
            }

            lock (_bufferLock)
            {
                if (_pendingVertices == null || _pendingVertices.Length != capacity)
                {
                    _pendingVertices = new Vector3[capacity];
                    _pendingColors = useColorPointCloud ? new Color[capacity] : null;
                }
            }

            int count = useColorPointCloud
                ? FillColorPointBuffers(rawData, width, height, stride, positionScale, _pendingVertices, _pendingColors)
                : FillPointBuffers(rawData, width, height, stride, positionScale, _pendingVertices);

            if (count <= 0)
            {
                return;
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
            pointFrame?.Dispose();
            pointCloudFrame?.Dispose();
            alignedFrameset?.Dispose();
            depthFrame?.Dispose();
            colorFrame?.Dispose();
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

    // 二维网格行列等步长采样，跳过无深度的无效点，返回实际写入的点数。
    // 直接把字节缓冲 reinterpret 成结构体，避免逐字段 BitConverter（千万次带边界检查的调用）。
    private static int FillPointBuffers(byte[] data, int width, int height, int stride,
        float positionScale, Vector3[] outVertices)
    {
        float meterScale = positionScale * 0.001f;
        ReadOnlySpan<OBPoint> points = MemoryMarshal.Cast<byte, OBPoint>(data);
        int capacity = outVertices.Length;
        int count = 0;
        for (int y = 0; y < height; y += stride)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x += stride)
            {
                if (count >= capacity)
                {
                    return count;
                }
                OBPoint p = points[rowOffset + x];
                if (p.z == 0f)
                {
                    continue;
                }
                outVertices[count++] = new Vector3(p.x, p.y, p.z) * meterScale;
            }
        }
        return count;
    }

    // 二维网格行列等步长采样（彩色点云），跳过无深度的无效点，返回实际写入的点数。
    private static int FillColorPointBuffers(byte[] data, int width, int height, int stride,
        float positionScale, Vector3[] outVertices, Color[] outColors)
    {
        float meterScale = positionScale * 0.001f;
        ReadOnlySpan<OBColorPoint> points = MemoryMarshal.Cast<byte, OBColorPoint>(data);
        int capacity = outVertices.Length;
        int count = 0;
        for (int y = 0; y < height; y += stride)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x += stride)
            {
                if (count >= capacity)
                {
                    return count;
                }
                OBColorPoint p = points[rowOffset + x];
                if (p.z == 0f)
                {
                    continue;
                }
                outVertices[count] = new Vector3(p.x, p.y, p.z) * meterScale;
                outColors[count] = new Color(Mathf.Clamp01(p.r), Mathf.Clamp01(p.g), Mathf.Clamp01(p.b), 1f);
                count++;
            }
        }
        return count;
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
