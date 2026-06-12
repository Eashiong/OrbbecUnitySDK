
using System;
using UnityEngine;
using UnityEngine.Android;

namespace OrbbecUnity
{
public class AndroidDeviceManager
{
    private static AndroidJavaClass UsbPermissionUtil;
    private static bool usbDeviceStarted;

    public static void Init(Action onReady)
    {
        Debug.Log("init android device");
        if (Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            StartUsbDevice(onReady);
            return;
        }

        var callbacks = new PermissionCallbacks();
        callbacks.PermissionGranted += _ => StartUsbDevice(onReady);
        callbacks.PermissionDenied += _ => Debug.LogError("需要相机权限才能访问 Orbbec USB 设备");
        callbacks.PermissionDeniedAndDontAskAgain += _ => Debug.LogError("需要相机权限才能访问 Orbbec USB 设备，请在系统设置中手动开启");
        Permission.RequestUserPermission(Permission.Camera, callbacks);
    }

    private static void StartUsbDevice(Action onReady)
    {
        // Context 必须先创建，waitForUsbDevice 内部才会注册 USB DeviceWatcher
        onReady?.Invoke();

        if (usbDeviceStarted)
        {
            return;
        }

        AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
        AndroidJavaObject currentActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
        UsbPermissionUtil = new AndroidJavaClass("com.orbbec.obsensor.usbdevice.UsbPermissionUtil");
        UsbPermissionUtil.CallStatic("waitForUsbDevice", currentActivity);
        usbDeviceStarted = true;
        Debug.Log("android device has init");
    }

    public static void Close()
    {
        if (UsbPermissionUtil == null)
        {
            return;
        }

        Debug.Log("close android device");
        UsbPermissionUtil.CallStatic("closeUsbDevice");
        UsbPermissionUtil = null;
        usbDeviceStarted = false;
    }
}
}
