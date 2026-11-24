using System;
using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using Chimera.Core;

namespace Chimera.Player
{
    /// Controlador completo del Jugador en 3D (vista táctica).
    /// Soporta tanto input humano (Teclado + Mouse) como control virtual por Bots de entrenamiento.
    /// Emite eventos limpios para alimentar al PlayerBehaviorProfiler (Capa 1 de IA).
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
        [SerializeField] private float guardDamageReduction = 0.70f; // Recibe solo 30% de daño al bloquear normal
        [SerializeField] private float parryStaggerToAttacker = 35f;

        [Header("Referencias Visuales (Primitivas)")]
        [SerializeField] private Transform weaponPivot;
        [SerializeField] private GameObject shieldVisual;
        [SerializeField] private Renderer bodyRenderer;
        [SerializeField] private Material normalBodyMaterial;
        [SerializeField] private Material dashBodyMaterial;

        [Header("Capas y Referencias de Escena")]
        [SerializeField] private LayerMask bossLayer;
        [SerializeField] private LayerMask arenaWallLayer;
        [SerializeField] private Camera mainCamera;

        private CharacterController controller;
        private HealthComponent health;

        // Estado interno
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

        // Inputs virtuales (usados cuando isBotControlled == true)
        private Vector2 botMoveAxis;
        private Vector3 botAimPoint;
        private bool botGuardHeld;

        // Eventos de Telemetría para PlayerBehaviorProfiler (Capa 1)
        public event Action<Vector3> OnPlayerDash;          // Dirección del dash
        public event Action OnPlayerMeleeAttack;
        public event Action OnPlayerRangedAttack;
        public event Action<bool> OnPlayerParryAttempt;     // true = parry exitoso, false = solo levantó escudo

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

            // Conectar reglas de Parry y Guardia al componente de salud
            health.ParryEvaluator = EvaluateParry;
            health.GuardDamageMultiplier = EvaluateGuardMultiplier;
            health.OnPostureBroken += HandlePostureBroken;

            if (shieldVisual != null)
            {
                shieldVisual.SetActive(false);
            }
        }

        private void Update()
        {
            if (health.IsDead) return;

            // Si tiene postura rota (aturdido), no puede actuar brevemente
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

        /// Lee entradas de teclado y mouse usando el nuevo Input System de Unity 6.
        private void ReadHumanInputs()
        {
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            // 1. Movimiento WASD / Flechas
            float h = 0f;
            float v = 0f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) h -= 1f;
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) h += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v += 1f;

            moveInput = new Vector3(h, 0f, v).normalized;

            // 2. Apuntado proyectando rayo del mouse sobre el plano horizontal Y = 1.0
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

            // 3. Guardia / Parry (Clic Derecho)
            bool rightPressed = mouse.rightButton.isPressed;
            SetGuardState(rightPressed);

            // 4. Dash Direccional (Barra Espaciadora)
            if (kb.spaceKey.wasPressedThisFrame)
            {
                TryDash();
            }

            // 5. Ataque Cuerpo a Cuerpo (Clic Izquierdo)
            if (mouse.leftButton.wasPressedThisFrame)
            {
                TryMeleeAttack();
            }

            // 6. Disparo a Distancia (Tecla E o Q)
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

            // Rotación suave hacia la dirección de apuntado
            if (aimDirection.sqrMagnitude > 0.001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(aimDirection);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationSpeed * Time.deltaTime);
            }

            // Penalización de velocidad si está bloqueando o cargando un disparo
            float speedMultiplier = 1f;
            if (isGuarding) speedMultiplier = 0.45f;
            else if (isChargingShot) speedMultiplier = 0.35f;

            Vector3 velocity = moveInput * (moveSpeed * speedMultiplier);
            velocity.y = -9.81f; // Gravedad simple para mantener pegado al suelo

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

            // Activa i-frames y feedback visual
            health.SetInvulnerable(true);
            if (bodyRenderer != null && dashBodyMaterial != null)
            {
                bodyRenderer.material = dashBodyMaterial;
            }

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

            // Detección de impacto en esfera frontal
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
                    break;
                }
            }

            return true;
        }

        private IEnumerator SwingWeaponVisualRoutine()
        {
            if (weaponPivot == null) yield break;

            // Barrido visual rápido de la espada de derecha a izquierda
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
                        arenaWallLayer
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

                // Durante la ventana de Parry perfecto el escudo se ve ligeramente más ancho/reactivo
                bool inParryWindow = isGuarding && (Time.time - guardStartTime <= perfectParryWindow);
                shieldVisual.transform.localScale = inParryWindow
                    ? new Vector3(0.95f, 0.95f, 0.16f)
                    : new Vector3(0.75f, 0.85f, 0.12f);
            }
        }

        /// Evalúa si un impacto entrante ocurre dentro de la ventana de Parry perfecto.
        private bool EvaluateParry(DamageInfo info)
        {
            if (!isGuarding) return false;

            bool isPerfect = (Time.time - guardStartTime) <= perfectParryWindow;
            if (isPerfect)
            {
                OnPlayerParryAttempt?.Invoke(true);

                // Si el golpe fue cuerpo a cuerpo del Jefe, le inflige alto daño de postura
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

        private float EvaluateGuardMultiplier(DamageInfo info)
        {
            return isGuarding ? (1f - guardDamageReduction) : 1f;
        }

        private void HandlePostureBroken()
        {
            SetGuardState(false);
            stunEndTime = Time.time + 1.0f;
        }

        /// Interfaz limpia para que los Bots de Entrenamiento (Fase 4) controlen al jugador.
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

            if (shieldVisual != null) shieldVisual.SetActive(false);
            if (bodyRenderer != null && normalBodyMaterial != null) bodyRenderer.material = normalBodyMaterial;

            controller.enabled = false;
            transform.localPosition = localStartPos;
            transform.localRotation = Quaternion.identity;
            controller.enabled = true;

            health.ResetStats();
        }
    }
}