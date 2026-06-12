using Orbbec;
using UnityEngine;

namespace OrbbecUnity
{
    public class OrbbecRecord : MonoBehaviour
    {
        public OrbbecPipeline pipeline;

        private RecordDevice recordDevice;

        public void StartRecord(string recordPath)
        {
            if (!pipeline.HasInit)
            {
                return;
            }

            var device = pipeline.Pipeline.GetDevice();
            recordDevice = new RecordDevice(device, recordPath);
            recordDevice.Resume();
        }

        public void StopRecord()
        {
            if (recordDevice == null)
            {
                return;
            }

            recordDevice.Pause();
            recordDevice.Dispose();
            recordDevice = null;
        }

        void OnDestroy()
        {
            StopRecord();
        }
    }
}
