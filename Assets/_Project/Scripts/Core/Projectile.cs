using UnityEngine;

namespace Chimera.Core
{
    /// Proyectil cinemático ligero y determinista.
    /// Soporta reflexión automática cuando es interceptado por un Parry perfecto del jugador.
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
        private float spawnTime;
        private bool isInitialized;

        /// Inicializa el proyectil inmediatamente después de instanciarlo.
        public void Initialize(
            Vector3 shootDirection,
            float projectileSpeed,
            float damageHP,
            float damagePosture,
            GameObject shooter,
            LayerMask targets,
            LayerMask walls)
        {
            direction = new Vector3(shootDirection.x, 0f, shootDirection.z).normalized;
            if (direction.sqrMagnitude < 0.001f) direction = transform.forward;

            speed = projectileSpeed;
            hpDamage = damageHP;
            postureDamage = damagePosture;
            owner = shooter;
            targetMask = targets;
            wallMask = walls;
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

            // 1. Chequeo de impacto contra muros de la arena mediante SphereCast
            if (Physics.SphereCast(transform.position, hitRadius * 0.6f, direction, out RaycastHit wallHit, step, wallMask))
            {
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

                    // Si el objetivo tenía Parry activo, reflejamos el proyectil hacia el atacante original
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
                        Destroy(gameObject);
                        return;
                    }
                }
            }

            transform.position = nextPos;
        }

        /// Invierte la trayectoria del proyectil hacia quien lo disparó con bonificación de velocidad y postura.
        private void ReflectToward(GameObject originalShooter, GameObject parrier)
        {
            Vector3 toShooter = (originalShooter.transform.position - transform.position);
            toShooter.y = 0f;
            direction = toShooter.sqrMagnitude > 0.01f ? toShooter.normalized : -direction;

            // Intercambiamos dueño y máscara de objetivo para que ahora dañe al Jefe
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