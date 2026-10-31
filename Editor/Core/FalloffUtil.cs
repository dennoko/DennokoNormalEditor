using UnityEngine;

namespace Dennokoworks.DenNormalEditor.Editor
{
    internal static class FalloffUtil
    {
        /// <summary>
        /// 正規化距離 t（0 = 中心, 1 = 影響半径の端）に対する重みを返す。
        /// </summary>
        internal static float Weight(float t, FalloffType type)
        {
            if (t >= 1f) return 0f;
            if (t <= 0f) return 1f;

            var s = 1f - t;

            switch (type)
            {
                case FalloffType.Linear:
                    return s;
                case FalloffType.Sharp:
                    return s * s;
                case FalloffType.Constant:
                    return 1f;
                case FalloffType.Smooth:
                default:
                    return s * s * (3f - 2f * s);
            }
        }
    }
}

