using UnityEngine;

namespace Chimera.Core
{
    // Proyectil cinemático ligero y determinista.
    // Soporta reflexión automática con Parry perfecto y explosión de partículas al impactar.
    public class Projectile : MonoBehaviour
    {
        [Header("Parámetros de Vuelo")]
        [SerializeField] private float speed = 18f;
        [SerializeField] private float maxLifetime = 3.5f;
        [SerializeField] private float hitRadius = 0.35f;

        private Vector3 direction;
        private float hpDamage;
        private float postureDamage;
        private GameObject owner;
        private LayerMask targetMask;
        private LayerMask wallMask;
        private Material vfxMaterial;
        private float spawnTime;
        private bool isInitialized;

        // Inicializa el proyectil inmediatamente después de instanciarlo
        public void Initialize(
            Vector3 shootDirection,
            float projectileSpeed,
            float damageHP,
            float damagePosture,
            GameObject shooter,
            LayerMask targets,
            LayerMask walls,
            Material impactVfxMaterial = null)
        {
            direction = new Vector3(shootDirection.x, 0f, shootDirection.z).normalized;
            if (direction.sqrMagnitude < 0.001f) direction = transform.forward;

            speed = projectileSpeed;
            hpDamage = damageHP;
            postureDamage = damagePosture;
            owner = shooter;
            targetMask = targets;
            wallMask = walls;
            vfxMaterial = impactVfxMaterial;
            spawnTime = Time.time;
            isInitialized = true;

            transform.rotation = Quaternion.LookRotation(direction);
        }

        private void Update()
        {
            if (!isInitialized) return;

            if (Time.time - spawnTime >= maxLifetime)
            {
                Destroy(gameObject);
                return;
            }

            float step = speed * Time.deltaTime;
            Vector3 nextPos = transform.position + direction * step;

            // 1. Chequeo de impacto contra muros de la arena
            if (Physics.SphereCast(transform.position, hitRadius * 0.6f, direction, out RaycastHit wallHit, step, wallMask))
            {
                SpawnHitParticles(wallHit.point, isWallHit: true);
                Destroy(gameObject);
                return;
            }

            // 2. Chequeo de impacto contra entidades objetivo (Jugador o Jefe)
            Collider[] hits = Physics.OverlapSphere(nextPos, hitRadius, targetMask);
            for (int i = 0; i < hits.Length; i++)
            {
                GameObject hitRoot = hits[i].transform.root == hits[i].transform
                    ? hits[i].gameObject
                    : hits[i].GetComponentInParent<HealthComponent>()?.gameObject;

                if (hitRoot == null || hitRoot == owner) continue;

                HealthComponent targetHealth = hitRoot.GetComponent<HealthComponent>();
                if (targetHealth != null)
                {
                    DamageInfo info = new DamageInfo
                    {
                        hpDamage = hpDamage,
                        postureDamage = postureDamage,
                        hitPoint = nextPos,
                        hitDirection = direction,
                        attacker = owner,
                        isProjectile = true,
                        canBeParried = true
                    };

                    bool wasParried = false;
                    void OnParryHandler(DamageInfo _) => wasParried = true;

                    targetHealth.OnParriedSuccess += OnParryHandler;
                    bool connected = targetHealth.TakeDamage(info);
                    targetHealth.OnParriedSuccess -= OnParryHandler;

                    if (wasParried && owner != null)
                    {
                        ReflectToward(owner, hitRoot);
                        return;
                    }

                    if (connected)
                    {
                        SpawnHitParticles(nextPos, isWallHit: false);
                        Destroy(gameObject);
                        return;
                    }
                }
            }

            transform.position = nextPos;
        }

        private void SpawnHitParticles(Vector3 pos, bool isWallHit)
        {
            if (vfxMaterial == null) return;

            bool isPlayerShot = owner != null && owner.CompareTag("Player");
            Color startCol = isPlayerShot
                ? new Color(0.3f, 1f, 0.35f, 1f)
                : new Color(1f, 0.45f, 0.05f, 1f);
            Color endCol = isPlayerShot
                ? new Color(0f, 0.9f, 1f, 0f)
                : new Color(1f, 0.1f, 0.2f, 0f);

            CombatVFX.SpawnImpactBurst(
                pos,
                vfxMaterial,
                startCol,
                endCol,
                count: isWallHit ? 8 : 18,
                maxSpeed: isWallHit ? 5f : 9f,
                size: isWallHit ? 0.16f : 0.26f
            );
        }

        // Invierte la trayectoria del proyectil hacia quien lo disparó con bonificación
        private void ReflectToward(GameObject originalShooter, GameObject parrier)
        {
            Vector3 toShooter = (originalShooter.transform.position - transform.position);
            toShooter.y = 0f;
            direction = toShooter.sqrMagnitude > 0.01f ? toShooter.normalized : -direction;

            targetMask = 1 << originalShooter.layer;
            owner = parrier;
            speed *= 1.45f;
            hpDamage *= 1.3f;
            postureDamage += 25f;
            spawnTime = Time.time;

            transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}