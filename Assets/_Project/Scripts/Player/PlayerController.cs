using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using Chimera.Core;

namespace Chimera.Player
{
    // Controlador completo del Jugador en 3D (vista táctica).
    // Soporta tanto input humano (Teclado + Mouse) como control virtual por Bots de entrenamiento.
    // Emite eventos limpios para alimentar al PlayerBehaviorProfiler (Capa 1 de IA).
    [RequireComponent(typeof(CharacterController))]
    [RequireComponent(typeof(HealthComponent))]
    public class PlayerController : MonoBehaviour
    {
        [Header("Modo de Control")]
        [Tooltip("Activar cuando un Bot heurístico controle esta entidad durante el entrenamiento PPO.")]
        [SerializeField] private bool isBotControlled = false;

        [Header("Movimiento y Dash")]
        [SerializeField] private float moveSpeed = 7.5f;
        [SerializeField] private float rotationSpeed = 18f;
        [SerializeField] private float dashSpeed = 20f;
        [SerializeField] private float dashDuration = 0.22f;
        [SerializeField] private float dashCooldown = 1.0f;

        [Header("Combate Cuerpo a Cuerpo (Melee)")]
        [SerializeField] private float meleeDamage = 22f;
        [SerializeField] private float meleePostureDamage = 18f;
        [SerializeField] private float meleeRange = 2.2f;
        [SerializeField] private float meleeRadius = 1.15f;
        [SerializeField] private float meleeCooldown = 0.48f;

        [Header("Disparo Cargado a Distancia")]
        [SerializeField] private GameObject projectilePrefab;
        [SerializeField] private Transform firePoint;
        [SerializeField] private float rangedDamage = 16f;
        [SerializeField] private float rangedPostureDamage = 10f;
        [SerializeField] private float projectileSpeed = 22f;
        [SerializeField] private float rangedChargeTime = 0.18f;
        [SerializeField] private float rangedCooldown = 1.15f;

        [Header("Escudo y Parry Reactivo")]
        [SerializeField] private float perfectParryWindow = 0.24f;
        [SerializeField] private float guardDamageReduction = 0.70f;
        [SerializeField] private float parryStaggerToAttacker = 35f;
        [SerializeField] private float staggerDuration = 1.0f;

        [Header("Referencias Visuales y VFX")]
        [SerializeField] private Transform visualRoot;
        [SerializeField] private Transform weaponPivot;
        [SerializeField] private GameObject shieldVisual;
        [SerializeField] private Renderer bodyRenderer;
        [SerializeField] private Material normalBodyMaterial;
        [SerializeField] private Material dashBodyMaterial;
        [SerializeField] private Material vfxMaterial;

        [Header("Capas y Referencias de Escena")]
        [SerializeField] private LayerMask bossLayer;
        [SerializeField] private LayerMask arenaWallLayer;
        [SerializeField] private Camera mainCamera;

        private CharacterController controller;
        private HealthComponent health;
        private TrailRenderer dashTrail;

        private Vector3 moveInput;
        private Vector3 aimDirection = Vector3.forward;
        private Vector3 currentVelocity;
        private bool isDashing;
        private bool isGuarding;
        private bool isChargingShot;
        private float lastDashTime = -10f;
        private float lastMeleeTime = -10f;
        private float lastRangedTime = -10f;
        private float guardStartTime = -10f;
        private float stunEndTime = -10f;

        private Vector2 botMoveAxis;
        private Vector3 botAimPoint;
        private bool botGuardHeld;

        public event Action<Vector3> OnPlayerDash;
        public event Action OnPlayerMeleeAttack;
        public event Action OnPlayerRangedAttack;
        public event Action<bool> OnPlayerParryAttempt;

        public Vector3 Velocity => currentVelocity;
        public Vector3 AimDirection => aimDirection;
        public bool IsDashing => isDashing;
        public bool IsGuarding => isGuarding;
        public bool IsBotControlled { get => isBotControlled; set => isBotControlled = value; }

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            health = GetComponent<HealthComponent>();

            if (mainCamera == null)
            {
                mainCamera = Camera.main;
            }

            health.ParryEvaluator = EvaluateParry;
            health.GuardDamageMultiplier = EvaluateGuardMultiplier;
            health.OnPostureBroken += HandlePostureBroken;
            health.OnParriedSuccess += HandleParriedSuccessVFX;
            health.OnDeath += HandlePlayerDeath;

            if (shieldVisual != null)
            {
                shieldVisual.SetActive(false);
            }

            // Configura estela visual para el Dash
            dashTrail = CombatVFX.AttachTrail(
                transform,
                vfxMaterial,
                new Color(0.2f, 1f, 0.5f, 0.9f),
                new Color(0f, 0.8f, 1f, 0f),
                startWidth: 0.65f,
                trailTime: 0.22f,
                startEmitting: false
            );
        }

        private void Update()
        {
            if (health.IsDead) return;

            if (Time.time < stunEndTime)
            {
                currentVelocity = Vector3.zero;
                return;
            }

            if (!isBotControlled)
            {
                ReadHumanInputs();
            }
            else
            {
                ApplyBotInputs();
            }

            HandleMovementAndRotation();
        }

        private void ReadHumanInputs()
        {
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            float h = 0f;
            float v = 0f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) h -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) h += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v += 1f;

            moveInput = new Vector3(h, 0f, v).normalized;

            if (mainCamera != null)
            {
                Vector2 screenPos = mouse.position.ReadValue();
                Ray ray = mainCamera.ScreenPointToRay(screenPos);
                Plane arenaPlane = new Plane(Vector3.up, new Vector3(0f, transform.position.y + 1f, 0f));

                if (arenaPlane.Raycast(ray, out float enter))
                {
                    Vector3 worldHit = ray.GetPoint(enter);
                    Vector3 flatDir = worldHit - transform.position;
                    flatDir.y = 0f;
                    if (flatDir.sqrMagnitude > 0.01f)
                    {
                        aimDirection = flatDir.normalized;
                    }
                }
            }

            SetGuardState(mouse.rightButton.isPressed);

            if (kb.spaceKey.wasPressedThisFrame)
            {
                TryDash();
            }

            if (mouse.leftButton.wasPressedThisFrame)
            {
                TryMeleeAttack();
            }

            if (kb.eKey.wasPressedThisFrame || kb.qKey.wasPressedThisFrame)
            {
                TryRangedAttack();
            }
        }

        private void ApplyBotInputs()
        {
            moveInput = new Vector3(botMoveAxis.x, 0f, botMoveAxis.y).normalized;
            Vector3 toTarget = botAimPoint - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.01f)
            {
                aimDirection = toTarget.normalized;
            }
            SetGuardState(botGuardHeld);
        }

        private void HandleMovementAndRotation()
        {
            if (isDashing) return;

            if (aimDirection.sqrMagnitude > 0.001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(aimDirection);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationSpeed * Time.deltaTime);
            }

            float speedMultiplier = 1f;
            if (isGuarding) speedMultiplier = 0.45f;
            else if (isChargingShot) speedMultiplier = 0.35f;

            Vector3 velocity = moveInput * (moveSpeed * speedMultiplier);
            velocity.y = -9.81f;

            controller.Move(velocity * Time.deltaTime);
            currentVelocity = new Vector3(velocity.x, 0f, velocity.z);
        }

        public bool TryDash()
        {
            if (isDashing || Time.time - lastDashTime < dashCooldown || Time.time < stunEndTime)
                return false;

            Vector3 dashDir = moveInput.sqrMagnitude > 0.01f ? moveInput : aimDirection;
            StartCoroutine(DashRoutine(dashDir.normalized));
            return true;
        }

        private IEnumerator DashRoutine(Vector3 dashDir)
        {
            isDashing = true;
            SetGuardState(false);
            lastDashTime = Time.time;

            health.SetInvulnerable(true);
            if (bodyRenderer != null && dashBodyMaterial != null)
            {
                bodyRenderer.material = dashBodyMaterial;
            }

            if (dashTrail != null) dashTrail.emitting = true;
            CombatVFX.SpawnImpactBurst(
                transform.position + Vector3.up * 0.8f,
                vfxMaterial,
                new Color(0.2f, 1f, 0.5f, 1f),
                new Color(0f, 0.85f, 1f, 0f),
                count: 14,
                maxSpeed: 7f,
                size: 0.22f
            );

            OnPlayerDash?.Invoke(dashDir);

            float elapsed = 0f;
            while (elapsed < dashDuration)
            {
                Vector3 step = dashDir * dashSpeed;
                step.y = -9.81f;
                controller.Move(step * Time.deltaTime);
                currentVelocity = dashDir * dashSpeed;

                elapsed += Time.deltaTime;
                yield return null;
            }

            if (dashTrail != null) dashTrail.emitting = false;
            health.SetInvulnerable(false);
            if (bodyRenderer != null && normalBodyMaterial != null)
            {
                bodyRenderer.material = normalBodyMaterial;
            }

            isDashing = false;
        }

        public bool TryMeleeAttack()
        {
            if (isDashing || isGuarding || isChargingShot || Time.time - lastMeleeTime < meleeCooldown || Time.time < stunEndTime)
                return false;

            lastMeleeTime = Time.time;
            OnPlayerMeleeAttack?.Invoke();

            StartCoroutine(SwingWeaponVisualRoutine());

            Vector3 hitCenter = transform.position + Vector3.up * 1.0f + aimDirection * (meleeRange * 0.65f);
            Collider[] hits = Physics.OverlapSphere(hitCenter, meleeRadius, bossLayer);

            for (int i = 0; i < hits.Length; i++)
            {
                HealthComponent bossHealth = hits[i].GetComponentInParent<HealthComponent>();
                if (bossHealth != null)
                {
                    DamageInfo info = new DamageInfo
                    {
                        hpDamage = meleeDamage,
                        postureDamage = meleePostureDamage,
                        hitPoint = hitCenter,
                        hitDirection = aimDirection,
                        attacker = gameObject,
                        isProjectile = false,
                        canBeParried = false
                    };
                    bossHealth.TakeDamage(info);

                    CombatVFX.SpawnImpactBurst(
                        hitCenter,
                        vfxMaterial,
                        new Color(0.2f, 1f, 0.5f, 1f),
                        new Color(1f, 1f, 1f, 0f),
                        count: 12,
                        maxSpeed: 6f,
                        size: 0.2f
                    );
                    break;
                }
            }

            return true;
        }

        private IEnumerator SwingWeaponVisualRoutine()
        {
            if (weaponPivot == null) yield break;

            float duration = 0.18f;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float t = elapsed / duration;
                float angle = Mathf.Lerp(65f, -75f, t);
                weaponPivot.localRotation = Quaternion.Euler(0f, angle, 0f);
                elapsed += Time.deltaTime;
                yield return null;
            }
            weaponPivot.localRotation = Quaternion.identity;
        }

        public bool TryRangedAttack()
        {
            if (isDashing || isGuarding || isChargingShot || Time.time - lastRangedTime < rangedCooldown || Time.time < stunEndTime)
                return false;

            lastRangedTime = Time.time;
            OnPlayerRangedAttack?.Invoke();
            StartCoroutine(ChargedShotRoutine());
            return true;
        }

        private IEnumerator ChargedShotRoutine()
        {
            isChargingShot = true;
            yield return new WaitForSeconds(rangedChargeTime);
            isChargingShot = false;

            if (health.IsDead || isDashing) yield break;

            if (projectilePrefab != null)
            {
                Vector3 spawnPos = firePoint != null
                    ? firePoint.position
                    : transform.position + Vector3.up * 1.1f + aimDirection * 0.9f;

                GameObject projObj = Instantiate(projectilePrefab, spawnPos, Quaternion.LookRotation(aimDirection));
                CombatVFX.AttachTrail(
                projObj.transform,
                vfxMaterial,
                    new Color(0.35f, 1f, 0.2f, 1f),
                    new Color(0f, 0.95f, 1f, 0f),
                    startWidth: 0.34f,
                    trailTime: 0.18f
                );

                Projectile proj = projObj.GetComponent<Projectile>();
                if (proj != null)
                {
                    proj.Initialize(
                        aimDirection,
                        projectileSpeed,
                        rangedDamage,
                        rangedPostureDamage,
                        gameObject,
                        bossLayer,
                        arenaWallLayer,
                        vfxMaterial
                    );
                }
            }
        }

        public void SetGuardState(bool active)
        {
            if (isDashing || isChargingShot) active = false;

            if (active && !isGuarding)
            {
                guardStartTime = Time.time;
                OnPlayerParryAttempt?.Invoke(false);
            }

            isGuarding = active;
            if (shieldVisual != null)
            {
                shieldVisual.SetActive(isGuarding);

                bool inParryWindow = isGuarding && (Time.time - guardStartTime <= perfectParryWindow);
                shieldVisual.transform.localScale = inParryWindow
                    ? new Vector3(0.95f, 0.95f, 0.16f)
                    : new Vector3(0.75f, 0.85f, 0.12f);
            }
        }

        private bool EvaluateParry(DamageInfo info)
        {
            if (!isGuarding) return false;

            bool isPerfect = (Time.time - guardStartTime) <= perfectParryWindow;
            if (isPerfect)
            {
                OnPlayerParryAttempt?.Invoke(true);

                if (!info.isProjectile && info.attacker != null)
                {
                    HealthComponent attackerHealth = info.attacker.GetComponent<HealthComponent>();
                    if (attackerHealth != null)
                    {
                        attackerHealth.AddPostureDamage(parryStaggerToAttacker);
                    }
                }
                return true;
            }

            return false;
        }

        // Genera chispas brillantes frente al escudo cuando logra un Parry perfecto
        private void HandleParriedSuccessVFX(DamageInfo info)
        {
            Vector3 sparkPos = shieldVisual != null
                ? shieldVisual.transform.position
                : transform.position + Vector3.up * 1.1f + aimDirection * 0.6f;

            CombatVFX.SpawnParrySparks(sparkPos, vfxMaterial);
        }

        private float EvaluateGuardMultiplier(DamageInfo info)
        {
            return isGuarding ? (1f - guardDamageReduction) : 1f;
        }

        private void HandlePostureBroken()
        {
            StopAllCoroutines();
            isDashing = false;
            isChargingShot = false;
            SetGuardState(false);

            if (dashTrail != null) dashTrail.emitting = false;
            health.SetInvulnerable(false);

            stunEndTime = Time.time + staggerDuration;
            StartCoroutine(PlayerStaggerRoutine());
        }

        private IEnumerator PlayerStaggerRoutine()
        {
            // 1. Estallido inicial de chispas al romperse la guardia/postura
            CombatVFX.SpawnImpactBurst(
                transform.position + Vector3.up * 1.1f,
                vfxMaterial,
                new Color(0.6f, 0.95f, 1f, 1f),
                new Color(1f, 1f, 1f, 0f),
                count: 24,
                maxSpeed: 8.5f,
                size: 0.25f
            );

            // 2. Bucle visual durante el aturdimiento: cuerpo pálido parpadeante + inclinación y temblor
            float elapsed = 0f;
            Color paleColor = new Color(0.55f, 0.65f, 0.72f, 1f);
            Color flickerColor = new Color(0.88f, 0.95f, 1f, 1f);

            while (elapsed < staggerDuration && !health.IsDead)
            {
                float pulse = Mathf.PingPong(elapsed * 14f, 1f);

                if (bodyRenderer != null)
                {
                    Color currentTint = Color.Lerp(paleColor, flickerColor, pulse);
                    bodyRenderer.material.color = currentTint;
                    bodyRenderer.material.SetColor("_EmissionColor", currentTint * Mathf.Lerp(0.10f, 0.75f, pulse));
                }

                if (visualRoot != null)
                {
                    float shakeX = Mathf.Sin(elapsed * 55f) * 0.05f;
                    visualRoot.localPosition = new Vector3(shakeX, -0.15f, 0f);
                    visualRoot.localRotation = Quaternion.Euler(26f, 0f, 0f);
                }

                elapsed += Time.deltaTime;
                yield return null;
            }

            // 3. Restaurar postura y material normal al recuperarse
            if (!health.IsDead)
            {
                if (visualRoot != null)
                {
                    visualRoot.localPosition = Vector3.zero;
                    visualRoot.localRotation = Quaternion.identity;
                }

                if (bodyRenderer != null && normalBodyMaterial != null)
                {
                    bodyRenderer.material = normalBodyMaterial;
                }
            }
        }

        private void HandlePlayerDeath()
        {
            StopAllCoroutines();
            isDashing = false;
            isGuarding = false;
            isChargingShot = false;

            if (dashTrail != null) dashTrail.emitting = false;
            if (shieldVisual != null) shieldVisual.SetActive(false);

            CombatVFX.SpawnImpactBurst(
                transform.position + Vector3.up * 1.0f,
                vfxMaterial,
                new Color(0.8f, 0.95f, 1f, 1f),
                new Color(1f, 1f, 1f, 0f),
                count: 28,
                maxSpeed: 10f,
                size: 0.28f
            );

            // Cuerpo pálido apagado
            if (bodyRenderer != null)
            {
                bodyRenderer.material.color = new Color(0.75f, 0.78f, 0.82f, 1f);
                bodyRenderer.material.SetColor("_EmissionColor", Color.black);
            }

            // Personaje desplomado en el suelo
            if (visualRoot != null)
            {
                visualRoot.localPosition = new Vector3(0f, -0.35f, 0.2f);
                visualRoot.localRotation = Quaternion.Euler(68f, 0f, -15f);
            }
        }

        public void SetBotControls(Vector2 moveAxis, Vector3 aimWorldPoint, bool guardHeld)
        {
            botMoveAxis = moveAxis;
            botAimPoint = aimWorldPoint;
            botGuardHeld = guardHeld;
        }

        public void ResetPlayerState(Vector3 localStartPos)
        {
            StopAllCoroutines();
            isDashing = false;
            isGuarding = false;
            isChargingShot = false;
            stunEndTime = -10f;
            lastDashTime = -10f;
            lastMeleeTime = -10f;
            lastRangedTime = -10f;

            if (dashTrail != null) dashTrail.emitting = false;
            if (shieldVisual != null) shieldVisual.SetActive(false);
            if (bodyRenderer != null && normalBodyMaterial != null) bodyRenderer.material = normalBodyMaterial;
            if (visualRoot != null)
            {
                visualRoot.localPosition = Vector3.zero;
                visualRoot.localRotation = Quaternion.identity;
            }

            controller.enabled = false;
            transform.localPosition = localStartPos;
            transform.localRotation = Quaternion.identity;
            controller.enabled = true;

            health.ResetStats();
        }

        private void LateUpdate()
        {
            Vector3 localPos = transform.localPosition;
            Vector2 flat = new Vector2(localPos.x, localPos.z);
            const float maxArenaRadius = 5.3f;

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