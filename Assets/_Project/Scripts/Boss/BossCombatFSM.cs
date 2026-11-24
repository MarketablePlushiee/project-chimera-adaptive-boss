using System;
using System.Collections;
using UnityEngine;
using Chimera.Core;

namespace Chimera.Boss
{
    // Estados deterministas de la Capa 3 (Ejecución de Combate)
    public enum BossFSMState
    {
        Locomotion,     // Libre para moverse, flanquear o iniciar un ataque
        Telegraph,      // Señal visual activa en suelo/arma antes del impacto
        ActiveHitbox,   // Ventana física donde se aplica daño o desplazamiento
        Recovery,       // Pausa post-ataque donde el jugador puede castigar
        Staggered       // Postura rota: aturdido y vulnerable a daño extra
    }

    // Acciones tácticas de posicionamiento (decididas por la Capa 2 o bot de prueba)
    public enum BossMoveCommand
    {
        Hold = 0,
        Advance = 1,
        Retreat = 2,
        StrafeLeft = 3,
        StrafeRight = 4,
        MicroDash = 5
    }

    // Los 4 contra-ataques especializados del Jefe
    public enum BossAttackType
    {
        None = 0,
        GapCloserLunge = 1,     // Embestida: castiga distancia larga / disparos
        ShockwaveRing = 2,      // Onda 360°: castiga abuso de melee continuo
        LeadShotVolley = 3,     // Ráfaga predictiva: castiga esquive lateral repetitivo
        DelayedParrySlam = 4    // Golpe retardado con amague: castiga spam de parry
    }

    // Capa 3 de Project Chimera: Máquina de Estados Determinista del Jefe.
    // Recibe órdenes de alto nivel (posicionamiento y contra-ataque) y las ejecuta
    // con telegrafías visuales claras, ventanas de recuperación y transición a Fase 2.
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(HealthComponent))]
    public class BossCombatFSM : MonoBehaviour
    {
        [Header("Modo de Prueba Autónoma (Desactivar al conectar ML-Agents)")]
        [Tooltip("Si está activo, el jefe prueba sus 4 contra-ataques automáticamente sin red neuronal.")]
        [SerializeField] private bool autonomousTestMode = true;

        [Header("Referencias de Combate")]
        [SerializeField] private Transform playerTarget;
        [SerializeField] private GameObject bossProjectilePrefab;
        [SerializeField] private Transform cannonFirePoint;
        [SerializeField] private LayerMask playerLayer;
        [SerializeField] private LayerMask arenaWallLayer;

        [Header("Referencias Visuales (Primitivas)")]
        [SerializeField] private Renderer coreRenderer;
        [SerializeField] private Material phase1CoreMaterial;
        [SerializeField] private Material phase2CoreMaterial;
        [SerializeField] private Transform hammerArmPivot;
        [SerializeField] private Transform visualRoot;

        [Header("Telegrafías en el Suelo (Hijos de Telegraphs)")]
        [SerializeField] private GameObject telegraphLunge;
        [SerializeField] private GameObject telegraphShockwave;
        [SerializeField] private GameObject telegraphVolley;
        [SerializeField] private GameObject telegraphDelayedSlam;

        [Header("Movimiento Base")]
        [SerializeField] private float moveSpeed = 5.2f;
        [SerializeField] private float rotationSpeed = 10f;
        [SerializeField] private float microDashSpeed = 16f;
        [SerializeField] private float microDashDuration = 0.20f;
        [SerializeField] private float microDashCooldown = 2.2f;

        [Header("Parámetros de los 4 Contra-ataques")]
        // 1. Embestida (Gap-Closer Lunge)
        [SerializeField] private float lungeTelegraphTime = 0.55f;
        [SerializeField] private float lungeSpeed = 24f;
        [SerializeField] private float lungeDuration = 0.30f;
        [SerializeField] private float lungeDamage = 24f;
        [SerializeField] private float lungePostureDamage = 25f;

        // 2. Onda Expansiva 360° (Shockwave Ring)
        [SerializeField] private float shockwaveTelegraphTime = 0.65f;
        [SerializeField] private float shockwaveRadius = 4.25f;
        [SerializeField] private float shockwaveDamage = 22f;
        [SerializeField] private float shockwavePostureDamage = 30f;

        // 3. Ráfaga Predictiva (Lead-Shot Volley)
        [SerializeField] private float volleyTelegraphTime = 0.50f;
        [SerializeField] private float volleyProjectileSpeed = 19f;
        [SerializeField] private float volleyDamage = 15f;
        [SerializeField] private float volleyPostureDamage = 15f;
        [SerializeField] private float volleySpreadAngle = 18f;

        // 4. Golpe Retardado / Amague (Anti-Parry Delayed Slam)
        [SerializeField] private float slamFeintHoldTime = 0.50f;
        [SerializeField] private float slamFinalStrikeDelay = 0.45f;
        [SerializeField] private float slamRadius = 2.1f;
        [SerializeField] private float slamDamage = 28f;
        [SerializeField] private float slamPostureDamage = 35f;

        [Header("Recuperación, Stagger y Fase 2")]
        [SerializeField] private float baseRecoveryTime = 0.85f;
        [SerializeField] private float staggerDuration = 1.8f;
        [SerializeField] private float phase2ThresholdHP = 0.50f;
        [SerializeField] private float phase2SpeedMultiplier = 1.25f;
        [SerializeField] private float phase2RecoveryMultiplier = 0.72f;

        private CharacterController controller;
        private HealthComponent health;

        private BossFSMState currentState = BossFSMState.Locomotion;
        private BossAttackType currentAttack = BossAttackType.None;
        private BossMoveCommand currentMoveCommand = BossMoveCommand.Hold;

        private bool isPhase2;
        private bool isMicroDashing;
        private float lastMicroDashTime = -10f;
        private float lastAttackEndTime = -10f;
        private int autonomousCycleIndex;

        // Sesgo lateral de esquive recibido desde PlayerBehaviorProfiler (-1 izq, +1 der)
        private float externalDodgeLateralBias;
        private Vector3 predictedAimDirection = Vector3.forward;

        // Eventos para telemetría, recompensas RL y HUD
        public event Action<BossFSMState> OnStateChanged;
        public event Action<BossAttackType> OnAttackStarted;
        public event Action<BossAttackType, bool> OnAttackCompleted; // (tipo, conectó daño)
        public event Action OnPhase2Entered;
        public event Action OnBossStaggered;

        // Propiedades de lectura para BossBrainAgent, HUD y Gizmos
        public BossFSMState CurrentState => currentState;
        public BossAttackType CurrentAttack => currentAttack;
        public BossMoveCommand CurrentMoveCommand => currentMoveCommand;
        public bool IsPhase2 => isPhase2;
        public bool IsBusy => currentState != BossFSMState.Locomotion;
        public Vector3 PredictedAimDirection => predictedAimDirection;
        public float DodgeLateralBias { get => externalDodgeLateralBias; set => externalDodgeLateralBias = Mathf.Clamp(value, -1f, 1f); }
        public float ShockwaveRadius => shockwaveRadius;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            health = GetComponent<HealthComponent>();

            health.OnHealthChanged += HandleHealthChanged;
            health.OnPostureBroken += HandlePostureBroken;

            HideAllTelegraphs();
        }

        private void Start()
        {
            ApplyPhaseVisuals(false);
        }

        private void Update()
        {
            if (health.IsDead) return;

            // En modo prueba autónoma, el jefe decide movimientos y cicla los 4 ataques
            if (autonomousTestMode && currentState == BossFSMState.Locomotion)
            {
                RunAutonomousTestBehavior();
            }

            if (currentState == BossFSMState.Locomotion)
            {
                ExecuteLocomotion();
            }
        }

        // Comportamiento autónomo simple solo para validar mecánicas en Fase 3
        private void RunAutonomousTestBehavior()
        {
            if (playerTarget == null) return;

            float dist = Vector3.Distance(FlatPos(transform.position), FlatPos(playerTarget.position));
            currentMoveCommand = dist > 6.5f ? BossMoveCommand.Advance : BossMoveCommand.StrafeRight;

            if (Time.time - lastAttackEndTime >= 1.35f)
            {
                BossAttackType nextAttack = (BossAttackType)((autonomousCycleIndex % 4) + 1);
                autonomousCycleIndex++;
                TryExecuteAttack(nextAttack);
            }
        }

        // Recibe orden de posicionamiento desde Capa 2 (ML-Agents)
        public void SetMoveCommand(BossMoveCommand command)
        {
            if (currentState != BossFSMState.Locomotion) return;

            if (command == BossMoveCommand.MicroDash)
            {
                TryMicroDash();
            }
            else
            {
                currentMoveCommand = command;
            }
        }

        // Recibe orden de contra-ataque desde Capa 2 (ML-Agents)
        public bool TryExecuteAttack(BossAttackType attackType)
        {
            if (attackType == BossAttackType.None || currentState != BossFSMState.Locomotion || health.IsDead)
                return false;

            StartCoroutine(ExecuteAttackSequence(attackType, isComboFollowUp: false));
            return true;
        }

        private void ExecuteLocomotion()
        {
            if (isMicroDashing || playerTarget == null) return;

            Vector3 toPlayer = FlatPos(playerTarget.position) - FlatPos(transform.position);
            float distance = toPlayer.magnitude;
            Vector3 dirToPlayer = distance > 0.01f ? toPlayer.normalized : transform.forward;

            // Encarar siempre al jugador suavemente
            Quaternion targetRot = Quaternion.LookRotation(dirToPlayer);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationSpeed * Time.deltaTime);

            Vector3 moveDir = Vector3.zero;
            Vector3 rightDir = Vector3.Cross(Vector3.up, dirToPlayer).normalized;

            switch (currentMoveCommand)
            {
                case BossMoveCommand.Hold:
                    moveDir = Vector3.zero;
                    break;
                case BossMoveCommand.Advance:
                    moveDir = dirToPlayer;
                    break;
                case BossMoveCommand.Retreat:
                    moveDir = -dirToPlayer;
                    break;
                case BossMoveCommand.StrafeLeft:
                    moveDir = -rightDir;
                    break;
                case BossMoveCommand.StrafeRight:
                    moveDir = rightDir;
                    break;
            }

            float speed = moveSpeed * (isPhase2 ? phase2SpeedMultiplier : 1f);
            Vector3 velocity = moveDir * speed;
            velocity.y = -9.81f;
            controller.Move(velocity * Time.deltaTime);
        }

        private void TryMicroDash()
        {
            if (isMicroDashing || Time.time - lastMicroDashTime < microDashCooldown || playerTarget == null)
                return;

            lastMicroDashTime = Time.time;
            Vector3 toPlayer = (FlatPos(playerTarget.position) - FlatPos(transform.position)).normalized;
            Vector3 rightDir = Vector3.Cross(Vector3.up, toPlayer).normalized;

            // Esquiva hacia el lado opuesto al sesgo del jugador o aleatorio si es neutro
            float side = Mathf.Abs(externalDodgeLateralBias) > 0.15f
                ? -Mathf.Sign(externalDodgeLateralBias)
                : (UnityEngine.Random.value > 0.5f ? 1f : -1f);

            StartCoroutine(MicroDashRoutine(rightDir * side));
        }

        private IEnumerator MicroDashRoutine(Vector3 dashDir)
        {
            isMicroDashing = true;
            float elapsed = 0f;
            while (elapsed < microDashDuration)
            {
                Vector3 step = dashDir * microDashSpeed;
                step.y = -9.81f;
                controller.Move(step * Time.deltaTime);
                elapsed += Time.deltaTime;
                yield return null;
            }
            isMicroDashing = false;
        }

        // Secuencia completa determinista: Telegraph -> ActiveHitbox -> (Combo Fase 2) -> Recovery
        private IEnumerator ExecuteAttackSequence(BossAttackType attackType, bool isComboFollowUp)
        {
            currentAttack = attackType;
            OnAttackStarted?.Invoke(attackType);

            bool hitLanded = false;
            float speedScale = isPhase2 ? phase2SpeedMultiplier : 1f;

            switch (attackType)
            {
                case BossAttackType.GapCloserLunge:
                    yield return StartCoroutine(RoutineGapCloserLunge(speedScale, landed => hitLanded = landed));
                    break;

                case BossAttackType.ShockwaveRing:
                    yield return StartCoroutine(RoutineShockwaveRing(speedScale, landed => hitLanded = landed));
                    break;

                case BossAttackType.LeadShotVolley:
                    yield return StartCoroutine(RoutineLeadShotVolley(speedScale));
                    hitLanded = true; // El disparo se considera ejecutado correctamente
                    break;

                case BossAttackType.DelayedParrySlam:
                    yield return StartCoroutine(RoutineDelayedParrySlam(speedScale, landed => hitLanded = landed));
                    break;
            }

            HideAllTelegraphs();
            OnAttackCompleted?.Invoke(attackType, hitLanded);

            // En Fase 2, el jefe encadena un segundo ataque rápido si este fue el primero del combo
            if (isPhase2 && !isComboFollowUp && currentState != BossFSMState.Staggered && !health.IsDead)
            {
                BossAttackType followUp = ChoosePhase2ComboFollowUp(attackType);
                yield return new WaitForSeconds(0.15f);
                yield return StartCoroutine(ExecuteAttackSequence(followUp, isComboFollowUp: true));
                yield break;
            }

            // Fase de Recuperación (ventana de castigo para el jugador)
            if (currentState != BossFSMState.Staggered && !health.IsDead)
            {
                ChangeState(BossFSMState.Recovery);
                float recTime = baseRecoveryTime * (isPhase2 ? phase2RecoveryMultiplier : 1f);
                yield return new WaitForSeconds(recTime);

                if (currentState == BossFSMState.Recovery)
                {
                    currentAttack = BossAttackType.None;
                    lastAttackEndTime = Time.time;
                    ChangeState(BossFSMState.Locomotion);
                }
            }
        }

        // 1. EMBESTIDA (Gap-Closer Lunge): Castiga jugadores a larga distancia
        private IEnumerator RoutineGapCloserLunge(float speedScale, Action<bool> onResult)
        {
            ChangeState(BossFSMState.Telegraph);
            if (telegraphLunge != null) telegraphLunge.SetActive(true);

            // Apunta al jugador durante la primera mitad de la telegrafía y luego fija dirección
            float elapsed = 0f;
            float duration = lungeTelegraphTime / speedScale;
            Vector3 lockedDir = transform.forward;

            while (elapsed < duration)
            {
                if (elapsed < duration * 0.65f && playerTarget != null)
                {
                    Vector3 toPlayer = (FlatPos(playerTarget.position) - FlatPos(transform.position)).normalized;
                    if (toPlayer.sqrMagnitude > 0.01f)
                    {
                        lockedDir = toPlayer;
                        transform.rotation = Quaternion.LookRotation(lockedDir);
                    }
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (telegraphLunge != null) telegraphLunge.SetActive(false);
            ChangeState(BossFSMState.ActiveHitbox);

            bool dealtDamage = false;
            float dashElapsed = 0f;
            while (dashElapsed < lungeDuration)
            {
                Vector3 step = lockedDir * (lungeSpeed * speedScale);
                step.y = -9.81f;
                controller.Move(step * Time.deltaTime);

                if (!dealtDamage)
                {
                    dealtDamage = CheckSphereHit(transform.position + Vector3.up * 1.0f + lockedDir * 0.9f, 1.35f, lungeDamage, lungePostureDamage, lockedDir, canBeParried: true);
                }

                dashElapsed += Time.deltaTime;
                yield return null;
            }

            onResult?.Invoke(dealtDamage);
        }

        // 2. ONDA EXPANSIVA 360° (Shockwave Ring): Castiga abuso de melee continuo
        private IEnumerator RoutineShockwaveRing(float speedScale, Action<bool> onResult)
        {
            ChangeState(BossFSMState.Telegraph);
            if (telegraphShockwave != null)
            {
                telegraphShockwave.SetActive(true);
                telegraphShockwave.transform.localScale = new Vector3(shockwaveRadius * 2f, 0.01f, shockwaveRadius * 2f);
            }

            // Eleva ligeramente el núcleo visual como advertencia
            if (visualRoot != null) visualRoot.localPosition = new Vector3(0f, 0.35f, 0f);

            yield return new WaitForSeconds(shockwaveTelegraphTime / speedScale);

            if (visualRoot != null) visualRoot.localPosition = Vector3.zero;
            if (telegraphShockwave != null) telegraphShockwave.SetActive(false);

            ChangeState(BossFSMState.ActiveHitbox);
            Vector3 radialDir = playerTarget != null
                ? (FlatPos(playerTarget.position) - FlatPos(transform.position)).normalized
                : transform.forward;

            // La onda expansiva obliga a usar Dash i-frames o salir del radio (no se puede parrear en seco)
            bool dealtDamage = CheckSphereHit(transform.position + Vector3.up * 0.8f, shockwaveRadius, shockwaveDamage, shockwavePostureDamage, radialDir, canBeParried: false);
            yield return new WaitForSeconds(0.15f);

            onResult?.Invoke(dealtDamage);
        }

        // 3. RÁFAGA PREDICTIVA (Lead-Shot Volley): Sesga proyectiles hacia dodgeLateralBias
        private IEnumerator RoutineLeadShotVolley(float speedScale)
        {
            ChangeState(BossFSMState.Telegraph);
            if (telegraphVolley != null) telegraphVolley.SetActive(true);

            float elapsed = 0f;
            float duration = volleyTelegraphTime / speedScale;

            while (elapsed < duration)
            {
                UpdatePredictedAimDirection();
                if (predictedAimDirection.sqrMagnitude > 0.01f)
                {
                    transform.rotation = Quaternion.LookRotation(predictedAimDirection);
                }
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (telegraphVolley != null) telegraphVolley.SetActive(false);
            ChangeState(BossFSMState.ActiveHitbox);

            // Dispara abanico de 3 proyectiles centrados en la dirección predicha
            if (bossProjectilePrefab != null)
            {
                Vector3 spawnPos = cannonFirePoint != null
                    ? cannonFirePoint.position
                    : transform.position + Vector3.up * 1.2f + predictedAimDirection * 1.1f;

                float[] angles = { -volleySpreadAngle, 0f, volleySpreadAngle };
                for (int i = 0; i < angles.Length; i++)
                {
                    Vector3 shotDir = Quaternion.Euler(0f, angles[i], 0f) * predictedAimDirection;
                    GameObject projObj = Instantiate(bossProjectilePrefab, spawnPos, Quaternion.LookRotation(shotDir));
                    Projectile proj = projObj.GetComponent<Projectile>();
                    if (proj != null)
                    {
                        proj.Initialize(
                            shotDir,
                            volleyProjectileSpeed * speedScale,
                            volleyDamage,
                            volleyPostureDamage,
                            gameObject,
                            playerLayer,
                            arenaWallLayer
                        );
                    }
                }
            }

            yield return new WaitForSeconds(0.15f);
        }

        // 4. GOLPE RETARDADO / AMAGUE (Anti-Parry Delayed Slam): Castiga spam de parry
        private IEnumerator RoutineDelayedParrySlam(float speedScale, Action<bool> onResult)
        {
            ChangeState(BossFSMState.Telegraph);
            if (telegraphDelayedSlam != null) telegraphDelayedSlam.SetActive(true);

            // Paso A: Levanta el brazo martillo rápido (amague inicial que provoca parry prematuro)
            if (hammerArmPivot != null) hammerArmPivot.localRotation = Quaternion.Euler(-75f, 0f, 0f);

            float feintElapsed = 0f;
            float feintDuration = slamFeintHoldTime / speedScale;
            while (feintElapsed < feintDuration)
            {
                if (playerTarget != null)
                {
                    Vector3 toPlayer = (FlatPos(playerTarget.position) - FlatPos(transform.position)).normalized;
                    if (toPlayer.sqrMagnitude > 0.01f)
                    {
                        transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(toPlayer), 12f * Time.deltaTime);
                    }
                }
                feintElapsed += Time.deltaTime;
                yield return null;
            }

            // Paso B: Pausa extra en el aire (cuando el parry prematuro del jugador ya expiró)
            yield return new WaitForSeconds(slamFinalStrikeDelay / speedScale);

            if (telegraphDelayedSlam != null) telegraphDelayedSlam.SetActive(false);
            ChangeState(BossFSMState.ActiveHitbox);

            // Baja el brazo martillo de golpe
            if (hammerArmPivot != null) hammerArmPivot.localRotation = Quaternion.Euler(35f, 0f, 0f);

            Vector3 slamCenter = transform.position + Vector3.up * 0.9f + transform.forward * 2.1f;
            bool dealtDamage = CheckSphereHit(slamCenter, slamRadius, slamDamage, slamPostureDamage, transform.forward, canBeParried: true);

            yield return new WaitForSeconds(0.18f);
            if (hammerArmPivot != null) hammerArmPivot.localRotation = Quaternion.identity;

            onResult?.Invoke(dealtDamage);
        }

        // Calcula el vector de disparo sesgado hacia el lado al que suele esquivar el jugador
        private void UpdatePredictedAimDirection()
        {
            if (playerTarget == null)
            {
                predictedAimDirection = transform.forward;
                return;
            }

            Vector3 toPlayer = FlatPos(playerTarget.position) - FlatPos(transform.position);
            float dist = toPlayer.magnitude;
            Vector3 baseDir = dist > 0.01f ? toPlayer.normalized : transform.forward;
            Vector3 rightOfBoss = Vector3.Cross(Vector3.up, baseDir).normalized;

            // Desplaza el punto de mira lateralmente según dodgeLateralBias (-1 a +1)
            float leadOffset = externalDodgeLateralBias * Mathf.Clamp(dist * 0.35f, 1.2f, 4.2f);
            Vector3 predictedTargetPos = FlatPos(playerTarget.position) + rightOfBoss * leadOffset;

            Vector3 finalDir = (predictedTargetPos - FlatPos(transform.position)).normalized;
            predictedAimDirection = finalDir.sqrMagnitude > 0.01f ? finalDir : baseDir;
        }

        private BossAttackType ChoosePhase2ComboFollowUp(BossAttackType firstAttack)
        {
            // Encadena ataques complementarios en Fase 2
            return firstAttack switch
            {
                BossAttackType.GapCloserLunge => BossAttackType.DelayedParrySlam,
                BossAttackType.ShockwaveRing => BossAttackType.LeadShotVolley,
                BossAttackType.LeadShotVolley => BossAttackType.GapCloserLunge,
                BossAttackType.DelayedParrySlam => BossAttackType.ShockwaveRing,
                _ => BossAttackType.ShockwaveRing
            };
        }

        private bool CheckSphereHit(Vector3 center, float radius, float dmgHP, float dmgPosture, Vector3 hitDir, bool canBeParried)
        {
            Collider[] hits = Physics.OverlapSphere(center, radius, playerLayer);
            for (int i = 0; i < hits.Length; i++)
            {
                HealthComponent targetHealth = hits[i].GetComponentInParent<HealthComponent>();
                if (targetHealth != null)
                {
                    DamageInfo info = new DamageInfo
                    {
                        hpDamage = dmgHP,
                        postureDamage = dmgPosture,
                        hitPoint = center,
                        hitDirection = hitDir,
                        attacker = gameObject,
                        isProjectile = false,
                        canBeParried = canBeParried
                    };
                    return targetHealth.TakeDamage(info);
                }
            }
            return false;
        }

        private void HandleHealthChanged(float current, float max)
        {
            if (!isPhase2 && max > 0f && (current / max) <= phase2ThresholdHP && current > 0f)
            {
                isPhase2 = true;
                ApplyPhaseVisuals(true);
                OnPhase2Entered?.Invoke();
            }
        }

        private void HandlePostureBroken()
        {
            StopAllCoroutines();
            HideAllTelegraphs();
            isMicroDashing = false;
            if (visualRoot != null) visualRoot.localPosition = Vector3.zero;
            if (hammerArmPivot != null) hammerArmPivot.localRotation = Quaternion.identity;

            StartCoroutine(StaggerRoutine());
        }

        private IEnumerator StaggerRoutine()
        {
            ChangeState(BossFSMState.Staggered);
            currentAttack = BossAttackType.None;
            OnBossStaggered?.Invoke();

            // Inclinación visual clara de aturdimiento
            if (visualRoot != null) visualRoot.localRotation = Quaternion.Euler(22f, 0f, 0f);

            yield return new WaitForSeconds(staggerDuration);

            if (visualRoot != null) visualRoot.localRotation = Quaternion.identity;

            if (!health.IsDead)
            {
                lastAttackEndTime = Time.time;
                ChangeState(BossFSMState.Locomotion);
            }
        }

        private void ApplyPhaseVisuals(bool phase2Active)
        {
            if (coreRenderer == null) return;
            coreRenderer.material = phase2Active && phase2CoreMaterial != null
                ? phase2CoreMaterial
                : phase1CoreMaterial;
        }

        private void HideAllTelegraphs()
        {
            if (telegraphLunge != null) telegraphLunge.SetActive(false);
            if (telegraphShockwave != null) telegraphShockwave.SetActive(false);
            if (telegraphVolley != null) telegraphVolley.SetActive(false);
            if (telegraphDelayedSlam != null) telegraphDelayedSlam.SetActive(false);
        }

        private void ChangeState(BossFSMState newState)
        {
            if (currentState == newState) return;
            currentState = newState;
            OnStateChanged?.Invoke(newState);
        }

        private Vector3 FlatPos(Vector3 pos) => new Vector3(pos.x, 0f, pos.z);

        // Reinicia completamente al Jefe al iniciar un nuevo episodio de entrenamiento
        public void ResetBossState(Vector3 localStartPos)
        {
            StopAllCoroutines();
            HideAllTelegraphs();

            isPhase2 = false;
            isMicroDashing = false;
            lastMicroDashTime = -10f;
            lastAttackEndTime = Time.time;
            currentAttack = BossAttackType.None;
            currentMoveCommand = BossMoveCommand.Hold;
            externalDodgeLateralBias = 0f;

            if (visualRoot != null)
            {
                visualRoot.localPosition = Vector3.zero;
                visualRoot.localRotation = Quaternion.identity;
            }
            if (hammerArmPivot != null) hammerArmPivot.localRotation = Quaternion.identity;

            ApplyPhaseVisuals(false);

            controller.enabled = false;
            transform.localPosition = localStartPos;
            transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            controller.enabled = true;

            health.ResetStats();
            ChangeState(BossFSMState.Locomotion);
        }
    }
}