#nullable disable
#pragma warning disable
using System;

namespace emu2026
{
    public static class AIMath
    {
        public struct DetectionResult
        {
            public float x, y, w, h;
            public float confidence;
            public int classId;
            public float centerDistSq;
            public int detectionIndex;
        }

        // Multi-component target scoring (portado do Aimmy2 MathUtil.cs)
        // Combina: distancia + confianca + tamanho + bonus de lock
        public static double CalculateTargetScore(float detX, float detY, float detW, float detH,
            float confidence, bool isCurrentTarget, double currentLockScore)
        {
            float distSq = detX * detX + detY * detY;
            float normDist = 1.0f - Math.Min(1.0f, distSq / 90000.0f);
            double distanceScore = normDist * normDist;

            double confidenceBonus = (confidence - 0.3) * 1.0;
            if (confidenceBonus < 0) confidenceBonus = 0;
            confidenceBonus = Math.Min(confidenceBonus, 0.3);

            float area = detW * detH;
            double sizeBonus = Math.Max(0, Math.Min(area / 10000.0, 1.0)) * 0.2;

            double lockBonus = isCurrentTarget ? (Math.Min(currentLockScore, 100.0) / 100.0) * 0.5 : 0.0;

            return distanceScore * 1.0 + confidenceBonus + sizeBonus + lockBonus;
        }
    }

    public class KalmanFilter
    {
        public float x, y, vx, vy;
        public float px, py, pvx, pvy;
        private float processNoise = 0.1f;
        private float measurementNoise = 0.5f;
        private float maxVelocity = 5000.0f;
        private float lastTime = 0;

        public KalmanFilter()
        {
            x = 0; y = 0; vx = 0; vy = 0;
            px = py = pvx = pvy = 1.0f;
        }

        public void Reset(float initX, float initY)
        {
            x = initX; y = initY;
            vx = 0; vy = 0;
            px = py = pvx = pvy = 1.0f;
            lastTime = 0;
        }

        public void Predict(float dt)
        {
            if (dt <= 0) dt = 0.016f;
            x += vx * dt;
            y += vy * dt;
            px += pvx + processNoise * dt * dt;
            py += pvy + processNoise * dt * dt;
            pvx += processNoise * dt;
            pvy += processNoise * dt;
        }

        public void Update(float measX, float measY)
        {
            float kx = px / (px + measurementNoise);
            float ky = py / (py + measurementNoise);

            x += kx * (measX - x);
            y += ky * (measY - y);
            px *= (1.0f - kx);
            py *= (1.0f - ky);

            float dt = Environment.TickCount - lastTime;
            if (dt > 0 && dt < 1000)
            {
                float sDt = dt / 1000.0f;
                if (sDt > 0.001f)
                {
                    vx = (measX - (x - kx * (measX - x))) / sDt;
                    vy = (measY - (y - ky * (measY - y))) / sDt;
                    float nv = Math.Max(0, pvx - kx * vx * sDt);
                    pvx = Math.Min(nv, maxVelocity);
                    float nv2 = Math.Max(0, pvy - ky * vy * sDt);
                    pvy = Math.Min(nv2, maxVelocity);
                }
            }

            vx = Math.Max(-maxVelocity, Math.Min(maxVelocity, vx));
            vy = Math.Max(-maxVelocity, Math.Min(maxVelocity, vy));
        }

        public float PredictY(float leadTime)
        {
            return y + vy * leadTime;
        }

        public float PredictX(float leadTime)
        {
            return x + vx * leadTime;
        }

        public float GetVelX() { return vx; }
        public float GetVelY() { return vy; }
    }
}
