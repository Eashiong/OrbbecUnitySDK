using System.Collections;
using Orbbec;
using UnityEngine;
using UnityEngine.Events;

namespace OrbbecUnity
{
    [System.Serializable]
    public class DeviceFoundEvent : UnityEvent<Device> {}

    public class OrbbecDevice : MonoBehaviour
    {
        public int deviceIndex;
        public DeviceFoundEvent onDeviceFound;

        private Context context;
        private Device device;

        public Device Device
        {
            get
            {
                return device;
            }
        }

        void Start()
        {
            StartCoroutine(StartWhenReady());
        }

        private IEnumerator StartWhenReady()
        {
            yield return OrbbecContext.Instance.WaitUntilInitialized();
            context = OrbbecContext.Instance.Context;
            yield return WaitForDevice();
        }

        void OnDestroy()
        {
            if(device != null)
            {
                device.Dispose();
            }
        }

        private IEnumerator WaitForDevice()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                OrbbecContext.TryEnableNetDeviceEnumeration(context, true);
                DeviceList deviceList = context.QueryDeviceList();
                if (deviceList.DeviceCount() > deviceIndex)
                {
                    device = deviceList.GetDevice((uint)deviceIndex);
                    DeviceInfo deviceInfo = device.GetDeviceInfo();
                    Debug.LogFormat(
                        "Device found: {0} {1} {2:X} {3:X}", 
                        deviceInfo.Name(), 
                        deviceInfo.SerialNumber(),
                        deviceInfo.Vid(),
                        deviceInfo.Pid());
                    deviceList.Dispose();
                    onDeviceFound?.Invoke(device);
                    break;
                }
                else
                {
                    deviceList.Dispose();
                }
            }
        }
    }
}