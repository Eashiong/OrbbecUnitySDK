using System.Collections;
using System.IO;
using Orbbec;
using UnityEngine;

namespace OrbbecUnity
{
    public class OrbbecContext : MonoBehaviour
    {
        private static OrbbecContext instance;
        private bool hasInit;
        private Context context;

        public static OrbbecContext Instance
        {
            get
            {
                if(instance == null)
                {
                    instance = FindObjectOfType<OrbbecContext>();
 
                    if (instance == null)
                    {
                        var singletonObject = new GameObject();
                        instance = singletonObject.AddComponent<OrbbecContext>();
                        singletonObject.name = typeof(OrbbecContext).ToString();
                        DontDestroyOnLoad(singletonObject);
                    }
                }

                return instance;
            }
        }

        public bool HasInit
        {
            get
            {
                return hasInit;
            }
        }

        public Context Context
        {
            get
            {
                return context;
            }
        }

        void Awake()
        {
            if (!hasInit)
            {
                InitSDK();
            }
        }

        void OnDestroy()
        {
            if(hasInit && context != null)
            {
                context.Dispose();
            }
#if !UNITY_EDITOR && UNITY_ANDROID
            AndroidDeviceManager.Close();
#endif
            hasInit = false;
        }

        public IEnumerator WaitUntilInitialized()
        {
            while (!hasInit)
            {
                yield return null;
            }
        }

        private void InitSDK()
        {
            Debug.LogFormat("Orbbec SDK version: {0}.{1}.{2}",
                                        Version.GetMajorVersion(),
                                        Version.GetMinorVersion(),
                                        Version.GetPatchVersion());
#if !UNITY_EDITOR && UNITY_ANDROID
            AndroidDeviceManager.Init(CompleteInit);
#else
            CompleteInit();
#endif
        }

        private void CompleteInit()
        {
            if (hasInit)
            {
                return;
            }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            SetupWindowsNativePaths();
#endif
            context = new Context();
            hasInit = true;
        }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        private static void SetupWindowsNativePaths()
        {
            var pluginDir = Path.Combine(Application.dataPath, "Orbbec", "Plugins", "x86_64");
            var extensionsDir = Path.Combine(pluginDir, "extensions");
            if (Directory.Exists(extensionsDir))
            {
                Context.SetExtensionsDirectory(extensionsDir);
            }
            else
            {
                Debug.LogWarning($"Orbbec extensions directory not found: {extensionsDir}");
            }
        }
#endif

        public static void TryEnableNetDeviceEnumeration(Context ctx, bool enable)
        {
#if !UNITY_EDITOR && UNITY_ANDROID
            try
            {
                ctx.EnableNetDeviceEnumeration(enable);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"EnableNetDeviceEnumeration skipped on Android: {e.Message}");
            }
#else
            ctx.EnableNetDeviceEnumeration(enable);
#endif
        }
    }
}
