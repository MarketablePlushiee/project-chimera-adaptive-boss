using System.Collections;
using UnityEngine;

namespace Chimera.Core
{
    // Utilidad estática para generar feedback visual procedural en URP (partículas, ondas y trails)
    // sin depender de prefabs externos y manteniendo un costo mínimo en CPU/GPU.
    public static class CombatVFX
    {
        // Agrega y configura un TrailRenderer limpio sobre cualquier objeto (Dash, Embestida, Proyectiles)
        public static TrailRenderer AttachTrail(
            Transform target,
            Material vfxMaterial,
            Color startColor,
            Color endColor,
            float startWidth,
            float trailTime,
            bool startEmitting = true)
        {
            if (target == null) return null;

            TrailRenderer trail = target.gameObject.GetComponent<TrailRenderer>();
            if (trail == null)
            {
                trail = target.gameObject.AddComponent<TrailRenderer>();
            }

            trail.material = vfxMaterial;
            trail.time = trailTime;
            trail.startWidth = startWidth;
            trail.endWidth = 0.02f;
            trail.minVertexDistance = 0.08f;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            trail.receiveShadows = false;
            trail.emitting = startEmitting;

            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(startColor, 0f),
                    new GradientColorKey(endColor, 1f)
                },
                new[]
                {
                    new GradientAlphaKey(startColor.a, 0f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            trail.colorGradient = gradient;

            return trail;
        }

        // Genera una explosión radial de chispas doradas/cian cuando ocurre un Parry perfecto
        public static void SpawnParrySparks(Vector3 position, Material vfxMaterial)
        {
            SpawnBurst(
                position,
                vfxMaterial,
                colorStart: new Color(1f, 0.92f, 0.35f, 1f),
                colorEnd: new Color(0f, 0.95f, 1f, 0f),
                count: 26,
                speedMin: 5f,
                speedMax: 12f,
                sizeStart: 0.22f,
                lifetime: 0.38f,
                gravity: 1.2f,
                shapeType: ParticleSystemShapeType.Sphere,
                shapeRadius: 0.15f
            );
        }

        // Genera una explosión de partículas para impactos de martillo, cañón o arranque de dash
        public static void SpawnImpactBurst(
            Vector3 position,
            Material vfxMaterial,
            Color colorStart,
            Color colorEnd,
            int count = 20,
            float maxSpeed = 9f,
            float size = 0.28f)
        {
            SpawnBurst(
                position,
                vfxMaterial,
                colorStart,
                colorEnd,
                count,
                speedMin: maxSpeed * 0.4f,
                speedMax: maxSpeed,
                sizeStart: size,
                lifetime: 0.42f,
                gravity: 0.4f,
                shapeType: ParticleSystemShapeType.Sphere,
                shapeRadius: 0.25f
            );
        }

        // Genera una onda expansiva visual desde el centro del Jefe hasta el radio exterior del golpe
        public static void SpawnShockwaveWave(Vector3 center, float targetRadius, float duration, Material vfxMaterial)
        {
            // 1. Anillo de partículas rasantes que viajan hacia afuera a la velocidad exacta de la onda
            float particleSpeed = targetRadius / Mathf.Max(0.1f, duration);
            SpawnBurst(
                center + Vector3.up * 0.15f,
                vfxMaterial,
                colorStart: new Color(1f, 0.35f, 0.05f, 1f),
                colorEnd: new Color(1f, 0.08f, 0.25f, 0f),
                count: 42,
                speedMin: particleSpeed * 0.92f,
                speedMax: particleSpeed * 1.05f,
                sizeStart: 0.34f,
                lifetime: duration,
                gravity: 0f,
                shapeType: ParticleSystemShapeType.Circle,
                shapeRadius: 0.35f
            );

            // 2. Disco/aro expansivo que crece desde el centro hasta el borde del ataque
            GameObject waveObj = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            waveObj.name = "VFX_ShockwaveRing";
            Object.Destroy(waveObj.GetComponent<Collider>());

            waveObj.transform.position = new Vector3(center.x, 0.06f, center.z);
            waveObj.transform.localScale = new Vector3(0.6f, 0.015f, 0.6f);

            Renderer rend = waveObj.GetComponent<Renderer>();
            if (rend != null && vfxMaterial != null)
            {
                rend.material = vfxMaterial;
                rend.material.color = new Color(1f, 0.25f, 0.1f, 0.75f);
                rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }

            ShockwaveRunner runner = waveObj.AddComponent<ShockwaveRunner>();
            runner.Run(targetRadius * 2f, duration);
        }

        private static void SpawnBurst(
            Vector3 position,
            Material vfxMaterial,
            Color colorStart,
            Color colorEnd,
            int count,
            float speedMin,
            float speedMax,
            float sizeStart,
            float lifetime,
            float gravity,
            ParticleSystemShapeType shapeType,
            float shapeRadius)
        {
            GameObject go = new GameObject("VFX_Burst");
            go.transform.position = position;

            // Orienta el círculo paralelo al suelo si el shape es Circle
            if (shapeType == ParticleSystemShapeType.Circle)
            {
                go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            }

            ParticleSystem ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.playOnAwake = false;
            main.duration = lifetime;
            main.loop = false;
            main.startLifetime = lifetime;
            main.startSpeed = new ParticleSystem.MinMaxCurve(speedMin, speedMax);
            main.startSize = sizeStart;
            main.startColor = colorStart;
            main.gravityModifier = gravity;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = shapeType;
            shape.radius = shapeRadius;

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(colorStart, 0f), new GradientColorKey(colorEnd, 1f) },
                new[] { new GradientAlphaKey(colorStart.a, 0f), new GradientAlphaKey(0f, 1f) }
            );
            colorOverLifetime.color = grad;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.1f));

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Billboard;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (vfxMaterial != null)
            {
                renderer.material = vfxMaterial;
            }

            ps.Play();
            Object.Destroy(go, lifetime + 0.15f);
        }

        // Componente auxiliar interno que anima la escala del anillo expansivo y lo destruye al terminar
        private class ShockwaveRunner : MonoBehaviour
        {
            public void Run(float targetDiameter, float duration)
            {
                StartCoroutine(ExpandRoutine(targetDiameter, duration));
            }

            private IEnumerator ExpandRoutine(float targetDiameter, float duration)
            {
                Renderer rend = GetComponent<Renderer>();
                Color initialColor = rend != null ? rend.material.color : Color.red;
                float elapsed = 0f;

                while (elapsed < duration)
                {
                    float t = elapsed / duration;
                    float currentDiameter = Mathf.Lerp(0.6f, targetDiameter, t);
                    transform.localScale = new Vector3(currentDiameter, 0.015f, currentDiameter);

                    if (rend != null)
                    {
                        Color c = initialColor;
                        c.a = Mathf.Lerp(0.75f, 0f, t);
                        rend.material.color = c;
                    }

                    elapsed += Time.deltaTime;
                    yield return null;
                }

                Destroy(gameObject);
            }
        }
    }
}