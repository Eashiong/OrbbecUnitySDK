using System.Collections;
using Orbbec;
using UnityEngine;

namespace OrbbecUnity
{
    [CreateAssetMenu(menuName = "OrbbecProfile")]
    public class OrbbecProfile : ScriptableObject
    {
        public SensorType sensorType;
        public int width;
        public int height;
        public int fps;
        public Format format;

        public Format GetNormalizedFormat()
        {
            int formatValue = (int)format;
            if (formatValue < 0 || formatValue == 255 || !System.Enum.IsDefined(typeof(Format), format))
            {
                return Format.OB_FORMAT_ANY;
            }
            return format;
        }
    }
}