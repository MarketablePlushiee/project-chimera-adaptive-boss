using System;
using UnityEngine;

namespace Chimera.Core
{
    /// Estructura de datos que encapsula un impacto de combate.
    /// Permite desacoplar quién ataca de quién recibe el daño (Patrón Command/Message).
    public struct DamageInfo
    {
        public float hpDamage;
        public float postureDamage;
        public Vector3 hitPoint;
        public Vector3 hitDirection;
        public GameObject attacker;
        public bool isProjectile;
        public bool canBeParried;
    }

    /// Componente reutilizable de Vida (HP), Postura (Stagger) e Invulnerabilidad (i-frames).
    /// Se utiliza tanto en el Jugador como en el Jefe para mantener consistencia de reglas.
    public class HealthComponent : MonoBehaviour
    {
        [Header("Estadísticas Base")]
        [SerializeField] private float maxHP = 100f;
        [SerializeField] private float maxPosture = 100f;
        [SerializeField] private float postureDecayPerSecond = 12f;
        [SerializeField] private float postureDecayDelay = 2.0f;

        [Header("Estado en Tiempo Real (Solo Lectura)")]
        [SerializeField] private float currentHP;
        [SerializeField] private float currentPosture;
        [SerializeField] private bool isInvulnerable;

        private float lastPostureHitTime;

        // Eventos para que la UI, la FSM del Jefe y la Telemetría reaccionen sin acoplamiento directo
        public event Action<float, float> OnHealthChanged;      // (actual, máximo)
        public event Action<float, float> OnPostureChanged;     // (actual, máximo)
        public event Action<DamageInfo> OnDamaged;
        public event Action<DamageInfo> OnParriedSuccess;       // Se dispara si el receptor hizo Parry
        public event Action OnPostureBroken;
        public event Action OnDeath;

        // Delegado opcional: si retorna true, el impacto fue detenido por un Parry perfecto
        public Func<DamageInfo, bool> ParryEvaluator;
        // Delegado opcional: retorna multiplicador de daño (ej. 0.3f si está bloqueando normal)
        public Func<DamageInfo, float> GuardDamageMultiplier;

        public float CurrentHP => currentHP;
        public float MaxHP => maxHP;
        public float NormalizedHP => maxHP > 0f ? Mathf.Clamp01(currentHP / maxHP) : 0f;

        public float CurrentPosture => currentPosture;
        public float MaxPosture => maxPosture;
        public float NormalizedPosture => maxPosture > 0f ? Mathf.Clamp01(currentPosture / maxPosture) : 0f;

        public bool IsInvulnerable => isInvulnerable;
        public bool IsDead => currentHP <= 0f;

        private void Awake()
        {
            ResetStats();
        }

        private void Update()
        {
            if (IsDead) return;

            // Regeneración pasiva de postura si no ha recibido golpes recientemente
            if (currentPosture > 0f && Time.time - lastPostureHitTime >= postureDecayDelay)
            {
                currentPosture = Mathf.Max(0f, currentPosture - postureDecayPerSecond * Time.deltaTime);
                OnPostureChanged?.Invoke(currentPosture, maxPosture);
            }
        }

        /// Restaura HP y Postura al máximo (crucial al reiniciar episodios en ML-Agents).
        public void ResetStats()
        {
            currentHP = maxHP;
            currentPosture = 0f;
            isInvulnerable = false;
            OnHealthChanged?.Invoke(currentHP, maxHP);
            OnPostureChanged?.Invoke(currentPosture, maxPosture);
        }

        public void SetInvulnerable(bool value)
        {
            isInvulnerable = value;
        }

        /// Procesa un impacto entrante evaluando i-frames, Parry perfecto y Guardia normal.
        /// Retorna true si el ataque conectó (o fue bloqueado/parreado) y false si fue esquivado por i-frames.
        public bool TakeDamage(DamageInfo info)
        {
            if (IsDead) return false;

            // 1. Invulnerabilidad activa (Dash i-frames) -> esquive limpio
            if (isInvulnerable)
            {
                return false;
            }

            // 2. Evaluación de Parry Perfecto
            if (info.canBeParried && ParryEvaluator != null && ParryEvaluator(info))
            {
                OnParriedSuccess?.Invoke(info);
                return true;
            }

            // 3. Evaluación de Guardia Normal (reducción de daño)
            float dmgMultiplier = 1f;
            if (GuardDamageMultiplier != null)
            {
                dmgMultiplier = GuardDamageMultiplier(info);
            }

            float finalHpDamage = info.hpDamage * dmgMultiplier;
            float finalPostureDamage = info.postureDamage * (dmgMultiplier < 1f ? 1.25f : 1f);

            currentHP = Mathf.Max(0f, currentHP - finalHpDamage);
            AddPostureDamage(finalPostureDamage);

            OnHealthChanged?.Invoke(currentHP, maxHP);
            OnDamaged?.Invoke(info);

            if (currentHP <= 0f)
            {
                OnDeath?.Invoke();
            }

            return true;
        }

        /// Incrementa la barra de postura (usado tanto por golpes como por penalización al ser parreado).
        public void AddPostureDamage(float amount)
        {
            if (IsDead || amount <= 0f) return;

            lastPostureHitTime = Time.time;
            currentPosture = Mathf.Min(maxPosture, currentPosture + amount);
            OnPostureChanged?.Invoke(currentPosture, maxPosture);

            if (currentPosture >= maxPosture)
            {
                currentPosture = 0f;
                OnPostureChanged?.Invoke(currentPosture, maxPosture);
                OnPostureBroken?.Invoke();
            }
        }
    }
}