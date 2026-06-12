using Orbbec;
using UnityEngine;

namespace OrbbecUnity
{
    public class OrbbecPlayback : MonoBehaviour
    {
        private Pipeline pipeline;
        private PlaybackDevice playbackDevice;
        private FramesetCallback framesetCallback;

        public void SetFramesetCallback(FramesetCallback callback)
        {
            framesetCallback = callback;
        }

        public void StartPlayback(string playbackPath)
        {
            playbackDevice = new PlaybackDevice(playbackPath);
            pipeline = new Pipeline(playbackDevice);
            pipeline.Start(null, framesetCallback);
        }

        public void StopPlayback()
        {
            if (pipeline != null)
            {
                pipeline.Stop();
                pipeline.Dispose();
                pipeline = null;
            }

            if (playbackDevice != null)
            {
                playbackDevice.Dispose();
                playbackDevice = null;
            }
        }

        void OnDestroy()
        {
            StopPlayback();
        }
    }
}
