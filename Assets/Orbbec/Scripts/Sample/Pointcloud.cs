using System;
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

            // v2: PointCloudFilter reads intrinsics/extrinsics from frame StreamProfiles at runtime.
            // Do not call pipeline.Pipeline.GetCameraParam() or filter.SetCameraParam().
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

        Frame alignedFrameset = null;
        Frame pointCloudFrame = null;
        try
        {
            alignedFrameset = alignFilter.Process(frameset);
            pointCloudFrame = filter.Process(alignedFrameset);
            if (pointCloudFrame == null)
            {
                Debug.LogWarning("Point cloud filter returned no frame");
                return;
            }

            if (pointCloudFrame.GetDataSize() == 0)
            {
                Debug.LogWarning("Point cloud data size is 0");
                return;
            }

            string outputPath = format == Format.OB_FORMAT_POINT ? pointcloudPath : colorPointcloudPath;
            PointCloudHelper.SavePointcloudToPly(outputPath, pointCloudFrame, false, false, 50f);

            pointCloudSaved = true;
            save = false;
        }
        catch (Exception e)
        {
            Debug.LogError($"Save point cloud failed: {e.Message}");
            if (tipsText != null)
            {
                tipsText.text = "点云保存失败: " + e.Message;
            }
        }
        finally
        {
            pointCloudFrame?.Dispose();
            alignedFrameset?.Dispose();
            frameset.Dispose();
        }
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
        pointCloudSaved = false;
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
        pointCloudSaved = false;
        if (tipsText != null)
        {
            tipsText.text = "正在保存彩色点云...";
        }
    }
}
