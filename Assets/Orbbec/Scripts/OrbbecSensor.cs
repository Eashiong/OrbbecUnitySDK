using System.Collections;
using Orbbec;
using UnityEngine;
using UnityEngine.Events;

namespace OrbbecUnity
{
    [System.Serializable]
    public class SensorInitEvent : UnityEvent { }

    public class OrbbecSensor : MonoBehaviour
    {
        public OrbbecDevice orbbecDevice;
        public SensorType sensorType;
        public OrbbecProfile[] orbbecProfiles;
        public SensorInitEvent onSensorInit;

        private Sensor sensor;
        private VideoStreamProfile streamProfile;
        private FrameCallback frameCallback;

        public Sensor Sensor
        {
            get
            {
                return sensor;
            }
        }

        void Start()
        {
            orbbecDevice.onDeviceFound.AddListener(InitSensor);
        }

        void OnDestroy()
        {
            if(sensor != null)
            {
                sensor.Dispose();
            }
        }

        private VideoStreamProfile FindProfile(OrbbecProfile obProfile)
        {
            if (obProfile.sensorType != sensor.GetSensorType())
            {
                return null;
            }

            StreamProfileList profileList = null;
            try
            {
                profileList = sensor.GetStreamProfileList();
                if (profileList.ProfileCount() == 0)
                {
                    return null;
                }

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

                        Debug.LogFormat("Profile found: {0}x{1}@{2} {3}",
                            videoProfile.GetWidth(),
                            videoProfile.GetHeight(),
                            videoProfile.GetFPS(),
                            videoProfile.GetFormat());
                        streamProfile = null;
                        return videoProfile;
                    }
                    finally
                    {
                        streamProfile?.Dispose();
                    }
                }

                Debug.LogWarning("Profile not found");
            }
            catch (NativeException e)
            {
                Debug.Log(e.Message);
            }
            finally
            {
                profileList?.Dispose();
            }

            return null;
        }

        public void SetFrameCallback(FrameCallback callback)
        {
            frameCallback = callback;
        }

        public void StartStream()
        {
            sensor.Start(streamProfile, frameCallback);
        }
        
        public void StopStream()
        {
            sensor.Stop();
        }

        private void InitSensor(Device device)
        {
            sensor = device.GetSensor(sensorType);
            if(sensor == null)
            {
                Debug.LogError("Sensor not found: " + sensorType);
                return;
            }

            for (int i = 0; i < orbbecProfiles.Length; i++)
            {
                streamProfile = FindProfile(orbbecProfiles[i]);
                if (streamProfile != null)
                {
                    break;
                }
            }
            onSensorInit?.Invoke();
        }
    }
}