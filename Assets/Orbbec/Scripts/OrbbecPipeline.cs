using System.Collections;
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
        }

        void OnDestroy()
        {
            if (hasInit)
            {
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
            EnableFirstMatchingProfile(SensorType.OB_SENSOR_COLOR);
            EnableFirstMatchingProfile(SensorType.OB_SENSOR_DEPTH);
            EnableFirstMatchingProfile(SensorType.OB_SENSOR_IR);
            EnableFirstMatchingProfile(SensorType.OB_SENSOR_IR_LEFT);
            EnableFirstMatchingProfile(SensorType.OB_SENSOR_IR_RIGHT);
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
            StreamProfileList profileList = null;
            try
            {
                profileList = pipeline.GetStreamProfileList(obProfile.sensorType);
                if (profileList.ProfileCount() == 0)
                {
                    return false;
                }

                var matchedProfile = FindMatchingVideoProfile(profileList, obProfile);
                if (matchedProfile != null)
                {
                    config.EnableStream(matchedProfile);
                    Debug.LogFormat("Profile enabled: {0}x{1}@{2} {3}",
                        matchedProfile.GetWidth(),
                        matchedProfile.GetHeight(),
                        matchedProfile.GetFPS(),
                        matchedProfile.GetFormat());
                    return true;
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

        private static VideoStreamProfile FindMatchingVideoProfile(StreamProfileList profileList, OrbbecProfile obProfile)
        {
            var format = obProfile.GetNormalizedFormat();
            uint count = profileList.ProfileCount();

            for (int i = 0; i < count; i++)
            {
                StreamProfile streamProfile = null;
                VideoStreamProfile videoProfile = null;
                try
                {
                    streamProfile = profileList.GetProfile(i);
                    videoProfile = streamProfile.As<VideoStreamProfile>();
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

                    streamProfile = null;
                    return videoProfile;
                }
                finally
                {
                    streamProfile?.Dispose();
                }
            }

            return null;
        }

        public void SetFramesetCallback(FramesetCallback callback)
        {
            framesetCallback = callback;
        }

        public void StartPipeline()
        {
            pipeline.Start(config, framesetCallback);
        }

        public void StopPipeline()
        {
            pipeline.Stop();
        }
    }
}
