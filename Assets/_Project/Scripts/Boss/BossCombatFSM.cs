using System;
using System.Collections;
using UnityEngine;
using Chimera.Core;

namespace Chimera.Boss
{
    public enum BossFSMState
    {
        Locomotion,
        Telegraph,
        ActiveHitbox,
        Recovery,
        Staggered
    }

    public enum BossMoveCommand
    {
        Hold = 0,
        Advance = 1,
        Retreat = 2,
        StrafeLeft = 3,
        StrafeRight = 4,
        MicroDash = 5
    }

    public enum BossAttackType
    {
        None = 0,
        GapCloserLunge = 1,
        ShockwaveRing = 2,
        LeadShotVolley = 3,
        DelayedParrySlam = 4
    }

    // Capa 3 de Project Chimera: Máquina de Estados Determinista del Jefe.
    // Ejecuta posicionamiento y contra-ataques con telegrafías claras, VFX y escalado en Fase 2.
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

        [Header("Referencias Visuales y VFX")]
        [SerializeField] private Renderer coreRenderer;
        [SerializeField] private Material phase1CoreMaterial;
        [SerializeField] private Material phase2CoreMaterial;
        [SerializeField] private Material vfxMaterial;
        [SerializeField] private Transform hammerArmPivot;
        [SerializeField] private Transform visualRoot;
        [SerializeField] private GameObject shieldSphereVisual;

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
        [SerializeField] private float lungeTelegraphTime = 0.55f;
        [SerializeField] private float lungeSpeed = 24f;
        [SerializeField] private float lungeDuration = 0.30f;
        [SerializeField] private float lungeDamage = 24f;
        [SerializeField] private float lungePostureDamage = 25f;

        [SerializeField] private float shockwaveTelegraphTime = 0.65f;
        [SerializeField] private float shockwaveRadius = 4.25f;
        [SerializeField] private float shockwaveDamage = 22f;
        [SerializeField] private float shockwavePostureDamage = 30f;

        [SerializeField] private float volleyTelegraphTime = 0.50f;
        [SerializeField] private float volleyProjectileSpeed = 19f;
        [SerializeField] private float volleyDamage = 15f;
        [SerializeField] private float volleyPostureDamage = 15f;
        [SerializeField] private float volleySpreadAngle = 18f;

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
        private TrailRenderer lungeTrail;

        private BossFSMState currentState = BossFSMState.Locomotion;
        private BossAttackType currentAttack = BossAttackType.None;
        private BossMoveCommand currentMoveCommand = BossMoveCommand.Hold;

        private bool isPhase2;
        private bool isMicroDashing;
        private float lastMicroDashTime = -10f;
        private float lastAttackEndTime = -10f;
        private int autonomousCycleIndex;

        private float externalDodgeLateralBias;
        private Vector3 predictedAimDirection = Vector3.forward;

        public event Action<BossFSMState> OnStateChanged;
        public event Action<BossAttackType> OnAttackStarted;
        public event Action<BossAttackType, bool> OnAttackCompleted;
        public event Action OnPhase2Entered;
        public event Action OnBossStaggered;

        public BossFSMState CurrentState => currentState;
        public BossAttackType CurrentAttack => currentAttack;
        public BossMoveCommand CurrentMoveCommand => currentMoveCommand;
        public bool IsPhase2 => isPhase2;
        public bool IsBusy => currentState != BossFSMState.Locomotion;
        public bool AutonomousTestMode { get => autonomousTestMode; set => autonomousTestMode = value; }
        public Vector3 PredictedAimDirection => predictedAimDirection;
        public float DodgeLateralBias { get => externalDodgeLateralBias; set => externalDodgeLateralBias = Mathf.Clamp(value, -1f, 1f); }
        public float ShockwaveRadius => shockwaveRadius;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            health = GetComponent<HealthComponent>();

            health.OnHealthChanged += HandleHealthChanged;
            health.OnPostureBroken += HandlePostureBroken;
            health.OnDeath += HandleBossDeath;

            HideAllTelegraphs();
            if (shieldSphereVisual != null) shieldSphereVisual.SetActive(false);

            lungeTrail = CombatVFX.AttachTrail(
                transform,
                vfxMaterial,
                new Color(1f, 0.45f, 0f, 0.95f),
                new Color(1f, 0.08f, 0.25f, 0f),
                startWidth: 1.0f,
                trailTime: 0.28f,
                startEmitting: false
            );
        }

        private void Start()
        {
            ApplyPhaseVisuals(false);
        }

        private void Update()
        {
            if (health.IsDead) return;

            if (autonomousTestMode && currentState == BossFSMState.Locomotion)
            {
                RunAutonomousTestBehavior();
            }

            if (currentState == BossFSMState.Locomotion)
            {
                ExecuteLocomotion();
            }
        }

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

            float side = Mathf.Abs(externalDodgeLateralBias) > 0.15f
                ? -Mathf.Sign(externalDodgeLateralBias)
                : (UnityEngine.Random.value > 0.5f ? 1f : -1f);

            StartCoroutine(MicroDashRoutine(rightDir * side));
        }

        private IEnumerator MicroDashRoutine(Vector3 dashDir)
        {
            isMicroDashing = true;
            health.SetInvulnerable(true);
            if (shieldSphereVisual != null) shieldSphereVisual.SetActive(true);
            if (lungeTrail != null) lungeTrail.emitting = true;

            float elapsed = 0f;
            while (elapsed < microDashDuration)
            {
                Vector3 step = dashDir * microDashSpeed;
                step.y = -9.81f;
                controller.Move(step * Time.deltaTime);
                elapsed += Time.deltaTime;
                yield return null;
            }

            if (lungeTrail != null) lungeTrail.emitting = false;
            if (shieldSphereVisual != null) shieldSphereVisual.SetActive(false);
            health.SetInvulnerable(false);
            isMicroDashing = false;
        }

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
                    hitLanded = true;
                    break;

                case BossAttackType.DelayedParrySlam:
                    yield return StartCoroutine(RoutineDelayedParrySlam(speedScale, landed => hitLanded = landed));
                    break;
            }

            HideAllTelegraphs();
            OnAttackCompleted?.Invoke(attackType, hitLanded);

            if (isPhase2 && !isComboFollowUp && currentState != BossFSMState.Staggered && !health.IsDead)
            {
                BossAttackType followUp = ChoosePhase2ComboFollowUp(attackType);
                yield return new WaitForSeconds(0.15f);
                yield return StartCoroutine(ExecuteAttackSequence(followUp, isComboFollowUp: true));
                yield break;
            }

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

        // 1. EMBESTIDA (Gap-Closer Lunge)
        private IEnumerator RoutineGapCloserLunge(float speedScale, Action<bool> onResult)
        {
            ChangeState(BossFSMState.Telegraph);
            if (telegraphLunge != null) telegraphLunge.SetActive(true);

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

            if (lungeTrail != null) lungeTrail.emitting = true;
            CombatVFX.SpawnImpactBurst(
                transform.position + Vector3.up * 0.9f,
                vfxMaterial,
                new Color(1f, 0.5f, 0f, 1f),
                new Color(1f, 0.1f, 0.2f, 0f),
                count: 20,
                maxSpeed: 10f,
                size: 0.32f
            );

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

            if (lungeTrail != null) lungeTrail.emitting = false;
            onResult?.Invoke(dealtDamage);
        }

        // 2. ONDA EXPANSIVA 360° (Shockwave Ring)
        private IEnumerator RoutineShockwaveRing(float speedScale, Action<bool> onResult)
        {
            ChangeState(BossFSMState.Telegraph);
            if (telegraphShockwave != null)
            {
                telegraphShockwave.SetActive(true);
                telegraphShockwave.transform.localScale = new Vector3(shockwaveRadius * 2f, 0.01f, shockwaveRadius * 2f);
            }

            if (visualRoot != null) visualRoot.localPosition = new Vector3(0f, 0.35f, 0f);

            yield return new WaitForSeconds(shockwaveTelegraphTime / speedScale);

            if (visualRoot != null) visualRoot.localPosition = Vector3.zero;
            if (telegraphShockwave != null) telegraphShockwave.SetActive(false);

            ChangeState(BossFSMState.ActiveHitbox);

            // Dispara la onda expansiva visual desde el centro del jefe hasta el radio exterior
            CombatVFX.SpawnShockwaveWave(transform.position, shockwaveRadius, 0.26f, vfxMaterial);

            Vector3 radialDir = playerTarget != null
                ? (FlatPos(playerTarget.position) - FlatPos(transform.position)).normalized
                : transform.forward;

            bool dealtDamage = CheckSphereHit(transform.position + Vector3.up * 0.8f, shockwaveRadius, shockwaveDamage, shockwavePostureDamage, radialDir, canBeParried: false);
            yield return new WaitForSeconds(0.18f);

            onResult?.Invoke(dealtDamage);
        }

        // 3. RÁFAGA PREDICTIVA (Lead-Shot Volley): 3 proyectiles en Fase 1, 5 proyectiles en Fase 2
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

            if (bossProjectilePrefab != null)
            {
                Vector3 spawnPos = cannonFirePoint != null
                    ? cannonFirePoint.position
                    : transform.position + Vector3.up * 1.2f + predictedAimDirection * 1.1f;

                CombatVFX.SpawnImpactBurst(
                    spawnPos,
                    vfxMaterial,
                    new Color(1f, 0.6f, 0f, 1f),
                    new Color(1f, 0.1f, 0.2f, 0f),
                    count: 16,
                    maxSpeed: 8f,
                    size: 0.25f
                );

                // En Fase 2 dispara un abanico espectacular de 5 proyectiles en vez de 3
                float[] angles = isPhase2
                    ? new[] { -volleySpreadAngle * 1.4f, -volleySpreadAngle * 0.7f, 0f, volleySpreadAngle * 0.7f, volleySpreadAngle * 1.4f }
                    : new[] { -volleySpreadAngle, 0f, volleySpreadAngle };

                for (int i = 0; i < angles.Length; i++)
                {
                    Vector3 shotDir = Quaternion.Euler(0f, angles[i], 0f) * predictedAimDirection;
                    GameObject projObj = Instantiate(bossProjectilePrefab, spawnPos, Quaternion.LookRotation(shotDir));

                    CombatVFX.AttachTrail(
                        projObj.transform,
                        vfxMaterial,
                        isPhase2 ? new Color(1f, 0.1f, 0.3f, 0.95f) : new Color(1f, 0.55f, 0f, 0.95f),
                        new Color(1f, 0.2f, 0f, 0f),
                        startWidth: 0.30f,
                        trailTime: 0.16f
                    );

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
                            arenaWallLayer,
                            vfxMaterial
                        );
                    }
                }
            }

            yield return new WaitForSeconds(0.15f);
        }

        // 4. GOLPE RETARDADO / AMAGUE (Anti-Parry Delayed Slam)
        private IEnumerator RoutineDelayedParrySlam(float speedScale, Action<bool> onResult)
        {
            ChangeState(BossFSMState.Telegraph);
            if (telegraphDelayedSlam != null) telegraphDelayedSlam.SetActive(true);

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

            yield return new WaitForSeconds(slamFinalStrikeDelay / speedScale);

            if (telegraphDelayedSlam != null) telegraphDelayedSlam.SetActive(false);
            ChangeState(BossFSMState.ActiveHitbox);

            if (hammerArmPivot != null) hammerArmPivot.localRotation = Quaternion.Euler(35f, 0f, 0f);

            Vector3 slamCenter = transform.position + Vector3.up * 0.25f + transform.forward * 2.1f;
            CombatVFX.SpawnImpactBurst(
                slamCenter,
                vfxMaterial,
                new Color(1f, 0.2f, 0.15f, 1f),
                new Color(1f, 0.7f, 0.1f, 0f),
                count: 28,
                maxSpeed: 11f,
                size: 0.34f
            );

            bool dealtDamage = CheckSphereHit(transform.position + Vector3.up * 0.9f + transform.forward * 2.1f, slamRadius, slamDamage, slamPostureDamage, transform.forward, canBeParried: true);

            yield return new WaitForSeconds(0.18f);
            if (hammerArmPivot != null) hammerArmPivot.localRotation = Quaternion.identity;

            onResult?.Invoke(dealtDamage);
        }

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

            float leadOffset = externalDodgeLateralBias * Mathf.Clamp(dist * 0.35f, 1.2f, 4.2f);
            Vector3 predictedTargetPos = FlatPos(playerTarget.position) + rightOfBoss * leadOffset;

            Vector3 finalDir = (predictedTargetPos - FlatPos(transform.position)).normalized;
            predictedAimDirection = finalDir.sqrMagnitude > 0.01f ? finalDir : baseDir;
        }

        private BossAttackType ChoosePhase2ComboFollowUp(BossAttackType firstAttack)
        {
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
                CombatVFX.SpawnShockwaveWave(transform.position, 5.0f, 0.35f, vfxMaterial);
                OnPhase2Entered?.Invoke();
            }
        }

        private void HandlePostureBroken()
        {
            StopAllCoroutines();
            HideAllTelegraphs();
            isMicroDashing = false;
            if (lungeTrail != null) lungeTrail.emitting = false;
            health.SetInvulnerable(false);
            if (shieldSphereVisual != null) shieldSphereVisual.SetActive(false);
            if (visualRoot != null) visualRoot.localPosition = Vector3.zero;
            if (hammerArmPivot != null) hammerArmPivot.localRotation = Quaternion.identity;

            StartCoroutine(StaggerRoutine());
        }

        private void HandleBossDeath()
        {
            StopAllCoroutines();
            HideAllTelegraphs();
            isMicroDashing = false;
            currentAttack = BossAttackType.None;
            if (lungeTrail != null) lungeTrail.emitting = false;
            health.SetInvulnerable(false);
            if (shieldSphereVisual != null) shieldSphereVisual.SetActive(false);

            // Estallido final de descarga al quedarse sin vida
            Vector3 burstPos = coreRenderer != null
                ? coreRenderer.transform.position
                : transform.position + Vector3.up * 1.4f;

            CombatVFX.SpawnImpactBurst(
                burstPos,
                vfxMaterial,
                new Color(1f, 1f, 1f, 1f),
                new Color(0.6f, 0.8f, 1f, 0f),
                count: 36,
                maxSpeed: 12f,
                size: 0.35f
            );

            // Núcleo blanco apagado (sin emisión)
            if (coreRenderer != null)
            {
                coreRenderer.material.color = new Color(0.92f, 0.94f, 0.96f, 1f);
                coreRenderer.material.SetColor("_EmissionColor", Color.black);
            }

            // Cuerpo desplomado e inclinado sobre la arena
            if (visualRoot != null)
            {
                visualRoot.localPosition = new Vector3(0f, -0.55f, 0.25f);
                visualRoot.localRotation = Quaternion.Euler(58f, 0f, 14f);
            }
            if (hammerArmPivot != null)
            {
                hammerArmPivot.localRotation = Quaternion.Euler(45f, 0f, 0f);
            }
        }

        private IEnumerator StaggerRoutine()
        {
            ChangeState(BossFSMState.Staggered);
            currentAttack = BossAttackType.None;
            OnBossStaggered?.Invoke();

            // 1. Estallido inicial de chispas eléctricas al romper la postura
            Vector3 burstPos = coreRenderer != null
                ? coreRenderer.transform.position
                : transform.position + Vector3.up * 1.4f;

            CombatVFX.SpawnImpactBurst(
                burstPos,
                vfxMaterial,
                new Color(0.6f, 0.95f, 1f, 1f),
                new Color(1f, 1f, 1f, 0f),
                count: 30,
                maxSpeed: 10f,
                size: 0.30f
            );

            // 2. Bucle visual durante el aturdimiento: núcleo pálido parpadeante + cuerpo desplomado temblando
            float elapsed = 0f;
            Color paleColor = new Color(0.55f, 0.62f, 0.68f, 1f);
            Color flickerColor = new Color(0.85f, 0.92f, 1f, 1f);

            while (elapsed < staggerDuration)
            {
                float pulse = Mathf.PingPong(elapsed * 12f, 1f);

                // Parpadeo pálido del núcleo energético
                if (coreRenderer != null)
                {
                    Color currentTint = Color.Lerp(paleColor, flickerColor, pulse);
                    coreRenderer.material.color = currentTint;
                    coreRenderer.material.SetColor("_EmissionColor", currentTint * Mathf.Lerp(0.15f, 0.9f, pulse));
                }

                // Inclinación hacia adelante + leve descenso + temblor mecánico
                if (visualRoot != null)
                {
                    float shakeX = Mathf.Sin(elapsed * 55f) * 0.05f;
                    visualRoot.localPosition = new Vector3(shakeX, -0.22f, 0f);
                    visualRoot.localRotation = Quaternion.Euler(28f, 0f, 0f);
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            // 3. Restaurar postura y material energético de la fase actual
            if (visualRoot != null)
            {
                visualRoot.localPosition = Vector3.zero;
                visualRoot.localRotation = Quaternion.identity;
            }

            ApplyPhaseVisuals(isPhase2);

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

        public void ResetBossState(Vector3 localStartPos)
        {
            StopAllCoroutines();
            HideAllTelegraphs();

            isPhase2 = false;
            isMicroDashing = false;
            if (lungeTrail != null) lungeTrail.emitting = false;
            if (lungeTrail != null) lungeTrail.emitting = false;
            if (shieldSphereVisual != null) shieldSphereVisual.SetActive(false);
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

        private void LateUpdate()
        {
            Vector3 localPos = transform.localPosition;
            Vector2 flat = new Vector2(localPos.x, localPos.z);
            const float maxArenaRadius = 8.6f;

            if (flat.sqrMagnitude > maxArenaRadius * maxArenaRadius)
            {
                flat = flat.normalized * maxArenaRadius;
                controller.enabled = false;
                transform.localPosition = new Vector3(flat.x, localPos.y, flat.y);
                controller.enabled = true;
            }
        }
    }
}
