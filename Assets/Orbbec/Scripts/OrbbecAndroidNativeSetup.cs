using System;
using System.IO;
using Orbbec;
using UnityEngine;
using UnityEngine.Networking;

namespace OrbbecUnity
{
    /// <summary>
    /// Prepares v2 native paths on Android before Context creation.
    /// Mirrors OrbbecSDK-Android-Wrapper OBContext.initExtensions().
    /// </summary>
    public static class OrbbecAndroidNativeSetup
    {
        public static void LoadNativeLibraries()
        {
#if !UNITY_EDITOR && UNITY_ANDROID
            using var system = new AndroidJavaClass("java.lang.System");
            system.CallStatic("loadLibrary", "c++_shared");
            system.CallStatic("loadLibrary", "omp");
            system.CallStatic("loadLibrary", "OrbbecSDK");
            system.CallStatic("loadLibrary", "obsensor_jni");
            Debug.Log("Orbbec Android native libraries loaded.");
#endif
        }

        public static void PrepareNativePaths()
        {
#if !UNITY_EDITOR && UNITY_ANDROID
            var extensionsDir = GetInternalExtensionsDirectory();
            EnsureExtensionsExtracted(extensionsDir);
            Context.SetExtensionsDirectory(extensionsDir);
            PreloadExtensionLibraries(extensionsDir);
            LogExtensionsStatus(extensionsDir);
            Debug.Log($"Orbbec Android extensions directory: {extensionsDir}");
#endif
        }

#if !UNITY_EDITOR && UNITY_ANDROID
        private static string GetInternalExtensionsDirectory()
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            using var filesDir = activity.Call<AndroidJavaObject>("getFilesDir");
            var filesPath = filesDir.Call<string>("getAbsolutePath");
            return Path.Combine(filesPath, "extensions");
        }

        private const long MinExtensionLibraryBytes = 4096;

        // depthengine 在官方 Android 包中可为空占位，深度依赖 frameprocessor
        private static readonly string[] RequiredExtensionLibraries =
        {
            Path.Combine("filters", "libFilterProcessor.so"),
            Path.Combine("frameprocessor", "libob_frame_processor.so"),
        };

        private static readonly string[] PreloadExtensionLibraryPaths =
        {
            Path.Combine("frameprocessor", "libob_frame_processor.so"),
            Path.Combine("filters", "libFilterProcessor.so"),
            Path.Combine("filters", "libob_priv_filter.so"),
        };

        private static bool HasValidElfHeader(string fullPath)
        {
            try
            {
                using var fs = File.OpenRead(fullPath);
                if (fs.Length < 4)
                {
                    return false;
                }

                var header = new byte[4];
                if (fs.Read(header, 0, 4) != 4)
                {
                    return false;
                }

                return header[0] == 0x7F
                    && header[1] == (byte)'E'
                    && header[2] == (byte)'L'
                    && header[3] == (byte)'F';
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Failed to read ELF header for {fullPath}: {e.Message}");
                return false;
            }
        }

        private static bool IsValidExtensionFile(string fullPath)
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            if (new FileInfo(fullPath).Length < MinExtensionLibraryBytes)
            {
                return false;
            }

            return HasValidElfHeader(fullPath);
        }

        private static bool IsExtensionsReady(string extensionsDir)
        {
            foreach (var relativePath in RequiredExtensionLibraries)
            {
                if (!IsValidExtensionFile(Path.Combine(extensionsDir, relativePath)))
                {
                    return false;
                }
            }

            return true;
        }

        private static void PreloadExtensionLibraries(string extensionsDir)
        {
            using var system = new AndroidJavaClass("java.lang.System");
            foreach (var relativePath in PreloadExtensionLibraryPaths)
            {
                var fullPath = Path.Combine(extensionsDir, relativePath);
                if (!IsValidExtensionFile(fullPath))
                {
                    Debug.LogWarning($"Skip preload, extension missing or invalid: {relativePath}");
                    continue;
                }

                try
                {
                    system.CallStatic("load", fullPath);
                    Debug.Log($"Preloaded Orbbec extension library: {relativePath}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to preload {relativePath}: {e.Message}");
                }
            }
        }

        private static void LogExtensionsStatus(string extensionsDir)
        {
            foreach (var relativePath in RequiredExtensionLibraries)
            {
                var fullPath = Path.Combine(extensionsDir, relativePath);
                if (!File.Exists(fullPath))
                {
                    Debug.LogError($"Orbbec extension missing: {relativePath}");
                    continue;
                }

                var size = new FileInfo(fullPath).Length;
                if (!IsValidExtensionFile(fullPath))
                {
                    Debug.LogError($"Orbbec extension invalid (size={size}, elf={HasValidElfHeader(fullPath)}): {relativePath}");
                }
                else
                {
                    Debug.Log($"Orbbec extension ready: {relativePath} ({size} bytes, ELF ok)");
                }
            }
        }

        private static void EnsureExtensionsExtracted(string extensionsDir)
        {
            if (!Directory.Exists(extensionsDir))
            {
                Directory.CreateDirectory(extensionsDir);
            }

            if (IsExtensionsReady(extensionsDir))
            {
                Debug.Log("Orbbec Android extensions already extracted, skipping.");
                return;
            }

            Debug.LogWarning("Orbbec Android extensions incomplete or corrupt, re-extracting from StreamingAssets.");
            ClearExtensionsDirectory(extensionsDir);

            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            using var assetManager = activity.Call<AndroidJavaObject>("getAssets");
            using var buildClass = new AndroidJavaClass("android.os.Build");
            var supportedAbis = buildClass.GetStatic<string[]>("SUPPORTED_ABIS");
            var abi = supportedAbis != null && supportedAbis.Length > 0
                ? supportedAbis[0]
                : "arm64-v8a";

            ExtractAssetDirectory(assetManager, $"{abi}/extensions", extensionsDir);

            if (!IsExtensionsReady(extensionsDir))
            {
                Debug.LogError(
                    "Orbbec Android extensions are still incomplete after extraction. " +
                    "Run OrbbecUnitySDK/Scripts/sync-android-v2-native.ps1 and rebuild the APK. " +
                    "Depth stream requires frameprocessor/libob_frame_processor.so.");
            }
        }

        private static void ClearExtensionsDirectory(string extensionsDir)
        {
            if (!Directory.Exists(extensionsDir))
            {
                return;
            }

            foreach (var entry in Directory.GetFileSystemEntries(extensionsDir))
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
        }

        private static void ExtractAssetDirectory(
            AndroidJavaObject assetManager,
            string assetParentDir,
            string targetDir)
        {
            var entries = assetManager.Call<string[]>("list", assetParentDir);
            if (entries == null)
            {
                Debug.LogWarning($"Orbbec Android asset path not found: {assetParentDir}");
                return;
            }

            foreach (var entry in entries)
            {
                if (entry == ".gitkeep")
                {
                    continue;
                }

                var assetPath = $"{assetParentDir}/{entry}";
                var outPath = Path.Combine(targetDir, entry);
                if (IsAssetDirectory(assetManager, assetPath))
                {
                    Directory.CreateDirectory(outPath);
                    ExtractAssetDirectory(assetManager, assetPath, outPath);
                }
                else
                {
                    CopyAssetFile(assetPath, outPath);
                }
            }
        }

        private static bool IsAssetDirectory(AndroidJavaObject assetManager, string assetPath)
        {
            var entries = assetManager.Call<string[]>("list", assetPath);
            return entries != null && entries.Length > 0;
        }

        /// <summary>
        /// Unity JNI 无法正确把 Java InputStream.read(byte[]) 写入 C# 缓冲，会得到全 0 文件。
        /// 必须用 UnityWebRequest 从 StreamingAssets（jar:file APK）读取真实内容。
        /// </summary>
        private static void CopyAssetFile(string assetPath, string outPath)
        {
            var outDirectory = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDirectory) && !Directory.Exists(outDirectory))
            {
                Directory.CreateDirectory(outDirectory);
            }

            var url = $"{Application.streamingAssetsPath}/{assetPath}".Replace('\\', '/');
            using var request = UnityWebRequest.Get(url);
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Failed to extract asset {assetPath}: {request.error}");
                return;
            }

            var data = request.downloadHandler.data;
            if (data == null || data.Length == 0)
            {
                Debug.LogError($"Extracted asset is empty: {assetPath}");
                return;
            }

            File.WriteAllBytes(outPath, data);
        }
#endif
    }
}
