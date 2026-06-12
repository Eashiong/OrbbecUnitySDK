using System;
using System.IO;
using System.Runtime.InteropServices;
using Orbbec;
using OrbbecUnity;
using UnityEngine;
using UnityEngine.UI;

public class Pointcloud : MonoBehaviour
{
    public OrbbecPipeline pipeline;
    public Button pointcloudButton;
    public Button colorPointcloudButton;
    public Text tipsText;

    private PointCloudFilter filter;
    private AlignFilter alignFilter;
    private Format format;
    private bool save;
    private bool pipelineReady;
    private byte[] data;

    private string pointcloudPath;
    private string colorPointcloudPath;
    private bool pointCloudSaved;

    void Start()
    {
        pointcloudPath = Application.persistentDataPath + "/points.ply";
        colorPointcloudPath = Application.persistentDataPath + "/color_points.ply";

        pointcloudButton.onClick.AddListener(SavePointcloud);
        colorPointcloudButton.onClick.AddListener(SaveColorPointcloud);

        pipeline.SetFramesetCallback(OnFrameset);
        pipeline.onPipelineInit.AddListener(OnPipelineInit);
    }

    void OnDestroy()
    {
        if (alignFilter != null)
        {
            alignFilter.Dispose();
            alignFilter = null;
        }

        if (filter != null)
        {
            filter.Dispose();
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
                Debug.LogWarning("Device not support frame sync");
            }

            filter = new PointCloudFilter();
            alignFilter = new AlignFilter(StreamType.OB_STREAM_COLOR);
            pipeline.StartPipeline();

            pipelineReady = true;
            if (tipsText != null)
            {
                tipsText.text = "点云就绪，点击按钮保存";
            }
            Debug.Log("Pointcloud pipeline ready");
        }
        catch (Exception e)
        {
            pipelineReady = false;
            Debug.LogError($"Pointcloud init failed: {e.Message}");
            if (tipsText != null)
            {
                tipsText.text = "点云初始化失败: " + e.Message;
            }
        }
    }

    void Update()
    {
        if (pointCloudSaved)
        {
            if (format == Format.OB_FORMAT_POINT)
            {
                tipsText.text = "Point cloud saved to: " + pointcloudPath;
            }
            else if (format == Format.OB_FORMAT_RGB_POINT)
            {
                tipsText.text = "Point cloud saved to: " + colorPointcloudPath;
            }
        }
    }

    private bool EnsureReady()
    {
        if (!pipelineReady || filter == null)
        {
            if (tipsText != null)
            {
                tipsText.text = "点云未就绪，请等待设备连接";
            }
            Debug.LogWarning("Pointcloud not ready");
            return false;
        }
        return true;
    }

    private void OnFrameset(Frameset frameset)
    {
        if (frameset == null)
        {
            return;
        }

        if (!save)
        {
            frameset.Dispose();
            return;
        }

        DepthFrame depthFrame = frameset.GetDepthFrame();
        ColorFrame colorFrame = frameset.GetColorFrame();

        if (format == Format.OB_FORMAT_POINT && depthFrame == null)
        {
            Debug.LogWarning("Depth frame empty, waiting for next frame");
            frameset.Dispose();
            return;
        }

        if (format == Format.OB_FORMAT_RGB_POINT && (depthFrame == null || colorFrame == null))
        {
            Debug.LogWarning("Depth or color frame empty, waiting for next frame");
            frameset.Dispose();
            return;
        }

        Frame alignedFrameset = alignFilter.Process(frameset);
        var frame = filter.Process(alignedFrameset);
        if (alignedFrameset != null)
        {
            alignedFrameset.Dispose();
        }
        if (frame != null)
        {
            var pointFrame = frame.As<PointsFrame>();
            var dataSize = pointFrame.GetDataSize();
            if (dataSize > 0)
            {
                if (data == null || data.Length != dataSize)
                {
                    data = new byte[dataSize];
                }
                pointFrame.CopyData(ref data);
                pointFrame.Dispose();
                frame.Dispose();

                pointCloudSaved = false;
                if (format == Format.OB_FORMAT_POINT)
                {
                    WritePointPly();
                }
                else if (format == Format.OB_FORMAT_RGB_POINT)
                {
                    WriteColorPointPly();
                }
                pointCloudSaved = true;
                save = false;
            }
            else
            {
                Debug.LogWarning("Point cloud data size is 0");
                pointFrame.Dispose();
                frame.Dispose();
            }
        }
        else
        {
            Debug.LogWarning("Point cloud filter returned no frame");
        }

        frameset.Dispose();
    }

    private void SavePointcloud()
    {
        if (!EnsureReady())
        {
            return;
        }

        format = Format.OB_FORMAT_POINT;
        filter.SetCreatePointFormat(format);
        save = true;
        if (tipsText != null)
        {
            tipsText.text = "正在保存深度点云...";
        }
    }

    private void SaveColorPointcloud()
    {
        if (!EnsureReady())
        {
            return;
        }

        format = Format.OB_FORMAT_RGB_POINT;
        filter.SetCreatePointFormat(format);
        save = true;
        if (tipsText != null)
        {
            tipsText.text = "正在保存彩色点云...";
        }
    }

    private void WritePointPly()
    {
        if (data == null || data.Length == 0)
        {
            return;
        }

        int pointSize = Marshal.SizeOf(typeof(Point));
        int pointsSize = data.Length / pointSize;

        Point[] points = new Point[pointsSize];

        IntPtr dataPtr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, dataPtr, data.Length);
        for (int i = 0; i < pointsSize; i++)
        {
            IntPtr pointPtr = new IntPtr(dataPtr.ToInt64() + i * pointSize);
            points[i] = Marshal.PtrToStructure<Point>(pointPtr);
        }
        Marshal.FreeHGlobal(dataPtr);

        using (var fs = new FileStream(pointcloudPath, FileMode.Create))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write("ply\n");
            writer.Write("format ascii 1.0\n");
            writer.Write("element vertex " + pointsSize + "\n");
            writer.Write("property float x\n");
            writer.Write("property float y\n");
            writer.Write("property float z\n");
            writer.Write("end_header\n");

            for (int i = 0; i < points.Length; i++)
            {
                writer.Write(points[i].x);
                writer.Write(" ");
                writer.Write(points[i].y);
                writer.Write(" ");
                writer.Write(points[i].z);
                writer.Write("\n");
            }
        }
    }

    private void WriteColorPointPly()
    {
        if (data == null || data.Length == 0)
        {
            return;
        }

        int pointSize = Marshal.SizeOf(typeof(ColorPoint));
        int pointsSize = data.Length / pointSize;

        ColorPoint[] points = new ColorPoint[pointsSize];

        IntPtr dataPtr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, dataPtr, data.Length);
        for (int i = 0; i < pointsSize; i++)
        {
            IntPtr pointPtr = new IntPtr(dataPtr.ToInt64() + i * pointSize);
            points[i] = Marshal.PtrToStructure<ColorPoint>(pointPtr);
        }
        Marshal.FreeHGlobal(dataPtr);

        using (var fs = new FileStream(colorPointcloudPath, FileMode.Create))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write("ply\n");
            writer.Write("format ascii 1.0\n");
            writer.Write("element vertex " + pointsSize + "\n");
            writer.Write("property float x\n");
            writer.Write("property float y\n");
            writer.Write("property float z\n");
            writer.Write("property uchar red\n");
            writer.Write("property uchar green\n");
            writer.Write("property uchar blue\n");
            writer.Write("end_header\n");

            for (int i = 0; i < points.Length; i++)
            {
                writer.Write(points[i].x);
                writer.Write(" ");
                writer.Write(points[i].y);
                writer.Write(" ");
                writer.Write(points[i].z);
                writer.Write(" ");
                writer.Write(points[i].r);
                writer.Write(" ");
                writer.Write(points[i].g);
                writer.Write(" ");
                writer.Write(points[i].b);
                writer.Write("\n");
            }
        }
    }
}
