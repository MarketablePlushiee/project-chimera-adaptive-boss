using UnityEngine;
using Chimera.Core;
using Chimera.Boss;

namespace Chimera.Player
{
    public enum BotArchetype
    {
        AggressiveRusher = 0, // Presión melee constante + Parry reactivo + Dash ofensivo
        EvasiveKiter = 1,     // Mantiene distancia + strafe lateral marcado + disparos cargados
        BalancedHybrid = 2    // Alterna distancia y castigo melee cuando el Jefe entra en Recovery/Stagger
    }

    // Bot Heurístico Parametrizado para entrenar y evaluar la adaptabilidad del Jefe PPO.
    // Incluye aleatorización de dominio por episodio (sesgo lateral, tiempos de reacción y arquetipo)
    // para evitar que la red neuronal memorice patrones rígidos.
    [RequireComponent(typeof(PlayerController))]
    [RequireComponent(typeof(HealthComponent))]
    public class TrainingBotController : MonoBehaviour
    {
        [Header("Configuración de Arquetipo")]
        [SerializeField] private BotArchetype archetype = BotArchetype.AggressiveRusher;
        [Tooltip("Si está activo, cambia de arquetipo y preferencias aleatoriamente al reiniciar cada episodio.")]
        [SerializeField] private bool randomizeArchetypeOnReset = true;

        [Header("Referencias de Combate")]
        [SerializeField] private BossCombatFSM bossFSM;
        [SerializeField] private HealthComponent bossHealth;

        [Header("Variabilidad Humana (Domain Randomization)")]
        [SerializeField] private float minReactionDelay = 0.08f;
        [SerializeField] private float maxReactionDelay = 0.20f;

        private PlayerController playerController;
        private HealthComponent playerHealth;

        private float strafeSign = 1f;
        private float reactionTimer;
        private BossFSMState lastObservedBossState = BossFSMState.Locomotion;
        private float nextStrafeFlipTime;
        private float guardReleaseTime;

        public BotArchetype CurrentArchetype => archetype;

        private void Awake()
        {
            playerController = GetComponent<PlayerController>();
            playerHealth = GetComponent<HealthComponent>();
        }

        private void Start()
        {
            if (playerController.IsBotControlled)
            {
                ResetBot();
            }
        }

        private void Update()
        {
            if (!playerController.IsBotControlled || playerHealth.IsDead || bossFSM == null || bossHealth == null || bossHealth.IsDead)
                return;

            Vector3 myPos = FlatPos(transform.position);
            Vector3 bossPos = FlatPos(bossFSM.transform.position);
            Vector3 toBoss = bossPos - myPos;
            float distance = toBoss.magnitude;
            Vector3 dirToBoss = distance > 0.01f ? toBoss.normalized : Vector3.forward;
            Vector3 rightDir = Vector3.Cross(Vector3.up, dirToBoss).normalized;

            // Detecta transiciones de estado del Jefe con un pequeño retardo de reacción humano
            if (bossFSM.CurrentState != lastObservedBossState)
            {
                lastObservedBossState = bossFSM.CurrentState;
                reactionTimer = Random.Range(minReactionDelay, maxReactionDelay);
            }

            if (reactionTimer > 0f)
            {
                reactionTimer -= Time.deltaTime;
            }

            switch (archetype)
            {
                case BotArchetype.AggressiveRusher:
                    UpdateRusherBehavior(distance, dirToBoss, rightDir, bossPos);
                    break;

                case BotArchetype.EvasiveKiter:
                    UpdateKiterBehavior(distance, dirToBoss, rightDir, bossPos);
                    break;

                case BotArchetype.BalancedHybrid:
                    UpdateHybridBehavior(distance, dirToBoss, rightDir, bossPos);
                    break;
            }
        }

        // ARQUETIPO 1: Rusher Melee agresivo + Parry reactivo
        private void UpdateRusherBehavior(float dist, Vector3 dirToBoss, Vector3 rightDir, Vector3 bossPos)
        {
            bool shouldGuard = Time.time < guardReleaseTime;

            // Reacción defensiva ante telegrafías del Jefe
            if (bossFSM.CurrentState == BossFSMState.Telegraph && reactionTimer <= 0f)
            {
                if (bossFSM.CurrentAttack == BossAttackType.ShockwaveRing && dist < bossFSM.ShockwaveRadius + 0.8f)
                {
                    // Ante onda expansiva imparable, hace Dash hacia afuera
                    playerController.SetBotControls(new Vector2(-dirToBoss.x, -dirToBoss.z), bossPos, false);
                    playerController.TryDash();
                    return;
                }
                else if (bossFSM.CurrentAttack == BossAttackType.GapCloserLunge || bossFSM.CurrentAttack == BossAttackType.DelayedParrySlam)
                {
                    // Intenta hacer Parry (y a veces falla el timing en el amague retardado)
                    shouldGuard = true;
                    guardReleaseTime = Time.time + 0.32f;
                }
            }

            // Movimiento ofensivo: cerrar brecha rápido y rodear ligeramente
            Vector3 moveDir = dist > 1.95f
                ? (dirToBoss + rightDir * (strafeSign * 0.25f)).normalized
                : (rightDir * (strafeSign * 0.7f));

            // Evita quedar pegado a los muros exteriores
            moveDir = ApplyArenaBoundaryRepulsion(moveDir);

            playerController.SetBotControls(new Vector2(moveDir.x, moveDir.z), bossPos, shouldGuard);

            if (dist > 6.5f && bossFSM.CurrentState == BossFSMState.Locomotion)
            {
                playerController.TryDash();
            }
            else if (dist <= 2.35f && !shouldGuard)
            {
                playerController.TryMeleeAttack();
            }
        }

        // ARQUETIPO 2: Kiter a distancia con sesgo lateral fuerte y Dashes evasivos
        private void UpdateKiterBehavior(float dist, Vector3 dirToBoss, Vector3 rightDir, Vector3 bossPos)
        {
            // Mantiene un sesgo lateral dominante (80% hacia su lado preferido) para que Capa 1 lo detecte
            if (Time.time >= nextStrafeFlipTime)
            {
                strafeSign = Random.value < 0.82f ? strafeSign : -strafeSign;
                nextStrafeFlipTime = Time.time + Random.Range(2.5f, 4.5f);
            }

            float radialWeight = 0f;
            if (dist < 7.2f) radialWeight = -0.85f;      // Retrocede si el Jefe se acerca
            else if (dist > 9.8f) radialWeight = 0.55f;  // Se acerca si está muy lejos

            Vector3 moveDir = (dirToBoss * radialWeight + rightDir * (strafeSign * 0.95f)).normalized;
            moveDir = ApplyArenaBoundaryRepulsion(moveDir);

            playerController.SetBotControls(new Vector2(moveDir.x, moveDir.z), bossPos, false);

            // Esquive con Dash lateral si el Jefe lanza embestida, ráfaga o se acerca demasiado
            if (reactionTimer <= 0f &&
                ((bossFSM.CurrentState == BossFSMState.Telegraph &&
                  (bossFSM.CurrentAttack == BossAttackType.GapCloserLunge || bossFSM.CurrentAttack == BossAttackType.LeadShotVolley))
                 || dist < 4.2f))
            {
                playerController.TryDash();
                return;
            }

            // Dispara a distancia cada vez que está en rango seguro
            if (dist >= 4.5f)
            {
                playerController.TryRangedAttack();
            }
            else if (dist <= 2.2f)
            {
                playerController.TryMeleeAttack();
            }
        }

        // ARQUETIPO 3: Híbrido (pokea a media distancia y entra a castigar en Recovery/Stagger)
        private void UpdateHybridBehavior(float dist, Vector3 dirToBoss, Vector3 rightDir, Vector3 bossPos)
        {
            bool punishWindow = bossFSM.CurrentState == BossFSMState.Staggered || bossFSM.CurrentState == BossFSMState.Recovery;

            if (punishWindow)
            {
                Vector3 rushDir = ApplyArenaBoundaryRepulsion(dirToBoss);
                playerController.SetBotControls(new Vector2(rushDir.x, rushDir.z), bossPos, false);

                if (dist > 4.5f && bossFSM.CurrentState == BossFSMState.Staggered)
                    playerController.TryDash();
                else if (dist <= 2.35f)
                    playerController.TryMeleeAttack();
                return;
            }

            // Fuera de ventana de castigo, orbita a media distancia (~5.5m) y dispara
            float radial = dist < 4.8f ? -0.7f : (dist > 6.5f ? 0.6f : 0f);
            Vector3 orbitDir = ApplyArenaBoundaryRepulsion((dirToBoss * radial + rightDir * strafeSign).normalized);
            playerController.SetBotControls(new Vector2(orbitDir.x, orbitDir.z), bossPos, false);

            if (bossFSM.CurrentState == BossFSMState.Telegraph && reactionTimer <= 0f && dist < 5.0f)
            {
                playerController.TryDash();
            }
            else if (dist >= 4.0f)
            {
                playerController.TryRangedAttack();
            }
        }

        // Fuerza centrípeta suave cerca de los muros de la arena (radio ~10.5m) para evitar que el bot quede atascado
        private Vector3 ApplyArenaBoundaryRepulsion(Vector3 desiredDir)
        {
            Vector3 arenaCenter = transform.parent != null ? transform.parent.position : Vector3.zero;
            Vector3 offsetFromCenter = FlatPos(transform.position) - FlatPos(arenaCenter);
            float radius = offsetFromCenter.magnitude;

            if (radius > 8.8f)
            {
                Vector3 inward = -offsetFromCenter.normalized;
                float weight = Mathf.Clamp01((radius - 8.8f) / 2.2f);
                return Vector3.Lerp(desiredDir, inward, weight).normalized;
            }

            return desiredDir;
        }

        public void SetArchetype(BotArchetype newArchetype, bool lockArchetype = true)
        {
            archetype = newArchetype;
            randomizeArchetypeOnReset = !lockArchetype;
            strafeSign = Random.value > 0.5f ? 1f : -1f;
            nextStrafeFlipTime = Time.time + Random.Range(2f, 4f);
            guardReleaseTime = -10f;
            lastObservedBossState = BossFSMState.Locomotion;
        }

        public void ResetBot()
        {
            if (randomizeArchetypeOnReset)
            {
                // Distribución balanceada: 40% Rusher, 40% Kiter, 20% Híbrido
                float roll = Random.value;
                if (roll < 0.40f) archetype = BotArchetype.AggressiveRusher;
                else if (roll < 0.80f) archetype = BotArchetype.EvasiveKiter;
                else archetype = BotArchetype.BalancedHybrid;
            }

            strafeSign = Random.value > 0.5f ? 1f : -1f;
            nextStrafeFlipTime = Time.time + Random.Range(2f, 4f);
            guardReleaseTime = -10f;
            reactionTimer = 0f;
            lastObservedBossState = BossFSMState.Locomotion;
        }

        private Vector3 FlatPos(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}