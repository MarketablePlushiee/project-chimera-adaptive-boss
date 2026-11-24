using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using Chimera.Core;
using Chimera.Player;
using Chimera.Boss;
using Chimera.Telemetry;

namespace Chimera.AI
{
    [RequireComponent(typeof(BossCombatFSM))]
    [RequireComponent(typeof(HealthComponent))]
    public class ChimeraBossAgent : Agent
    {
        [Header("Referencias de la Arena")]
        [SerializeField] private PlayerController playerController;
        [SerializeField] private HealthComponent playerHealth;
        [SerializeField] private TrainingBotController trainingBot;
        [SerializeField] private PlayerBehaviorProfiler behaviorProfiler;

        [Header("Posiciones Iniciales Locales (Respecto a BossArena_Unit)")]
        [SerializeField] private Vector3 bossSpawnLocalPos = new Vector3(0f, 0f, 6f);
        [SerializeField] private Vector3 playerSpawnLocalPos = new Vector3(0f, 0f, -6f);

        [Header("Parámetros de Normalización")]
        [SerializeField] private float arenaDiameter = 20f;
        [SerializeField] private float arenaMaxRadius = 11f;
        [SerializeField] private float playerMaxSpeed = 7.5f;

        private BossCombatFSM bossFSM;
        private HealthComponent bossHealth;

        private readonly float[] telemetryBuffer = new float[6];
        private readonly Collider[] threatBuffer = new Collider[12];
        private float prevBossHP;
        private float prevPlayerHP;
        private float lastDashRewardTime;
        private bool episodeTerminated;

        public override void Initialize()
        {
            bossFSM = GetComponent<BossCombatFSM>();
            bossHealth = GetComponent<HealthComponent>();

            bossFSM.AutonomousTestMode = false;

            bossHealth.OnPostureBroken += HandleBossStaggeredReward;
            bossFSM.OnAttackStarted += HandleAttackStartedReward;

            if (playerHealth != null)
            {
                playerHealth.OnPostureBroken += HandlePlayerStaggeredReward;
                playerHealth.OnParriedSuccess += HandlePlayerParriedReward;
            }
        }

        private void OnDestroy()
        {
            if (bossHealth != null)
                bossHealth.OnPostureBroken -= HandleBossStaggeredReward;

            if (bossFSM != null)
                bossFSM.OnAttackStarted -= HandleAttackStartedReward;

            if (playerHealth != null)
            {
                playerHealth.OnPostureBroken -= HandlePlayerStaggeredReward;
                playerHealth.OnParriedSuccess -= HandlePlayerParriedReward;
            }
        }

        public override void OnEpisodeBegin()
        {
            episodeTerminated = false;
            lastDashRewardTime = -10f;

            if (behaviorProfiler != null)
                behaviorProfiler.ResetProfiler();

            bossFSM.ResetBossState(bossSpawnLocalPos);

            if (playerController != null)
                playerController.ResetPlayerState(playerSpawnLocalPos);

            if (playerController != null && playerController.IsBotControlled && trainingBot != null)
                trainingBot.ResetBot();

            prevBossHP = bossHealth.CurrentHP;
            prevPlayerHP = playerHealth != null ? playerHealth.CurrentHP : 100f;
        }

        public override void CollectObservations(VectorSensor sensor)
        {
            // 1-2. Vida y Postura normalizadas del Jefe [2]
            sensor.AddObservation(bossHealth.NormalizedHP);
            sensor.AddObservation(bossHealth.NormalizedPosture);

            // 3. Indicador de Fase 2 [1]
            sensor.AddObservation(bossFSM.IsPhase2 ? 1f : 0f);

            // 4-8. Estado actual de la FSM en One-Hot [5]
            BossFSMState state = bossFSM.CurrentState;
            sensor.AddObservation(state == BossFSMState.Locomotion ? 1f : 0f);
            sensor.AddObservation(state == BossFSMState.Telegraph ? 1f : 0f);
            sensor.AddObservation(state == BossFSMState.ActiveHitbox ? 1f : 0f);
            sensor.AddObservation(state == BossFSMState.Recovery ? 1f : 0f);
            sensor.AddObservation(state == BossFSMState.Staggered ? 1f : 0f);

            if (playerController == null || playerHealth == null)
            {
                for (int i = 0; i < 16; i++) sensor.AddObservation(0f);
                return;
            }

            // 9-12. Estado vital y defensivo del Jugador [4]
            sensor.AddObservation(playerHealth.NormalizedHP);
            sensor.AddObservation(playerHealth.NormalizedPosture);
            sensor.AddObservation(playerController.IsDashing ? 1f : 0f);
            sensor.AddObservation(playerController.IsGuarding ? 1f : 0f);

            // 13-15. Distancia y dirección relativa hacia el Jugador [3]
            Vector3 toPlayerWorld = FlatPos(playerController.transform.position) - FlatPos(transform.position);
            float dist = toPlayerWorld.magnitude;
            sensor.AddObservation(Mathf.Clamp01(dist / Mathf.Max(1f, arenaDiameter)));

            Vector3 localDirToPlayer = dist > 0.01f
                ? transform.InverseTransformDirection(toPlayerWorld.normalized)
                : Vector3.forward;
            sensor.AddObservation(Mathf.Clamp(localDirToPlayer.x, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(localDirToPlayer.z, -1f, 1f));

            // 16-17. Velocidad relativa del Jugador en ejes locales del Jefe [2]
            Vector3 localPlayerVel = transform.InverseTransformDirection(playerController.Velocity) / Mathf.Max(1f, playerMaxSpeed);
            sensor.AddObservation(Mathf.Clamp(localPlayerVel.x, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp(localPlayerVel.z, -1f, 1f));

            // 18. Proximidad del Jefe al borde de la arena [1]
            float bossDistFromCenter = new Vector2(transform.localPosition.x, transform.localPosition.z).magnitude;
            sensor.AddObservation(Mathf.Clamp01(bossDistFromCenter / Mathf.Max(1f, arenaMaxRadius)));

            // 19-24. Telemetría Comportamental de Capa 1 [6]
            if (behaviorProfiler != null)
            {
                behaviorProfiler.WriteTelemetryToBuffer(telemetryBuffer, 0);
                for (int i = 0; i < 6; i++)
                {
                    sensor.AddObservation(telemetryBuffer[i]);
                }
            }
            else
            {
                for (int i = 0; i < 6; i++) sensor.AddObservation(0f);
            }
        }

        public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
        {
            if (bossFSM.IsBusy || bossHealth.IsDead)
            {
                for (int m = 1; m <= 5; m++)
                    actionMask.SetActionEnabled(0, m, false);

                for (int a = 1; a <= 4; a++)
                    actionMask.SetActionEnabled(1, a, false);
            }
        }

        public override void OnActionReceived(ActionBuffers actions)
        {
            if (episodeTerminated) return;

            int moveAction = actions.DiscreteActions[0];   // 0..5
            int attackAction = actions.DiscreteActions[1]; // 0..4

            if (!bossFSM.IsBusy && !bossHealth.IsDead)
            {
                // Evaluar uso táctico del MicroDash (Escudo naranja con I-Frames)
                if (moveAction == (int)BossMoveCommand.MicroDash)
                {
                    EvaluateMicroDashReward();
                }

                bossFSM.SetMoveCommand((BossMoveCommand)moveAction);

                if (attackAction > 0)
                {
                    bossFSM.TryExecuteAttack((BossAttackType)attackAction);
                }
            }

            EvaluateStepRewards();
        }

        private void EvaluateMicroDashReward()
        {
            if (playerController == null || Time.time - lastDashRewardTime < 2.4f) return;

            lastDashRewardTime = Time.time;
            float dist = Vector3.Distance(FlatPos(transform.position), FlatPos(playerController.transform.position));
            bool incomingProjectile = HasIncomingProjectile(6.0f);

            // Premia activar el escudo/dash cuando hay proyectil entrante o presión melee cercana
            if (incomingProjectile || dist <= 3.5f)
            {
                AddReward(0.18f);
            }
            else
            {
                // Penaliza gastar el cooldown del escudo sin amenaza cerca
                AddReward(-0.06f);
            }
        }

        private bool HasIncomingProjectile(float radius)
        {
            int hits = Physics.OverlapSphereNonAlloc(transform.position + Vector3.up, radius, threatBuffer);
            for (int i = 0; i < hits; i++)
            {
                Collider col = threatBuffer[i];
                if (col == null || col.transform.IsChildOf(transform)) continue;
                if (col.GetComponent<Projectile>() != null)
                {
                    return true;
                }
            }
            return false;
        }

        private void EvaluateStepRewards()
        {
            if (playerHealth == null) return;

            // 1. Recompensa fuerte por daño REAL conectado (+2.50 total por derrotar al jugador)
            float currentPlayerHP = playerHealth.CurrentHP;
            float dmgDealt = Mathf.Max(0f, prevPlayerHP - currentPlayerHP);
            if (dmgDealt > 0f)
            {
                AddReward(dmgDealt * 0.025f);
            }
            prevPlayerHP = currentPlayerHP;

            // 2. Penalización por daño recibido (-1.50 total si pierde sus 500 HP)
            float currentBossHP = bossHealth.CurrentHP;
            float dmgTaken = Mathf.Max(0f, prevBossHP - currentBossHP);
            if (dmgTaken > 0f)
            {
                AddReward(-dmgTaken * 0.003f);
            }
            prevBossHP = currentBossHP;

            // 3. Penalización leve por tiempo para mantener ritmo agresivo
            AddReward(-0.001f);

            // 4. Fin de episodio
            if (playerHealth.IsDead)
            {
                episodeTerminated = true;
                AddReward(2.5f);
                EndEpisode();
            }
            else if (bossHealth.IsDead)
            {
                episodeTerminated = true;
                AddReward(-2.0f);
                EndEpisode();
            }
            else if (MaxStep > 0 && StepCount >= MaxStep - 1)
            {
                episodeTerminated = true;
                AddReward(-0.5f);
                EndEpisode();
            }
        }

        // Solo penaliza ataques lanzados completamente fuera de rango (sin regalar puntos por fallar)
        private void HandleAttackStartedReward(BossAttackType attack)
        {
            if (episodeTerminated || playerController == null) return;

            float dist = Vector3.Distance(FlatPos(transform.position), FlatPos(playerController.transform.position));

            switch (attack)
            {
                case BossAttackType.ShockwaveRing:
                    if (dist > bossFSM.ShockwaveRadius + 1.5f) AddReward(-0.12f);
                    break;

                case BossAttackType.DelayedParrySlam:
                    if (dist > 4.5f) AddReward(-0.12f);
                    break;

                case BossAttackType.GapCloserLunge:
                    if (dist < 3.5f || dist > 11.5f) AddReward(-0.10f);
                    break;
            }
        }

        private void HandlePlayerStaggeredReward()
        {
            if (!episodeTerminated) AddReward(0.55f);
        }

        private void HandleBossStaggeredReward()
        {
            if (!episodeTerminated) AddReward(-0.35f);
        }

        private void HandlePlayerParriedReward(DamageInfo _)
        {
            if (!episodeTerminated) AddReward(-0.15f);
        }

        public override void Heuristic(in ActionBuffers actionsOut)
        {
            var discrete = actionsOut.DiscreteActions;
            discrete[0] = (int)BossMoveCommand.Advance;
            discrete[1] = (int)BossAttackType.None;
        }

        private Vector3 FlatPos(Vector3 pos) => new Vector3(pos.x, 0f, pos.z);
    }
}