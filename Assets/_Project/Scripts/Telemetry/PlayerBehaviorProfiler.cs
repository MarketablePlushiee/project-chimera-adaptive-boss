using System.Collections.Generic;
using UnityEngine;
using Chimera.Core;
using Chimera.Player;
using Chimera.Boss;

namespace Chimera.Telemetry
{
    // Capa 1 de Project Chimera: Perfilador Comportamental en Ventana Deslizante (12s).
    // Extrae 6 métricas normalizadas del estilo de juego para alimentar a la red neuronal PPO (Capa 2).
    public class PlayerBehaviorProfiler : MonoBehaviour
    {
        [Header("Referencias de Entidades")]
        [SerializeField] private PlayerController playerController;
        [SerializeField] private HealthComponent playerHealth;
        [SerializeField] private BossCombatFSM bossFSM;

        [Header("Parámetros de Ventana Deslizante")]
        [Tooltip("Duración en segundos de la memoria reciente del comportamiento del jugador.")]
        [SerializeField] private float windowDuration = 12.0f;
        [SerializeField] private float sampleInterval = 0.10f;
        [SerializeField] private float arenaDiameter = 20.0f;
        [SerializeField] private float expectedMaxDashesPerWindow = 6.0f;

        [Header("Visualización en Pantalla (Para Debug y Behance)")]
        [SerializeField] private bool showTelemetryOverlay = true;

        private struct TimedScalarSample
        {
            public float timestamp;
            public float value;
        }

        private struct ParryRecord
        {
            public float timestamp;
            public bool wasPerfect;
        }

        private readonly Queue<float> meleeTimestamps = new Queue<float>();
        private readonly Queue<float> rangedTimestamps = new Queue<float>();
        private readonly Queue<float> dashTimestamps = new Queue<float>();
        private readonly Queue<ParryRecord> parryRecords = new Queue<ParryRecord>();
        private readonly Queue<TimedScalarSample> lateralBiasSamples = new Queue<TimedScalarSample>();
        private readonly Queue<TimedScalarSample> distanceSamples = new Queue<TimedScalarSample>();

        private float nextSampleTime;

        // 6 Métricas Normalizadas (Vector de Telemetría para Capa 2)
        public float MeleeAggressionRatio { get; private set; }      // [0, 1]
        public float RangedPressureRatio { get; private set; }       // [0, 1]
        public float DashFrequencyNorm { get; private set; }         // [0, 1]
        public float DodgeLateralBias { get; private set; }          // [-1 (Izq Jefe), +1 (Der Jefe)]
        public float ParrySuccessRate { get; private set; }          // [0, 1]
        public float NormalizedAverageDistance { get; private set; } // [0, 1]

        private void Awake()
        {
            if (playerController != null)
            {
                playerController.OnPlayerMeleeAttack += HandleMeleeAttack;
                playerController.OnPlayerRangedAttack += HandleRangedAttack;
                playerController.OnPlayerDash += HandlePlayerDash;
                playerController.OnPlayerParryAttempt += HandleParryAttempt;
            }

            ResetProfiler();
        }

        private void OnDestroy()
        {
            if (playerController != null)
            {
                playerController.OnPlayerMeleeAttack -= HandleMeleeAttack;
                playerController.OnPlayerRangedAttack -= HandleRangedAttack;
                playerController.OnPlayerDash -= HandlePlayerDash;
                playerController.OnPlayerParryAttempt -= HandleParryAttempt;
            }
        }

        private void Update()
        {
            if (playerController == null || bossFSM == null) return;

            if (Time.time >= nextSampleTime)
            {
                nextSampleTime = Time.time + sampleInterval;
                SampleSpatialMetrics();
                PruneExpiredEntries(Time.time - windowDuration);
                RecomputeMetrics();

                // Sincroniza el sesgo lateral detectado con el sistema predictivo de Capa 3
                bossFSM.DodgeLateralBias = DodgeLateralBias;
            }
        }

        private void HandleMeleeAttack()
        {
            meleeTimestamps.Enqueue(Time.time);
        }

        private void HandleRangedAttack()
        {
            rangedTimestamps.Enqueue(Time.time);
        }

        private void HandlePlayerDash(Vector3 dashDir)
        {
            float now = Time.time;
            dashTimestamps.Enqueue(now);

            if (bossFSM != null && playerController != null)
            {
                Vector3 bossToPlayer = FlatPos(playerController.transform.position) - FlatPos(bossFSM.transform.position);
                if (bossToPlayer.sqrMagnitude > 0.01f)
                {
                    Vector3 bossRight = Vector3.Cross(Vector3.up, bossToPlayer.normalized).normalized;
                    float lateralProjection = Vector3.Dot(dashDir.normalized, bossRight);
                    // Los dashes laterales tienen peso alto (2.5x) en el sesgo de esquive
                    lateralBiasSamples.Enqueue(new TimedScalarSample
                    {
                        timestamp = now,
                        value = Mathf.Clamp(lateralProjection * 2.5f, -1.5f, 1.5f)
                    });
                }
            }
        }

        private void HandleParryAttempt(bool wasPerfectSuccess)
        {
            parryRecords.Enqueue(new ParryRecord
            {
                timestamp = Time.time,
                wasPerfect = wasPerfectSuccess
            });
        }

        private void SampleSpatialMetrics()
        {
            float now = Time.time;
            Vector3 pPos = FlatPos(playerController.transform.position);
            Vector3 bPos = FlatPos(bossFSM.transform.position);
            Vector3 bossToPlayer = pPos - bPos;
            float dist = bossToPlayer.magnitude;

            distanceSamples.Enqueue(new TimedScalarSample
            {
                timestamp = now,
                value = Mathf.Clamp01(dist / Mathf.Max(1f, arenaDiameter))
            });

            // Muestrea desplazamiento lateral continuo (strafing), con mayor peso si el Jefe está telegrafiando
            if (dist > 0.1f && playerController.Velocity.sqrMagnitude > 0.2f)
            {
                Vector3 bossRight = Vector3.Cross(Vector3.up, bossToPlayer.normalized).normalized;
                float moveSpeedRef = 7.5f;
                float lateralNorm = Vector3.Dot(playerController.Velocity / moveSpeedRef, bossRight);
                float contextWeight = bossFSM.CurrentState == BossFSMState.Telegraph ? 1.5f : 0.6f;

                lateralBiasSamples.Enqueue(new TimedScalarSample
                {
                    timestamp = now,
                    value = Mathf.Clamp(lateralNorm * contextWeight, -1f, 1f)
                });
            }
        }

        private void PruneExpiredEntries(float cutoffTime)
        {
            while (meleeTimestamps.Count > 0 && meleeTimestamps.Peek() < cutoffTime)
                meleeTimestamps.Dequeue();

            while (rangedTimestamps.Count > 0 && rangedTimestamps.Peek() < cutoffTime)
                rangedTimestamps.Dequeue();

            while (dashTimestamps.Count > 0 && dashTimestamps.Peek() < cutoffTime)
                dashTimestamps.Dequeue();

            while (parryRecords.Count > 0 && parryRecords.Peek().timestamp < cutoffTime)
                parryRecords.Dequeue();

            while (lateralBiasSamples.Count > 0 && lateralBiasSamples.Peek().timestamp < cutoffTime)
                lateralBiasSamples.Dequeue();

            while (distanceSamples.Count > 0 && distanceSamples.Peek().timestamp < cutoffTime)
                distanceSamples.Dequeue();
        }

        private void RecomputeMetrics()
        {
            int meleeCount = meleeTimestamps.Count;
            int rangedCount = rangedTimestamps.Count;
            int totalAttacks = meleeCount + rangedCount;

            if (totalAttacks > 0)
            {
                float attackActivity = Mathf.Clamp01(totalAttacks / 8.0f);
                MeleeAggressionRatio = ((float)meleeCount / totalAttacks) * Mathf.Lerp(0.4f, 1f, attackActivity);
                RangedPressureRatio = ((float)rangedCount / totalAttacks) * Mathf.Lerp(0.4f, 1f, attackActivity);
            }
            else
            {
                MeleeAggressionRatio = 0f;
                RangedPressureRatio = 0f;
            }

            DashFrequencyNorm = Mathf.Clamp01(dashTimestamps.Count / Mathf.Max(1f, expectedMaxDashesPerWindow));

            if (lateralBiasSamples.Count > 0)
            {
                float sumBias = 0f;
                foreach (var s in lateralBiasSamples)
                    sumBias += s.value;
                DodgeLateralBias = Mathf.Clamp(sumBias / lateralBiasSamples.Count, -1f, 1f);
            }
            else
            {
                DodgeLateralBias = 0f;
            }

            if (parryRecords.Count > 0)
            {
                int attempts = 0;
                int successes = 0;
                foreach (var p in parryRecords)
                {
                    if (p.wasPerfect) successes++;
                    else attempts++;
                }
                int denom = Mathf.Max(1, attempts);
                ParrySuccessRate = Mathf.Clamp01((float)successes / denom);
            }
            else
            {
                ParrySuccessRate = 0.25f; // Prior neutro al inicio del episodio
            }

            if (distanceSamples.Count > 0)
            {
                float sumDist = 0f;
                foreach (var d in distanceSamples)
                    sumDist += d.value;
                NormalizedAverageDistance = Mathf.Clamp01(sumDist / distanceSamples.Count);
            }
            else
            {
                NormalizedAverageDistance = 0.5f;
            }
        }

        // Copia el vector de 6 métricas directamente al buffer de observaciones de ML-Agents
        public void WriteTelemetryToBuffer(float[] buffer, int startIndex)
        {
            buffer[startIndex + 0] = MeleeAggressionRatio;
            buffer[startIndex + 1] = RangedPressureRatio;
            buffer[startIndex + 2] = DashFrequencyNorm;
            buffer[startIndex + 3] = DodgeLateralBias;
            buffer[startIndex + 4] = ParrySuccessRate;
            buffer[startIndex + 5] = NormalizedAverageDistance;
        }

        public void ResetProfiler()
        {
            meleeTimestamps.Clear();
            rangedTimestamps.Clear();
            dashTimestamps.Clear();
            parryRecords.Clear();
            lateralBiasSamples.Clear();
            distanceSamples.Clear();

            MeleeAggressionRatio = 0f;
            RangedPressureRatio = 0f;
            DashFrequencyNorm = 0f;
            DodgeLateralBias = 0f;
            ParrySuccessRate = 0.25f;
            NormalizedAverageDistance = 0.5f;
            nextSampleTime = Time.time;

            if (bossFSM != null)
            {
                bossFSM.DodgeLateralBias = 0f;
            }
        }

        private Vector3 FlatPos(Vector3 v) => new Vector3(v.x, 0f, v.z);

        // Panel HUD ligero en esquina superior izquierda para visualizar la Capa 1 en tiempo real
        private void OnGUI()
        {
            if (!showTelemetryOverlay) return;

            GUI.Box(new Rect(14, 14, 290, 175), "CAPA 1: TELEMETRÍA JUGADOR (12s)");

            DrawMetricBar(24, 42, "Agresión Melee", MeleeAggressionRatio, new Color(0.2f, 0.9f, 0.4f));
            DrawMetricBar(24, 64, "Presión Ranged", RangedPressureRatio, new Color(0.2f, 0.8f, 1f));
            DrawMetricBar(24, 86, "Frecuencia Dash", DashFrequencyNorm, new Color(1f, 0.8f, 0.2f));
            DrawSignedBar(24, 108, "Sesgo Lateral", DodgeLateralBias);
            DrawMetricBar(24, 130, "Éxito Parry", ParrySuccessRate, new Color(1f, 0.55f, 0.15f));
            DrawMetricBar(24, 152, "Distancia Media", NormalizedAverageDistance, new Color(0.75f, 0.55f, 1f));
        }

        private void DrawMetricBar(float x, float y, string label, float value01, Color barColor)
        {
            GUI.Label(new Rect(x, y, 125, 20), $"{label}: {value01:F2}");
            GUI.Box(new Rect(x + 130, y + 3, 135, 14), GUIContent.none);

            Color prev = GUI.color;
            GUI.color = barColor;
            GUI.DrawTexture(new Rect(x + 132, y + 5, 131f * Mathf.Clamp01(value01), 10), Texture2D.whiteTexture);
            GUI.color = prev;
        }

        private void DrawSignedBar(float x, float y, string label, float valueSigned)
        {
            GUI.Label(new Rect(x, y, 125, 20), $"{label}: {valueSigned:+0.00;-0.00}");
            GUI.Box(new Rect(x + 130, y + 3, 135, 14), GUIContent.none);

            float midX = x + 130 + 67.5f;
            float width = 65f * Mathf.Clamp(valueSigned, -1f, 1f);

            Color prev = GUI.color;
            GUI.color = valueSigned >= 0f ? new Color(0.2f, 0.95f, 0.85f) : new Color(1f, 0.4f, 0.6f);
            if (width >= 0f)
                GUI.DrawTexture(new Rect(midX, y + 5, width, 10), Texture2D.whiteTexture);
            else
                GUI.DrawTexture(new Rect(midX + width, y + 5, -width, 10), Texture2D.whiteTexture);
            GUI.color = prev;
        }
    }
}