using System.Collections.Generic;
using Orbbec;
using UnityEngine;
using UnityEngine.Events;

namespace OrbbecUnity
{
    [System.Serializable]
    public class PipelineInitEvent : UnityEvent { }

    public class OrbbecPipeline : MonoBehaviour
    {
        public OrbbecDevice orbbecDevice;
        public OrbbecProfile[] orbbecProfiles;
        public PipelineInitEvent onPipelineInit;

        private bool hasInit;
        private Pipeline pipeline;
        private Config config;
        private FramesetCallback framesetCallback;

        // streaming: 业务上是否处于“应当出流”的状态；autoPaused: 因暂停/切后台被自动停流，恢复时需自动重启。
        private bool streaming;
        private bool autoPaused;

        public bool HasInit
        {
            get
            {
                return hasInit;
            }
        }

        public Pipeline Pipeline
        {
            get
            {
                return pipeline;
            }
        }

        public Config Config
        {
            get
            {
                return config;
            }
        }

        void Start()
        {
            orbbecDevice.onDeviceFound.AddListener(InitPipeline);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.pauseStateChanged += OnEditorPauseStateChanged;
#endif
        }

        // 真机/Standalone：应用切后台或被系统暂停时触发。
        void OnApplicationPause(bool paused)
        {
            HandleAutoPause(paused);
        }

#if UNITY_EDITOR
        // 编辑器：点击 Pause/继续时触发。暂停时必须停掉原生回调线程，
        // 否则后台线程持续分配托管内存触发 GC，而主线程被暂停无法协调，会导致编辑器原生崩溃。
        private void OnEditorPauseStateChanged(UnityEditor.PauseState state)
        {
            HandleAutoPause(state == UnityEditor.PauseState.Paused);
        }
#endif

        // 暂停时停流（Stop 会让原生回调线程停止并 join），恢复时按原配置自动重启。
        private void HandleAutoPause(bool paused)
        {
            if (!hasInit || pipeline == null)
            {
                return;
            }

            if (paused)
            {
                if (!streaming || autoPaused)
                {
                    return;
                }

                try
                {
                    pipeline.Stop();
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[OrbbecPipeline] Auto stop on pause failed: {e.Message}");
                }
                autoPaused = true;
            }
            else
            {
                if (!autoPaused)
                {
                    return;
                }

                try
                {
                    pipeline.Start(config, framesetCallback);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[OrbbecPipeline] Auto restart on resume failed: {e.Message}");
                }
                autoPaused = false;
            }
        }

        void OnDestroy()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.pauseStateChanged -= OnEditorPauseStateChanged;
#endif
            if (hasInit)
            {
                hasInit = false;
                streaming = false;
                // 必须先 Stop 让原生回调线程停止并 join，再 Dispose，
                // 否则回调线程仍在投递帧时释放 pipeline 会导致编辑器原生崩溃。
                try
                {
                    pipeline.Stop();
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[OrbbecPipeline] Stop on destroy failed: {e.Message}");
                }

                config.Dispose();
                pipeline.Dispose();
            }
        }

        private void InitPipeline(Device device)
        {
            pipeline = new Pipeline(device);
            InitConfig();
            hasInit = true;
            onPipelineInit?.Invoke();
        }

        private void InitConfig()
        {
            config = new Config();
            if (orbbecProfiles == null || orbbecProfiles.Length == 0)
            {
                Debug.LogWarning("No Orbbec profiles configured for pipeline.");
                return;
            }

            var sensorTypes = new HashSet<SensorType>();
            foreach (var obProfile in orbbecProfiles)
            {
                if (obProfile != null)
                {
                    sensorTypes.Add(obProfile.sensorType);
                }
            }

            foreach (var sensorType in sensorTypes)
            {
                EnableFirstMatchingProfile(sensorType);
            }
        }

        private void EnableFirstMatchingProfile(SensorType sensorType)
        {
            for (int i = 0; i < orbbecProfiles.Length; i++)
            {
                var obProfile = orbbecProfiles[i];
                if (obProfile == null || obProfile.sensorType != sensorType)
                {
                    continue;
                }

                if (TryEnableProfile(obProfile))
                {
                    return;
                }
            }
        }

        private bool TryEnableProfile(OrbbecProfile obProfile)
        {
            if (TryEnableMatchedStreamProfile(obProfile))
            {
                return true;
            }

            var format = obProfile.GetNormalizedFormat();
            bool hasFullVideoParams = obProfile.width > 0
                && obProfile.height > 0
                && obProfile.fps > 0
                && format != Format.OB_FORMAT_ANY;

            if (hasFullVideoParams)
            {
                try
                {
                    config.EnableVideoStream(
                        obProfile.sensorType,
                        obProfile.width,
                        obProfile.height,
                        obProfile.fps,
                        format);
                    Debug.LogFormat("Profile enabled (video stream): {0} {1}x{2}@{3} {4}",
                        obProfile.sensorType,
                        obProfile.width,
                        obProfile.height,
                        obProfile.fps,
                        format);
                    return true;
                }
                catch (NativeException e)
                {
                    Debug.LogWarning(e.Message);
                }
            }

            try
            {
                config.EnableStream(obProfile.sensorType);
                Debug.LogFormat("Profile enabled (default stream): {0}", obProfile.sensorType);
                return true;
            }
            catch (NativeException e)
            {
                Debug.LogWarning(e.Message);
            }

            return false;
        }

        private bool TryEnableMatchedStreamProfile(OrbbecProfile obProfile)
        {
            StreamProfileList profileList = null;
            try
            {
                profileList = pipeline.GetStreamProfileList(obProfile.sensorType);
                if (profileList.ProfileCount() == 0)
                {
                    return false;
                }

                var format = obProfile.GetNormalizedFormat();
                uint count = profileList.ProfileCount();
                for (int i = 0; i < count; i++)
                {
                    StreamProfile streamProfile = null;
                    try
                    {
                        streamProfile = profileList.GetProfile(i);
                        var videoProfile = streamProfile.As<VideoStreamProfile>();
                        if (videoProfile == null)
                        {
                            continue;
                        }

                        if (obProfile.width > 0 && videoProfile.GetWidth() != obProfile.width)
                        {
                            continue;
                        }
                        if (obProfile.height > 0 && videoProfile.GetHeight() != obProfile.height)
                        {
                            continue;
                        }
                        if (obProfile.fps > 0 && videoProfile.GetFPS() != obProfile.fps)
                        {
                            continue;
                        }
                        if (format != Format.OB_FORMAT_ANY && videoProfile.GetFormat() != format)
                        {
                            continue;
                        }

                        config.EnableStream(streamProfile);
                        Debug.LogFormat("Profile enabled (matched): {0} {1}x{2}@{3} {4}",
                            obProfile.sensorType,
                            videoProfile.GetWidth(),
                            videoProfile.GetHeight(),
                            videoProfile.GetFPS(),
                            videoProfile.GetFormat());
                        streamProfile = null;
                        return true;
                    }
                    finally
                    {
                        streamProfile?.Dispose();
                    }
                }
            }
            catch (NativeException e)
            {
                Debug.LogWarning(e.Message);
            }
            finally
            {
                profileList?.Dispose();
            }

            return false;
        }

        public void SetFramesetCallback(FramesetCallback callback)
        {
            framesetCallback = callback;
        }

        public void StartPipeline()
        {
            pipeline.Start(config, framesetCallback);
            streaming = true;
            autoPaused = false;
        }

        public void StopPipeline()
        {
            if (!streaming)
            {
                return;
            }
            pipeline.Stop();
            streaming = false;
            autoPaused = false;
        }
    }
}
